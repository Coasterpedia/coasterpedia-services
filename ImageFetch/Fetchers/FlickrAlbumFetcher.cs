using CoasterpediaServices.ImageFetch.Clients.Flickr;
using CoasterpediaServices.ImageFetch.Options;
using Microsoft.Extensions.Options;

namespace CoasterpediaServices.ImageFetch.Fetchers;

public class FlickrAlbumFetcher : ICollectionFetcher
{
    private const int MaxItems = 500;

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

        var envelope = await _flickrClient.GetPhotosetPhotosAsync(_config.ApiKey, photosetId, MaxItems);
        var photoset = FlickrApiResult.EnsureOk(envelope.Stat, envelope.Message, envelope.Photoset);
        userSegment ??= photoset.Owner;

        var items = new List<CollectionItem>();
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
