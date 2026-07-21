using System.Collections.Concurrent;

namespace CoasterpediaServices.Common.Wiki;

/// <summary>
/// MediaWiki marks session cookies Secure (derived from the public https:// $wgServer), but this client
/// talks to MediaWiki over the internal http:// hostname, so CookieContainer silently drops them on the
/// way out. This replays raw Set-Cookie name/value pairs verbatim, ignoring Domain/Path/Secure attributes,
/// since this client only ever talks to a single trusted host.
/// </summary>
public class LenientCookieHandler : DelegatingHandler
{
    private readonly ConcurrentDictionary<string, string> _cookies = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!_cookies.IsEmpty)
        {
            request.Headers.Add("Cookie", string.Join("; ", _cookies.Select(c => $"{c.Key}={c.Value}")));
        }

        var response = await base.SendAsync(request, cancellationToken);

        if (response.Headers.TryGetValues("Set-Cookie", out var setCookieHeaders))
        {
            foreach (var header in setCookieHeaders)
            {
                var parts = header.Split(';', 2)[0].Split('=', 2);
                if (parts.Length == 2)
                {
                    _cookies[parts[0].Trim()] = parts[1].Trim();
                }
            }
        }

        return response;
    }
}
