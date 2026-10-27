using System.Net;
using System.Text.RegularExpressions;
using CoasterpediaServices.ArchiveBot.Options;
using Microsoft.Extensions.Logging;
using Refit;

namespace CoasterpediaServices.ArchiveBot.Clients.Archive;

// Wraps Wayback saves with pacing, a pause on 429, and a pause on a host whose captures keep failing. Only use
// from jobs on the single-worker archive queue: holding that worker is what holds back every other queued save.
public partial class PacedArchiveClient
{
    private readonly IArchiveClient _archiveClient;
    private readonly ILogger<PacedArchiveClient> _logger;
    private readonly TimeSpan _saveInterval;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, (int Failures, DateTimeOffset? PausedUntil)> _hosts = new(StringComparer.OrdinalIgnoreCase);

    public PacedArchiveClient(IArchiveClient archiveClient, ILogger<PacedArchiveClient> logger, TimeProvider time)
        : this(archiveClient, logger, ArchiveBotConfig.SaveInterval, time)
    {
    }

    // For tests, which save without pacing.
    internal PacedArchiveClient(IArchiveClient archiveClient, ILogger<PacedArchiveClient> logger, TimeSpan saveInterval, TimeProvider time)
    {
        _archiveClient = archiveClient;
        _logger = logger;
        _saveInterval = saveInterval;
        _time = time;
    }

    // When saves to the URL's host start again, while they're paused. Check before SavePage.
    public DateTimeOffset? PausedUntil(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && _hosts.TryGetValue(uri.Host, out var host)
            && host.PausedUntil is { } until && until > _time.GetUtcNow()
            ? until
            : null;

    public async Task<IApiResponse> SavePage(string url)
    {
        var response = await _archiveClient.SavePage(url);
        Record(url, response);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            // Throw after the pause so Hangfire retries the job, rather than every other queued
            // save hitting the limiter in turn and using up its retries.
            _logger.LogWarning("Wayback rate limit hit, pausing saves for {Cooldown}", ArchiveBotConfig.RateLimitCooldown);
            await Task.Delay(ArchiveBotConfig.RateLimitCooldown);
            throw new InvalidOperationException($"Saving {url} was rate limited");
        }

        // Pace saves so a bulk upload doesn't trip archive.org's rate limiter.
        await Task.Delay(_saveInterval);
        return response;
    }

    // A save after the pause ends gets one try: another failure pauses the host again straight away.
    private void Record(string url, IApiResponse response)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return;
        }

        if (response.Headers?.Location != null)
        {
            _hosts.Remove(uri.Host);
            return;
        }

        if ((int)response.StatusCode is < 520 or > 529)
        {
            return;
        }

        var failures = _hosts.GetValueOrDefault(uri.Host).Failures + 1;
        DateTimeOffset? pausedUntil = null;
        if (failures >= ArchiveBotConfig.HostFailuresBeforePause)
        {
            pausedUntil = _time.GetUtcNow() + ArchiveBotConfig.HostPause;
            _logger.LogWarning("Save Page Now failed {Failures} captures running on {Host}, pausing saves there until {Until}",
                failures, uri.Host, pausedUntil);
        }

        _hosts[uri.Host] = (failures, pausedUntil);
    }

    // Save Page Now answers a capture it couldn't make with a 52x and gives the reason on the page, e.g. "Cannot
    // resolve host" (523), or a timeout or the site's own error (520).
    public static string FailureReason(IApiResponse response)
    {
        var match = (response.Error as ApiException)?.Content is { } content ? SorryMessage().Match(content) : null;
        var reason = match is { Success: true } ? WebUtility.HtmlDecode(match.Groups[1].Value).Trim() : null;
        return $"status {(int)response.StatusCode}" + (string.IsNullOrEmpty(reason) ? "" : $": {reason}");
    }

    [GeneratedRegex(@"<h2>Sorry</h2>\s*<p>(.*?)</p>", RegexOptions.Singleline)]
    private static partial Regex SorryMessage();
}
