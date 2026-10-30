using System.Net;
using System.Text.RegularExpressions;
using CoasterpediaServices.ArchiveBot.Options;
using Microsoft.Extensions.Logging;
using Refit;

namespace CoasterpediaServices.ArchiveBot.Clients.Archive;

// Wraps Wayback saves with pacing, a pause on 429, a pause on a host whose captures keep failing, and save slots
// HostSaveInterval apart on each host for jobs that can wait. Only use from jobs on the single-worker archive queue:
// holding that worker is what holds back every other queued save. State is in memory, so a restart forgets pauses
// and slots.
public partial class PacedArchiveClient
{
    private readonly IArchiveClient _archiveClient;
    private readonly ILogger<PacedArchiveClient> _logger;
    private readonly TimeSpan _saveInterval;
    private readonly TimeSpan _hostInterval;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, Host> _hosts = new(StringComparer.OrdinalIgnoreCase);

    public PacedArchiveClient(IArchiveClient archiveClient, ILogger<PacedArchiveClient> logger, TimeProvider time)
        : this(archiveClient, logger, ArchiveBotConfig.SaveInterval, ArchiveBotConfig.HostSaveInterval, time)
    {
    }

    // For tests, which save without pacing.
    internal PacedArchiveClient(IArchiveClient archiveClient, ILogger<PacedArchiveClient> logger, TimeSpan saveInterval,
        TimeSpan hostInterval, TimeProvider time)
    {
        _archiveClient = archiveClient;
        _logger = logger;
        _saveInterval = saveInterval;
        _hostInterval = hostInterval;
        _time = time;
    }

    // When saves to the URL's host start again, while they're paused. Check before SavePage.
    public DateTimeOffset? PausedUntil(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && _hosts.TryGetValue(uri.Host, out var host)
            && host.PausedUntil is { } until && until > _time.GetUtcNow()
            ? until
            : null;

    // For a job that can wait its turn: null to save now, or a slot to come back at. Slots on one host are
    // HostSaveInterval apart and run past any pause, so a backlog drains steadily instead of in bursts that trip
    // the host's limit. A job back late at its slot waits here until an interval after the host's last save.
    public async Task<DateTimeOffset?> TakeTurn(string url)
    {
        var slot = Reserve(url);
        if (slot == null && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var wait = HostFor(uri).LastSave + _hostInterval - _time.GetUtcNow();
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, _time);
            }
        }

        return slot;
    }

    internal DateTimeOffset? Reserve(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var now = _time.GetUtcNow();
        var host = HostFor(uri);
        var pausedUntil = host.PausedUntil is { } until && until > now ? until : (DateTimeOffset?)null;
        // Every slot handed out has come round, so any left belong to jobs that were deleted.
        if (host.NextSlot <= now)
        {
            host.Slots.Clear();
        }

        if (host.Slots.Remove(url, out var held))
        {
            if (held > now)
            {
                host.Slots[url] = held;
                return held;
            }

            if (pausedUntil == null)
            {
                return null;
            }
        }

        var slot = new[] { now, host.NextSlot, pausedUntil ?? now }.Max();
        host.NextSlot = slot + _hostInterval;
        if (slot <= now)
        {
            return null;
        }

        host.Slots[url] = slot;
        return slot;
    }

    public async Task<IApiResponse> SavePage(string url)
    {
        // Citations and uploads save without a turn, but the next slot on the host still waits a full interval.
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var host = HostFor(uri);
            host.LastSave = _time.GetUtcNow();
            if (host.NextSlot < host.LastSave + _hostInterval)
            {
                host.NextSlot = host.LastSave + _hostInterval;
            }
        }

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

        var host = HostFor(uri);
        if (response.Headers?.Location != null)
        {
            host.Failures = 0;
            host.PausedUntil = null;
            return;
        }

        if ((int)response.StatusCode is < 520 or > 529)
        {
            return;
        }

        host.Failures++;
        host.PausedUntil = null;
        if (host.Failures >= ArchiveBotConfig.HostFailuresBeforePause)
        {
            host.PausedUntil = _time.GetUtcNow() + ArchiveBotConfig.HostPause;
            _logger.LogWarning("Save Page Now failed {Failures} captures running on {Host}, pausing saves there until {Until}",
                host.Failures, uri.Host, host.PausedUntil);
        }
    }

    private Host HostFor(Uri uri)
    {
        if (!_hosts.TryGetValue(uri.Host, out var host))
        {
            host = new Host();
            _hosts[uri.Host] = host;
        }

        return host;
    }

    private sealed class Host
    {
        public int Failures;
        public DateTimeOffset? PausedUntil;
        public DateTimeOffset LastSave = DateTimeOffset.MinValue;
        // The first slot not yet handed out.
        public DateTimeOffset NextSlot = DateTimeOffset.MinValue;
        // URL → the slot its job was told to come back at.
        public readonly Dictionary<string, DateTimeOffset> Slots = new();
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
