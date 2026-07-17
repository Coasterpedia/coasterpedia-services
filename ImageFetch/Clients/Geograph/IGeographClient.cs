using Refit;

namespace CoasterpediaServices.ImageFetch.Clients.Geograph;

public interface IGeographClient
{
    [Get("/api/photo/{photoId}/coasterpedia.net?format=json")]
    public Task<GeographResponse> GetPhotoAsync(string photoId);
}
