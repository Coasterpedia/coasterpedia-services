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

    // ignoreRedirects is for sites that redirect bots away from every page (Facebook to its login page):
    // only the status counts, and no redirect is kept.
    public async Task<LinkState> Check(Uri uri, bool ignoreRedirects = false)
    {
        var key = Key(uri);
        var previous = await _store.Get(key);
        var now = _time.GetUtcNow();
        // A state saved before soft 404s were caught can hold one as alive; ask again rather than trust it.
        var softNotFoundSaved = !ignoreRedirects && previous?.RedirectTarget is { } saved && SoftNotFound.IsSoftNotFound(uri, new Uri(saved));
        if (!LinkStates.IsDue(previous, now) && !softNotFoundSaved)
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

        var target = ignoreRedirects ? null : RedirectTarget(response, uri);
        if (result == CheckResult.Success && target != null && SoftNotFound.IsSoftNotFound(uri, target))
        {
            _logger.LogInformation("{Url} redirects to {Target}, which isn't the cited page", key, target);
            result = CheckResult.Failure;
        }

        var state = LinkStates.Decide(previous, result, target?.ToString(), now);
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
