using CoasterpediaServices.ArchiveBot.Options;
using Microsoft.Extensions.Logging;

namespace CoasterpediaServices.ArchiveBot.Clients.Archive;

// Wraps Wayback saves with pacing, a pause on 429, a pause on a host whose captures keep failing, a shorter pause on
// every host when Save Page Now is at fault or captures fail across hosts, and save slots
// HostSaveInterval apart on each host for jobs that can wait. Only use from jobs on the single-worker archive queue:
// holding that worker is what holds back every other queued save. State is in memory, so a restart forgets pauses
// and slots.
public class PacedArchiveClient
{
    private readonly ISavePageNow _savePageNow;
    private readonly ILogger<PacedArchiveClient> _logger;
    private readonly TimeSpan _saveInterval;
    private readonly TimeSpan _hostInterval;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, Host> _hosts = new(StringComparer.OrdinalIgnoreCase);
    // Hosts with a failed capture since the last capture that worked anywhere.
    private readonly HashSet<string> _failingHosts = new(StringComparer.OrdinalIgnoreCase);
    // Saves to every host, after captures failed across hosts.
    private DateTimeOffset? _pausedUntil;

    public PacedArchiveClient(ISavePageNow savePageNow, ILogger<PacedArchiveClient> logger, TimeProvider time)
        : this(savePageNow, logger, ArchiveBotConfig.SaveInterval, ArchiveBotConfig.HostSaveInterval, time)
    {
    }

    // For tests, which save without pacing.
    internal PacedArchiveClient(ISavePageNow savePageNow, ILogger<PacedArchiveClient> logger, TimeSpan saveInterval,
        TimeSpan hostInterval, TimeProvider time)
    {
        _savePageNow = savePageNow;
        _logger = logger;
        _saveInterval = saveInterval;
        _hostInterval = hostInterval;
        _time = time;
    }

    // When saves to the URL's host start again, while they're paused. Check before SavePage.
    public DateTimeOffset? PausedUntil(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? PausedUntil(HostFor(uri), _time.GetUtcNow()) : null;

    // The later of the host's pause and the pause on every host, while either lasts.
    private DateTimeOffset? PausedUntil(Host host, DateTimeOffset now)
    {
        var until = new[] { host.PausedUntil, _pausedUntil }.Max();
        return until > now ? until : null;
    }

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
        var pausedUntil = PausedUntil(host, now);
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

    public async Task<Capture> SavePage(string url)
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

        var capture = await _savePageNow.Save(url);
        Record(url, capture);
        if (capture.Fault == CaptureFault.RateLimited)
        {
            // Throw after the pause so Hangfire retries the job, rather than every other queued
            // save hitting the limiter in turn and using up its retries.
            _logger.LogWarning("Wayback rate limit hit, pausing saves for {Cooldown}", ArchiveBotConfig.RateLimitCooldown);
            await Task.Delay(ArchiveBotConfig.RateLimitCooldown);
            throw new InvalidOperationException($"Saving {url} was rate limited");
        }

        // Pace saves so a bulk upload doesn't trip archive.org's rate limiter.
        await Task.Delay(_saveInterval);
        return capture;
    }

    // A save after a pause ends gets one try: another failure pauses again straight away.
    private void Record(string url, Capture capture)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return;
        }

        var host = HostFor(uri);
        switch (capture.Fault)
        {
            case CaptureFault.None:
                host.Failures = 0;
                host.PausedUntil = null;
                _failingHosts.Clear();
                _pausedUntil = null;
                return;
            case CaptureFault.SaveService:
                _pausedUntil = _time.GetUtcNow() + ArchiveBotConfig.GlobalPause;
                _logger.LogWarning("Save Page Now refused a capture ({Reason}), pausing all saves until {Until}",
                    capture.Reason, _pausedUntil);
                return;
            case CaptureFault.Busy:
                _pausedUntil = _time.GetUtcNow() + ArchiveBotConfig.SessionLimitPause;
                _logger.LogInformation("Save Page Now has too many of our captures running, pausing all saves until {Until}", _pausedUntil);
                return;
            case CaptureFault.Url or CaptureFault.RateLimited:
                return;
        }

        host.Failures++;
        host.PausedUntil = null;
        if (capture.Fault == CaptureFault.Site)
        {
            // Save Page Now reached the site to say what went wrong, so Save Page Now itself is working. An old
            // backlog fails site after site like this (dead and blocking sources) without anything else being wrong.
            _failingHosts.Clear();
            _pausedUntil = null;
        }
        else
        {
            _failingHosts.Add(uri.Host);
            if (_failingHosts.Count >= ArchiveBotConfig.FailingHostsBeforeGlobalPause)
            {
                // The hosts weren't at fault, so none of them stays paused for it.
                foreach (var name in _failingHosts)
                {
                    var failing = _hosts[name];
                    failing.Failures = 0;
                    failing.PausedUntil = null;
                }

                _pausedUntil = _time.GetUtcNow() + ArchiveBotConfig.GlobalPause;
                _logger.LogWarning("Save Page Now failed captures on {Hosts} without a reason, pausing all saves until {Until}",
                    string.Join(", ", _failingHosts), _pausedUntil);
                return;
            }
        }

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
}
