using Refit;

namespace CoasterpediaServices.ArchiveBot.Clients.Archive;

public interface IArchiveClient
{
    [Get("/save/{url}")]
    public Task<IApiResponse> SavePage(string url);
}