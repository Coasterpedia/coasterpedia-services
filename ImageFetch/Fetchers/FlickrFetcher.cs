using CoasterpediaServices.ImageFetch.Clients.Flickr;
using CoasterpediaServices.ImageFetch.Options;
using CoasterpediaServices.ImageFetch.Provenance;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoasterpediaServices.ImageFetch.Fetchers;

public class FlickrFetcher : ISourceFetcher
{
    private readonly IFlickrClient _flickrClient;
    private readonly FlickrConfig _config;
    private readonly HttpClient _downloadClient;
    private readonly FlickrLicenseCache _licenseCache;
    private readonly ILogger<FlickrFetcher> _logger;

    public FlickrFetcher(IFlickrClient flickrClient, IOptions<FlickrConfig> config, HttpClient downloadClient, FlickrLicenseCache licenseCache, ILogger<FlickrFetcher> logger)
    {
        _flickrClient = flickrClient;
        _config = config.Value;
        _downloadClient = downloadClient;
        _licenseCache = licenseCache;
        _logger = logger;
    }

    public bool CanHandle(Uri uri) => uri.Host is "www.flickr.com" or "flickr.com" or "flic.kr";

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

        var slug = FlickrLicenses.ToSlug(licenseName)
                   ?? throw new ImageFetchException(422,
                       $"Flickr photo is not under a free licence ({licenseName}); it can't be imported.");

        var sizesEnvelope = await _flickrClient.GetPhotoSizesAsync(_config.ApiKey, photoId);
        var sizes = FlickrApiResult.EnsureOk(sizesEnvelope.Stat, sizesEnvelope.Message, sizesEnvelope.Sizes);
        var largest = sizes.Size.LastOrDefault()
                      ?? throw new ImageFetchException(502, "Flickr photo has no available sizes.");

        // The largest size Flickr rendered itself, as the right-way-up reference for the
        // rotation fix-up below. getSizes is ordered smallest-first with "Original" last,
        // so this is the last non-Original entry - and where a photo exposes no original,
        // `largest` IS that entry, already correct, and the check below no-ops.
        var reference = sizes.Size.LastOrDefault(s => s.Label != "Original");

        var bytes = await BoundedDownloader.DownloadAsync(_downloadClient, largest.Source, cancellationToken);
        var extension = Path.GetExtension(new Uri(largest.Source).AbsolutePath);
        bytes = CorrectRotation(bytes, extension, photo.Rotation, reference);
        var photoPageUrl = photo.Urls.Url.FirstOrDefault(u => u.Type == "photopage")?.Content
                           ?? $"https://www.flickr.com/photos/{photo.Owner.Nsid}/{photoId}/";
        var provenance = ProvenanceBuilder.Build(SourceRegistry.Flickr, slug, photoPageUrl);

        return new FetchResult
        {
            Bytes = bytes,
            ContentType = MimeTypes.FromExtension(extension),
            SuggestedFileName = $"{photoId}{extension}",
            Title = string.IsNullOrWhiteSpace(photo.Title?.Content) ? photoId : photo.Title!.Content,
            Author = string.IsNullOrWhiteSpace(photo.Owner.Realname) ? photo.Owner.Username : photo.Owner.Realname,
            SourceUrl = photoPageUrl,
            Source = provenance.Source,
            License = provenance.License,
            Cards = provenance.Cards,
            Date = photo.Dates.Taken,
            Latitude = photo.Location?.Latitude,
            Longitude = photo.Location?.Longitude
        };
    }

    // Deliberately fail-soft. A photo whose rotation could not be corrected is still a
    // perfectly good import that the user can straighten with the ImageRotate gadget in
    // one click; refusing the fetch outright over it would be the worse trade. The log
    // line is what makes a systematic failure (a bad decode path, a missing native lib)
    // visible rather than silent.
    private byte[] CorrectRotation(byte[] bytes, string extension, int rotation, FlickrSize? reference)
    {
        try
        {
            return FlickrRotation.Apply(bytes, extension, rotation, reference);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not apply Flickr rotation {Rotation} to a {Extension} import.", rotation, extension);
            return bytes;
        }
    }

    private static string ExtractPhotoId(Uri uri)
    {
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (uri.Host == "flic.kr")
        {
            if (segments.Length == 2 && segments[0] == "p")
            {
                return FlickrShortUrl.Decode(segments[1]);
            }

            throw new ImageFetchException(400,
                "Unrecognised Flickr short URL, please link to a photo (flic.kr/p/<code>).");
        }

        var photosIndex = Array.IndexOf(segments, "photos");
        if (photosIndex >= 0 && photosIndex + 2 < segments.Length)
        {
            return segments[photosIndex + 2];
        }

        throw new ImageFetchException(400,
            "Unrecognised Flickr URL, please link to a photo page (flickr.com/photos/<user>/<id>).");
    }
}
