namespace CoasterpediaServices.ImageFetch.Clients.GeographDe;

public interface IGeographDeClient
{
    Task<GeographDePhoto> GetPhotoAsync(string photoId, CancellationToken cancellationToken);
}
