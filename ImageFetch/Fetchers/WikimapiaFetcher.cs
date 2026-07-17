using System.Globalization;
using CoasterpediaServices.ImageFetch.Clients.Wikimapia;
using CoasterpediaServices.ImageFetch.Options;
using CoasterpediaServices.ImageFetch.Provenance;
using Microsoft.Extensions.Options;

namespace CoasterpediaServices.ImageFetch.Fetchers;

public class WikimapiaFetcher : ISourceFetcher
{
    private readonly IWikimapiaClient _wikimapiaClient;
    private readonly HttpClient _downloadClient;
    private readonly WikimapiaConfig _config;

    public WikimapiaFetcher(IWikimapiaClient wikimapiaClient, HttpClient downloadClient, IOptions<WikimapiaConfig> config)
    {
        _wikimapiaClient = wikimapiaClient;
        _downloadClient = downloadClient;
        _config = config.Value;
    }

    public bool CanHandle(Uri uri) => WikimapiaUrlParser.TryParse(uri, out _, out var photoId) && photoId != null;

    public async Task<FetchResult> FetchAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (!WikimapiaUrlParser.TryParse(uri, out var objectId, out var photoId) || photoId == null)
        {
            throw new ImageFetchException(400,
                "Unrecognised Wikimapia URL, please link to a specific photo.");
        }

        var place = await _wikimapiaClient.GetPlaceAsync(objectId, _config.ApiKey);
        var photo = place.Photos?.FirstOrDefault(p => p.Id.ToString(CultureInfo.InvariantCulture) == photoId)
                    ?? throw new ImageFetchException(404, "Photo not found on Wikimapia.");

        var bytes = await BoundedDownloader.DownloadAsync(_downloadClient, photo.FullUrl, cancellationToken);
        var extension = Path.GetExtension(new Uri(photo.FullUrl).AbsolutePath);
        var sourceUrl = $"https://wikimapia.org/{objectId}/photo/{photoId}";
        var provenance = ProvenanceBuilder.Build(SourceRegistry.Wikimapia, "cc-by-sa-3.0", sourceUrl);

        return new FetchResult
        {
            Bytes = bytes,
            ContentType = MimeTypes.FromExtension(extension),
            SuggestedFileName = $"{photoId}{extension}",
            Title = string.IsNullOrWhiteSpace(place.Title) ? objectId : place.Title,
            Author = photo.UserName,
            SourceUrl = sourceUrl,
            Source = provenance.Source,
            License = provenance.License,
            Cards = provenance.Cards,
            Date = DateTimeOffset.FromUnixTimeSeconds(photo.Time).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Latitude = place.Location?.Lat.ToString(CultureInfo.InvariantCulture),
            Longitude = place.Location?.Lon.ToString(CultureInfo.InvariantCulture)
        };
    }
}
