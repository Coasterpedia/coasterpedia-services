using CoasterpediaServices.ImageFetch.Clients.Flickr;
using CoasterpediaServices.ImageFetch.Options;
using Microsoft.Extensions.Options;

namespace CoasterpediaServices.ImageFetch.Fetchers;

public class FlickrAlbumFetcher : ICollectionFetcher
{
    // Usable (free-licensed) photos handed back to the client. 1,500 is where the
    // cull grid was measured to still toggle inside a frame; it degrades from
    // around 3,000, so this is the display ceiling rather than an API one.
    private const int MaxItems = 1500;
    // Flickr's own per_page ceiling - asking for more is silently clamped.
    private const int PageSize = 500;
    // Hard stop on how much of a huge album we're willing to scan looking for
    // MaxItems usable photos: an album that is mostly all-rights-reserved would
    // otherwise page on and on. 10 pages = 5,000 photos examined.
    private const int MaxPages = 10;

    private readonly IFlickrClient _flickrClient;
    private readonly FlickrConfig _config;
    private readonly FlickrLicenseCache _licenseCache;

    public FlickrAlbumFetcher(IFlickrClient flickrClient, IOptions<FlickrConfig> config, FlickrLicenseCache licenseCache)
    {
        _flickrClient = flickrClient;
        _config = config.Value;
        _licenseCache = licenseCache;
    }

    public bool CanHandle(Uri uri)
    {
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (uri.Host == "flic.kr")
        {
            return segments.Length == 2 && segments[0] == "s";
        }

        if (uri.Host is not ("www.flickr.com" or "flickr.com"))
        {
            return false;
        }

        var photosIndex = Array.IndexOf(segments, "photos");
        return photosIndex >= 0
               && photosIndex + 3 < segments.Length
               && segments[photosIndex + 2] is "albums" or "sets";
    }

    public async Task<IReadOnlyList<CollectionItem>> FetchAsync(Uri uri, CancellationToken cancellationToken)
    {
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        string photosetId;
        string? userSegment;
        if (uri.Host == "flic.kr")
        {
            photosetId = FlickrShortUrl.Decode(segments[1]);
            userSegment = null;
        }
        else
        {
            var photosIndex = Array.IndexOf(segments, "photos");
            userSegment = segments[photosIndex + 1];
            photosetId = segments[photosIndex + 3];
        }

        // The licence filter runs per photo, so capping the FETCH at MaxItems would
        // cap the wrong thing: a 1,500-photo album whose free-licensed shots are
        // spread throughout would surface only those inside the first page, with no
        // hint the rest existed. Page until MaxItems *usable* photos are in hand.
        var items = new List<CollectionItem>();
        for (var page = 1; page <= MaxPages && items.Count < MaxItems; page++)
        {
            var envelope = await _flickrClient.GetPhotosetPhotosAsync(
                _config.ApiKey, photosetId, PageSize, page, cancellationToken);
            var photoset = FlickrApiResult.EnsureOk(envelope.Stat, envelope.Message, envelope.Photoset);
            userSegment ??= photoset.Owner;

            foreach (var photo in photoset.Photo)
            {
                if (!await HasUsableLicenseAsync(photo.License, cancellationToken))
                {
                    continue;
                }

                items.Add(new CollectionItem(
                    $"https://www.flickr.com/photos/{userSegment}/{photo.Id}/",
                    string.IsNullOrWhiteSpace(photo.Title) ? null : photo.Title,
                    photo.UrlS));

                if (items.Count >= MaxItems)
                {
                    break;
                }
            }

            // A short page is the last page. Inferring it this way (rather than
            // reading the envelope's `pages`) keeps us off a field whose JSON type
            // Flickr is not consistent about across endpoints.
            if (photoset.Photo.Count < PageSize)
            {
                break;
            }
        }

        if (items.Count == 0)
        {
            throw new ImageFetchException(422, "None of the photos in this Flickr album are under a free licence.");
        }

        return items;
    }

    private async Task<bool> HasUsableLicenseAsync(string license, CancellationToken cancellationToken)
    {
        if (!int.TryParse(license, out var licenseId))
        {
            return false;
        }

        var licenseName = await _licenseCache.GetLicenseNameAsync(licenseId, cancellationToken);
        return licenseName != null && FlickrLicenses.ToSlug(licenseName) != null;
    }
}
