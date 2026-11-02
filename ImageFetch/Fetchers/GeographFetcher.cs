using CoasterpediaServices.ImageFetch.Clients.Geograph;
using CoasterpediaServices.ImageFetch.Options;
using CoasterpediaServices.ImageFetch.Provenance;
using Microsoft.Extensions.Options;

namespace CoasterpediaServices.ImageFetch.Fetchers;

public class GeographFetcher : ISourceFetcher
{
    private const string PhotoPathPrefix = "/photo/";

    private readonly IGeographClient _geographClient;
    private readonly HttpClient _downloadClient;
    private readonly GeographConfig _geographConfig;

    public GeographFetcher(IGeographClient geographClient, HttpClient downloadClient, IOptions<GeographConfig> geographConfig)
    {
        _geographClient = geographClient;
        _downloadClient = downloadClient;
        _geographConfig = geographConfig.Value;
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
        var page = await _geographClient.GetPhotoAsync(photoId, _geographConfig.ApiKey);

        if (page.Error != null)
        {
            throw new ImageFetchException(502, page.Error);
        }

        var imageUrl = page.Imgserver + page.Image;
        var bytes = await DownloadLargestAsync(photoId, page, imageUrl, cancellationToken);
        var extension = Path.GetExtension(page.Image ?? string.Empty);
        var sourceUrl = $"https://www.geograph.org.uk/photo/{photoId}";
        var provenance = ProvenanceBuilder.Build(SourceRegistry.Geograph, "cc-by-sa-2.0", sourceUrl);

        return new FetchResult
        {
            Bytes = bytes,
            ContentType = MimeTypes.FromExtension(extension),
            SuggestedFileName = Path.GetFileName(page.Image ?? $"{photoId}{extension}"),
            Title = page.Title ?? photoId,
            Author = page.Realname,
            SourceUrl = sourceUrl,
            Source = provenance.Source,
            License = provenance.License,
            Cards = provenance.Cards,
            Date = page.Taken,
            Latitude = page.Wgs84Lat,
            Longitude = page.Wgs84Long
        };
    }

    /// <summary>
    /// The API's <c>image</c> is always the 640px standard copy, and the plain original
    /// (<c>_original.jpg</c>) 403s "Missing signature" without a link signed by the website. The
    /// original is reachable through stamp.php, Geograph's "CC-stamped image" tool, which always
    /// burns in a credit line; at pointsize 1 that is a speck a few pixels high, and the file page
    /// carries the real attribution. Only worth it when the upload was bigger than the standard copy
    /// (<c>sizeinfo</c> 4–5 are the original's width and height). If stamp.php fails, the 1024px
    /// copy the image server keeps beside the standard one, then the standard copy itself.
    /// </summary>
    private async Task<byte[]> DownloadLargestAsync(string photoId, GeographResponse page, string standardUrl,
        CancellationToken cancellationToken)
    {
        if (HasLargerOriginal(page.Sizeinfo))
        {
            var candidates = new List<string>
            {
                $"https://t0.geograph.org.uk/stamp.php?id={Uri.EscapeDataString(photoId)}&large=1&font=Helvetica&gravity=SouthEast&pointsize=1&download=1"
            };
            if (Path.GetExtension(standardUrl) is { Length: > 0 } extension)
            {
                candidates.Add(standardUrl[..^extension.Length] + "_1024x1024" + extension);
            }

            foreach (var url in candidates)
            {
                try
                {
                    return await BoundedDownloader.DownloadAsync(_downloadClient, url, cancellationToken);
                }
                catch (ImageFetchException e) when (e.StatusCode == 502)
                {
                }
            }
        }

        return await BoundedDownloader.DownloadAsync(_downloadClient, standardUrl, cancellationToken);
    }

    private static bool HasLargerOriginal(string[]? sizeinfo) =>
        sizeinfo is { Length: >= 6 }
        && int.TryParse(sizeinfo[0], out var width) && int.TryParse(sizeinfo[1], out var height)
        && int.TryParse(sizeinfo[4], out var originalWidth) && int.TryParse(sizeinfo[5], out var originalHeight)
        && Math.Max(originalWidth, originalHeight) > Math.Max(width, height);
}
