using CoasterpediaServices.ImageFetch.Clients.GeographDe;
using CoasterpediaServices.ImageFetch.Provenance;

namespace CoasterpediaServices.ImageFetch.Fetchers;

/// <summary>
/// Geograph Deutschland (geo.hlipp.de) — a sister project to Geograph Britain and Ireland, same
/// CC BY-SA 2.0 terms, forked codebase. Separate from <see cref="GeographFetcher"/> on purpose:
/// the two APIs have drifted (see <see cref="GeographDeClient"/>), and the British path is in
/// production. What they share is the licence and <see cref="ProvenanceBuilder"/>.
/// </summary>
public class GeographDeFetcher : ISourceFetcher
{
    private const string PhotoPathPrefix = "/photo/";

    /// <summary>The German and English front ends of the same site, serving the same photo ids.</summary>
    private static readonly string[] Hosts = ["geo.hlipp.de", "geo-en.hlipp.de"];

    private readonly IGeographDeClient _geographDeClient;
    private readonly HttpClient _downloadClient;

    public GeographDeFetcher(IGeographDeClient geographDeClient, HttpClient downloadClient)
    {
        _geographDeClient = geographDeClient;
        _downloadClient = downloadClient;
    }

    public bool CanHandle(Uri uri) => Hosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);

    public async Task<FetchResult> FetchAsync(Uri uri, CancellationToken cancellationToken)
    {
        var photoId = PhotoIdFrom(uri)
                      ?? throw new ImageFetchException(400,
                          "Unrecognised Geograph Deutschland URL, please link to a photo page (geo.hlipp.de/photo/123).");

        var photo = await _geographDeClient.GetPhotoAsync(photoId, cancellationToken);
        // 404, not 502: the only way a well-formed request fails here is a photo id that isn't
        // there, which is the user's paste, not the upstream falling over.
        if (photo.Error != null)
        {
            throw new ImageFetchException(404, photo.Error);
        }

        if (photo.ImageUrl == null)
        {
            throw new ImageFetchException(502, "Geograph Deutschland gave no image for that photo.");
        }

        // The API hands back http:// URLs throughout (the site still declares an http canonical),
        // but https serves the same bytes. Upgrade rather than downgrade the fetch.
        var imageUrl = Https(photo.ImageUrl);
        var bytes = await BoundedDownloader.DownloadAsync(_downloadClient, imageUrl, cancellationToken);
        var extension = Path.GetExtension(new Uri(imageUrl).AbsolutePath);

        // Canonicalised to the German host, which is what the photo page's own rel="canonical"
        // names — so the same photo pasted from geo-en resolves to one attribution link, and the
        // gadget's duplicate check sees one identity rather than two.
        var sourceUrl = $"https://geo.hlipp.de/photo/{photoId}";
        var provenance = ProvenanceBuilder.Build(SourceRegistry.GeographDe, "cc-by-sa-2.0", sourceUrl);

        return new FetchResult
        {
            Bytes = bytes,
            ContentType = MimeTypes.FromExtension(extension),
            SuggestedFileName = Path.GetFileName(new Uri(imageUrl).AbsolutePath),
            Title = photo.Title ?? photoId,
            Author = photo.Author,
            SourceUrl = sourceUrl,
            Source = provenance.Source,
            License = provenance.License,
            Cards = provenance.Cards,
            Date = photo.Taken,
            Latitude = photo.Latitude,
            Longitude = photo.Longitude
        };
    }

    /// <summary>
    /// The photo id in <c>/photo/272349</c>, tolerating a trailing slash and the alternate
    /// representations the site links beside it (<c>/photo/272349.kml</c>). Null when the path
    /// isn't a photo page at all.
    /// </summary>
    private static string? PhotoIdFrom(Uri uri)
    {
        if (!uri.AbsolutePath.StartsWith(PhotoPathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rest = uri.AbsolutePath[PhotoPathPrefix.Length..];
        var cut = rest.IndexOfAny(['/', '.']);
        var id = cut == -1 ? rest : rest[..cut];

        return id.Length > 0 && id.All(char.IsAsciiDigit) ? id : null;
    }

    private static string Https(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            ? string.Concat("https://", url.AsSpan("http://".Length))
            : url;
}
