using Refit;

namespace CoasterpediaServices.ImageFetch.Clients.Geograph;

public interface IGeographClient
{
    [Get("/api/photo/{photoId}/{apiKey}?format=json")]
    public Task<GeographResponse> GetPhotoAsync(string photoId, string apiKey);
}
