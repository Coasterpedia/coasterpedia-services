using CoasterpediaServices.Common.Wiki;

namespace CoasterpediaServices.ImageFetch.Clients.Commons;

public class CommonsClient
{
    private readonly HttpClient _client;

    public CommonsClient(HttpClient client)
    {
        _client = client;
    }

    public WikiClient GetSite() => new(_client);

    public HttpClient HttpClient => _client;
}
