using CoasterpediaServices.ArchiveBot.Clients.WebClient;
using Microsoft.Extensions.Logging;

namespace CoasterpediaServices.ArchiveBot.Links;

// Checks a cited URL, at most once per recheck interval however many pages cite it, and remembers
// the answer.
public class LinkChecker
{
    private readonly WebClient _webClient;
    private readonly ILinkStateStore _store;
    private readonly TimeProvider _time;
    private readonly ILogger<LinkChecker> _logger;

    public LinkChecker(WebClient webClient, ILinkStateStore store, TimeProvider time, ILogger<LinkChecker> logger)
    {
        _webClient = webClient;
        _store = store;
        _time = time;
        _logger = logger;
    }

    public async Task<LinkState> Check(Uri uri)
    {
        var key = Key(uri);
        var previous = await _store.Get(key);
        var now = _time.GetUtcNow();
        if (!LinkStates.IsDue(previous, now))
        {
            _logger.LogInformation("{Url} checked {LastChecked}, still {Health}", key, previous!.LastChecked, previous.Health);
            return previous;
        }

        var response = await _webClient.CheckUrlAvailable(uri.ToString());
        var result = response.Available switch
        {
            true => CheckResult.Success,
            false => CheckResult.Failure,
            null => CheckResult.Inconclusive
        };

        var state = LinkStates.Decide(previous, result, RedirectTarget(response, uri)?.ToString(), now);
        _logger.LogInformation("{Url} check {Result}: {Previous} -> {Health}", key, result, previous?.Health, state.Health);
        await _store.Save(key, state);
        return state;
    }

    // The fragment never reaches the server, so "page#a" and "page#b" are one link.
    public static string Key(Uri uri) => uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);

    // Compared as URIs, so "https://example.com" and "https://example.com/" are not a redirect.
    private static Uri? RedirectTarget(StatusResponse statusResponse, Uri original)
    {
        var final = statusResponse.ResponseMessage?.RequestMessage?.RequestUri;
        return final != null && final != original ? final : null;
    }
}
