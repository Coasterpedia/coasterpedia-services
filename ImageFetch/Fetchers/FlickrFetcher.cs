using CoasterpediaServices.ImageFetch.Clients.Flickr;
using CoasterpediaServices.ImageFetch.Options;
using Microsoft.Extensions.Options;

namespace CoasterpediaServices.ImageFetch.Fetchers;

public class FlickrFetcher : ISourceFetcher
{
    private readonly IFlickrClient _flickrClient;
    private readonly FlickrConfig _config;
    private readonly HttpClient _downloadClient;
    private readonly FlickrLicenseCache _licenseCache;

    public FlickrFetcher(IFlickrClient flickrClient, IOptions<FlickrConfig> config, HttpClient downloadClient, FlickrLicenseCache licenseCache)
    {
        _flickrClient = flickrClient;
        _config = config.Value;
        _downloadClient = downloadClient;
        _licenseCache = licenseCache;
    }

    public bool CanHandle(Uri uri) => uri.Host is "www.flickr.com" or "flickr.com";

    public async Task<FetchResult> FetchAsync(Uri uri, CancellationToken cancellationToken)
    {
        var photoId = ExtractPhotoId(uri);

        var infoEnvelope = await _flickrClient.GetPhotoInfoAsync(_config.ApiKey, photoId);
        var photo = FlickrApiResult.EnsureOk(infoEnvelope.Stat, infoEnvelope.Message, infoEnvelope.Photo);

        if (!int.TryParse(photo.License, out var licenseId))
        {
            throw new ImageFetchException(422, "Unrecognised Flickr licence.");
        }

        var licenseName = await _licenseCache.GetLicenseNameAsync(licenseId, cancellationToken)
                           ?? throw new ImageFetchException(422, "Unrecognised Flickr licence.");

        var template = FlickrLicenses.ToCommonsTemplate(licenseName)
                       ?? throw new ImageFetchException(422,
                           $"Flickr photo is not under a free licence ({licenseName}); it can't be imported.");

        var sizesEnvelope = await _flickrClient.GetPhotoSizesAsync(_config.ApiKey, photoId);
        var sizes = FlickrApiResult.EnsureOk(sizesEnvelope.Stat, sizesEnvelope.Message, sizesEnvelope.Sizes);
        var largest = sizes.Size.LastOrDefault()
                      ?? throw new ImageFetchException(502, "Flickr photo has no available sizes.");

        var bytes = await BoundedDownloader.DownloadAsync(_downloadClient, largest.Source, cancellationToken);
        var extension = Path.GetExtension(new Uri(largest.Source).AbsolutePath);
        var photoPageUrl = photo.Urls.Url.FirstOrDefault(u => u.Type == "photopage")?.Content
                           ?? $"https://www.flickr.com/photos/{photo.Owner.Nsid}/{photoId}/";

        return new FetchResult
        {
            Bytes = bytes,
            ContentType = MimeTypes.FromExtension(extension),
            SuggestedFileName = $"{photoId}{extension}",
            Title = string.IsNullOrWhiteSpace(photo.Title?.Content) ? photoId : photo.Title!.Content,
            Author = string.IsNullOrWhiteSpace(photo.Owner.Realname) ? photo.Owner.Username : photo.Owner.Realname,
            SourceUrl = photoPageUrl,
            License = licenseName,
            AdditionalLicenseWikitext = template,
            Date = photo.Dates.Taken,
            Latitude = photo.Location?.Latitude,
            Longitude = photo.Location?.Longitude
        };
    }

    private static string ExtractPhotoId(Uri uri)
    {
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var photosIndex = Array.IndexOf(segments, "photos");
        if (photosIndex >= 0 && photosIndex + 2 < segments.Length)
        {
            return segments[photosIndex + 2];
        }

        throw new ImageFetchException(400,
            "Unrecognised Flickr URL, please link to a photo page (flickr.com/photos/<user>/<id>).");
    }
}
