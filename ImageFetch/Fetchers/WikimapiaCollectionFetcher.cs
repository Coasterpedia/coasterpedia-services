using CoasterpediaServices.ImageFetch.Clients.Wikimapia;
using CoasterpediaServices.ImageFetch.Options;
using Microsoft.Extensions.Options;

namespace CoasterpediaServices.ImageFetch.Fetchers;

public class WikimapiaCollectionFetcher : ICollectionFetcher
{
    private readonly IWikimapiaClient _wikimapiaClient;
    private readonly WikimapiaConfig _config;

    public WikimapiaCollectionFetcher(IWikimapiaClient wikimapiaClient, IOptions<WikimapiaConfig> config)
    {
        _wikimapiaClient = wikimapiaClient;
        _config = config.Value;
    }

    public bool CanHandle(Uri uri) => WikimapiaUrlParser.TryParse(uri, out _, out var photoId) && photoId == null;

    public async Task<IReadOnlyList<CollectionItem>> FetchAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (!WikimapiaUrlParser.TryParse(uri, out var objectId, out _))
        {
            throw new ImageFetchException(400, "Unrecognised Wikimapia URL.");
        }

        var place = await _wikimapiaClient.GetPlaceAsync(objectId, _config.ApiKey);
        var photos = place.Photos ?? [];

        if (photos.Count == 0)
        {
            throw new ImageFetchException(422, "This Wikimapia place has no photos.");
        }

        return photos
            .Select(photo => new CollectionItem(
                $"https://wikimapia.org/{objectId}/photo/{photo.Id}",
                string.IsNullOrWhiteSpace(place.Title) ? null : place.Title,
                photo.ThumbnailUrl))
            .ToList();
    }
}
