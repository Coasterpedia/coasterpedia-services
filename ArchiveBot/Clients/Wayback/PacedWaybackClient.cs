using System.Diagnostics;
using System.Net;
using CoasterpediaServices.ArchiveBot.Options;
using Microsoft.Extensions.Logging;
using Refit;

namespace CoasterpediaServices.ArchiveBot.Clients.Wayback;

// Wraps CDX searches with pacing, a pause on 429, and no captures for a URL the Wayback Machine excludes. A site
// closed to the CDX search is still captured, so FindSnapshot looks for it through playback instead.
// Only use from jobs on the single-worker archive queue, as with PacedArchiveClient.
public class PacedWaybackClient : IWaybackClient
{
    // The CDX server sends an empty body and gives the reason here, e.g. "AdministrativeAccessControlException:
    // Blocked Site Error", or "Blocked By Robots".
    private const string RuntimeErrorHeader = "x-archive-wayback-runtime-error";

    // The 403 body for a site whose owner closed the CDX search to the public, as The Guardian did.
    private const string RestrictedSearch = "requires authorization";

    private readonly IWaybackClient _waybackClient;
    private readonly WaybackPlayback _playback;
    private readonly ILogger<PacedWaybackClient> _logger;
    private readonly TimeSpan _searchInterval;
    private readonly TimeSpan _rateLimitCooldown;
    private readonly HashSet<string> _excluded = [];
    // By host: the whole site is closed, so one refusal saves a search for each of its other citations.
    private readonly HashSet<string> _restrictedHosts = new(StringComparer.OrdinalIgnoreCase);
    private long _lastSearch;

    public PacedWaybackClient(IWaybackClient waybackClient, WaybackPlayback playback, ILogger<PacedWaybackClient> logger)
        : this(waybackClient, playback, logger, ArchiveBotConfig.SearchInterval, ArchiveBotConfig.RateLimitCooldown)
    {
    }

    // For tests, which search without pacing or pauses.
    internal PacedWaybackClient(IWaybackClient waybackClient, WaybackPlayback playback, ILogger<PacedWaybackClient> logger,
        TimeSpan searchInterval, TimeSpan rateLimitCooldown)
    {
        _waybackClient = waybackClient;
        _playback = playback;
        _logger = logger;
        _searchInterval = searchInterval;
        _rateLimitCooldown = rateLimitCooldown;
    }

    // An excluded URL can't be saved either, so the job doesn't try.
    public bool IsExcluded(string url) => _excluded.Contains(url);

    private bool IsRestricted(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && _restrictedHosts.Contains(uri.Host);

    // Hides the FindSnapshot extension for callers holding this class, which is how ArchiveLinkJob gets the fallback.
    public async Task<Snapshot?> FindSnapshot(string url, DateTime? accessDate, DateTime? publishDate)
    {
        var snapshot = await WaybackClientExtensions.FindSnapshot(this, url, accessDate, publishDate);
        if (snapshot != null || !IsRestricted(url))
        {
            return snapshot;
        }

        // Nearest to the date the citation saw, as near as playback can get to FindSnapshot's choice.
        var timestamp = (accessDate ?? publishDate ?? DateTime.UtcNow).ToString("yyyyMMdd") + "235959";
        await Pace();
        try
        {
            var captured = await _playback.NearestCapture(url, timestamp);
            return captured == null
                ? null
                : new Snapshot($"https://web.archive.org/web/{captured}/{url}", DateTime.ParseExact(captured, "yyyyMMddHHmmss", null));
        }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.TooManyRequests)
        {
            _logger.LogWarning("Wayback rate limit hit on playback, pausing for {Cooldown}", _rateLimitCooldown);
            await Task.Delay(_rateLimitCooldown);
            throw new InvalidOperationException($"Finding a capture of {url} was rate limited", e);
        }
    }

    public async Task<List<List<string>>?> GetCaptures(string url, string? fromTimestamp, string? toTimestamp, int limit, string filter)
    {
        if (IsExcluded(url) || IsRestricted(url))
        {
            return null;
        }

        await Pace();
        try
        {
            return await _waybackClient.GetCaptures(url, fromTimestamp, toTimestamp, limit, filter);
        }
        catch (ApiException e) when (e.StatusCode == HttpStatusCode.Forbidden && e.Content?.Contains(RestrictedSearch) == true
                                     && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            _logger.LogInformation("{Host} is closed to the CDX search, using playback", uri.Host);
            _restrictedHosts.Add(uri.Host);
            return null;
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

    // Counted from the last request, so time spent checking links between requests counts towards the gap.
    private async Task Pace()
    {
        var wait = _searchInterval - Stopwatch.GetElapsedTime(_lastSearch);
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait);
        }

        _lastSearch = Stopwatch.GetTimestamp();
    }

    private static bool IsBlocked(ApiException e) =>
        e.Headers.TryGetValues(RuntimeErrorHeader, out var values) && values.Any(v => v.Contains("Blocked"));
}
