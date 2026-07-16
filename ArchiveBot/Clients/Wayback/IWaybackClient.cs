using Refit;

namespace CoasterpediaServices.ArchiveBot.Clients.Wayback;

public interface IWaybackClient
{
    [Get("/wayback/available?url={url}&timeout=60")]
    public Task<AvailableResponse> GetAvailable(string url);
}