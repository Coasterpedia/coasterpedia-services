using System.Net;

namespace CoasterpediaServices.ArchiveBot.Clients.WebClient;

public class WebClient
{
    private readonly HttpClient _httpClient;

    public WebClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<StatusResponse> CheckUrlAvailable(string url)
    {
        try
        {
            var response = await _httpClient.GetAsync(url);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            {
                return new StatusResponse(null, response);
            }

            return new StatusResponse(response.IsSuccessStatusCode, response);
        }
        catch (HttpRequestException)
        {
            return new StatusResponse(false, null);
        }
        catch (TaskCanceledException)
        {
            return new StatusResponse(null, null);
        }
    }
}