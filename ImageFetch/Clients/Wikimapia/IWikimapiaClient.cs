using Refit;

namespace CoasterpediaServices.ImageFetch.Clients.Wikimapia;

public interface IWikimapiaClient
{
    [Get("/?function=place.getbyid&format=json&data_blocks=main,photos,location")]
    Task<WikimapiaPlaceResponse> GetPlaceAsync([AliasAs("id")] string objectId, [AliasAs("key")] string apiKey);
}
