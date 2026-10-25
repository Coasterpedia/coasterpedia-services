using System.Diagnostics;
using System.Net;
using CoasterpediaServices.ArchiveBot.Options;
using Microsoft.Extensions.Logging;
using Refit;

namespace CoasterpediaServices.ArchiveBot.Clients.Wayback;

// Wraps CDX searches with pacing, a pause on 429, and no captures for a URL the Wayback Machine excludes.
// Only use from jobs on the single-worker archive queue, as with PacedArchiveClient.
public class PacedWaybackClient : IWaybackClient
{
    // The CDX server sends an empty body and gives the reason here, e.g. "AdministrativeAccessControlException:
    // Blocked Site Error", or "Blocked By Robots".
    private const string RuntimeErrorHeader = "x-archive-wayback-runtime-error";

    private readonly IWaybackClient _waybackClient;
    private readonly ILogger<PacedWaybackClient> _logger;
    private readonly TimeSpan _searchInterval;
    private readonly TimeSpan _rateLimitCooldown;
    private readonly HashSet<string> _excluded = [];
    private long _lastSearch;

    public PacedWaybackClient(IWaybackClient waybackClient, ILogger<PacedWaybackClient> logger)
        : this(waybackClient, logger, ArchiveBotConfig.SearchInterval, ArchiveBotConfig.RateLimitCooldown)
    {
    }

    // For tests, which search without pacing or pauses.
    internal PacedWaybackClient(IWaybackClient waybackClient, ILogger<PacedWaybackClient> logger, TimeSpan searchInterval,
        TimeSpan rateLimitCooldown)
    {
        _waybackClient = waybackClient;
        _logger = logger;
        _searchInterval = searchInterval;
        _rateLimitCooldown = rateLimitCooldown;
    }

    // An excluded URL can't be saved either, so the job doesn't try.
    public bool IsExcluded(string url) => _excluded.Contains(url);

    public async Task<List<List<string>>?> GetCaptures(string url, string? fromTimestamp, string? toTimestamp, int limit, string filter)
    {
        if (IsExcluded(url))
        {
            return null;
        }

        // Counted from the last search, so time spent checking links between searches counts towards the gap.
        var wait = _searchInterval - Stopwatch.GetElapsedTime(_lastSearch);
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait);
        }

        _lastSearch = Stopwatch.GetTimestamp();
        try
        {
            return await _waybackClient.GetCaptures(url, fromTimestamp, toTimestamp, limit, filter);
        }
        // Any other 403 still fails the job, so a block on the bot itself isn't read as every link having no archive.
        catch (ApiException e) when (e.StatusCode == HttpStatusCode.Forbidden && IsBlocked(e))
        {
            _logger.LogInformation("{Url} is excluded from the Wayback Machine", url);
            _excluded.Add(url);
            return null;
        }
        catch (ApiException e) when (e.StatusCode == HttpStatusCode.TooManyRequests)
        {
            // Throw after the pause so Hangfire retries the job, and the next job doesn't walk straight into the limiter.
            var cooldown = e.Headers.RetryAfter?.Delta ?? _rateLimitCooldown;
            _logger.LogWarning("Wayback rate limit hit on search, pausing for {Cooldown}", cooldown);
            await Task.Delay(cooldown);
            throw new InvalidOperationException($"Searching for captures of {url} was rate limited", e);
        }
    }

    private static bool IsBlocked(ApiException e) =>
        e.Headers.TryGetValues(RuntimeErrorHeader, out var values) && values.Any(v => v.Contains("Blocked"));
}
