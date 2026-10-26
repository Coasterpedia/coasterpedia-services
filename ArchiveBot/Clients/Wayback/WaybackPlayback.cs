using System.Net;
using System.Text.RegularExpressions;

namespace CoasterpediaServices.ArchiveBot.Clients.Wayback;

// Finds a capture through the Wayback Machine's own pages, for sites whose captures the CDX search won't list.
// The HttpClient must not follow redirects: the redirect is the answer.
public partial class WaybackPlayback
{
    private readonly HttpClient _httpClient;

    public WaybackPlayback(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    // The timestamp of the capture nearest the one given, if that capture answered 200. Unlike the CDX search
    // this can't filter by status or pick a side, so the capture may be after the timestamp.
    public async Task<string?> NearestCapture(string url, string timestamp)
    {
        var path = $"/web/{timestamp}id_/{url}";
        for (var hop = 0; hop < 2; hop++)
        {
            using var response = await _httpClient.SendAsync(new HttpRequestMessage(HttpMethod.Head, path));
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (response.StatusCode == HttpStatusCode.OK)
            {
                return CaptureTimestamp().Match(path).Groups[1].Value;
            }

            if ((int)response.StatusCode is < 300 or >= 400)
            {
                response.EnsureSuccessStatusCode();
            }

            // The first redirect is to the nearest capture. A second means that capture was itself a redirect.
            var location = response.Headers.Location;
            path = location == null ? "" : location.IsAbsoluteUri ? location.PathAndQuery : location.ToString();
            if (hop > 0 || !CaptureTimestamp().IsMatch(path))
            {
                return null;
            }
        }

        return null;
    }

    [GeneratedRegex(@"^/web/(\d{14})id_/")]
    private static partial Regex CaptureTimestamp();
}
