using CoasterpediaServices.ImageFetch.Clients.Geograph;

namespace CoasterpediaServices.ImageFetch.Fetchers;

public class GeographFetcher : ISourceFetcher
{
    private const string PhotoPathPrefix = "/photo/";

    private readonly IGeographClient _geographClient;
    private readonly HttpClient _downloadClient;

    public GeographFetcher(IGeographClient geographClient, HttpClient downloadClient)
    {
        _geographClient = geographClient;
        _downloadClient = downloadClient;
    }

    public bool CanHandle(Uri uri) => uri.Host == "www.geograph.org.uk";

    public async Task<FetchResult> FetchAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (!uri.AbsolutePath.StartsWith(PhotoPathPrefix, StringComparison.Ordinal))
        {
            throw new ImageFetchException(400,
                "Unrecognised Geograph URL, please link to a photo page (www.geograph.org.uk/photo/123).");
        }

        var photoId = uri.AbsolutePath[PhotoPathPrefix.Length..];
        var page = await _geographClient.GetPhotoAsync(photoId);

        if (page.Error != null)
        {
            throw new ImageFetchException(502, page.Error);
        }

        var imageUrl = page.Imgserver + page.Image;
        var bytes = await BoundedDownloader.DownloadAsync(_downloadClient, imageUrl, cancellationToken);
        var extension = Path.GetExtension(page.Image ?? string.Empty);
        var sourceUrl = $"https://www.geograph.org.uk/photo/{photoId}";

        return new FetchResult
        {
            Bytes = bytes,
            ContentType = MimeTypes.FromExtension(extension),
            SuggestedFileName = Path.GetFileName(page.Image ?? $"{photoId}{extension}"),
            Title = page.Title ?? photoId,
            Author = page.Realname,
            SourceUrl = sourceUrl,
            License = "cc-by-sa-2.0",
            AdditionalLicenseWikitext = $"{{{{Geograph|{sourceUrl}}}}}",
            Date = page.Taken,
            Latitude = page.Wgs84Lat,
            Longitude = page.Wgs84Long
        };
    }
}
