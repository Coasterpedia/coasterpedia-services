using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using CoasterpediaServices.ArchiveBot.Options;
using Microsoft.Extensions.Options;

namespace CoasterpediaServices.ArchiveBot.Clients.Archive;

public interface ISavePageNow
{
    Task<Capture> Save(string url);
}

// Save Page Now's capture API (SPN2), as the archive.org account whose S3 keys are configured: start a capture, then
// ask after it until it finishes. A capture that fails says why in status_ext, where anonymous GET /save/{url} only
// answered 520 "Job failed". Anonymous saves are also held to 200 a day, against 30,000 and 7 a minute with keys,
// which is what stopped every host at once after each day's ~190 saves (Nov 2026).
public class SavePageNow : ISavePageNow
{
    private readonly HttpClient _httpClient;
    private readonly ArchiveBotConfig _config;
    private readonly TimeProvider _time;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _captureTimeout;

    public SavePageNow(HttpClient httpClient, IOptions<ArchiveBotConfig> config, TimeProvider time)
        // SPN2 stops a capture at 2 minutes, so one still pending at 3 isn't coming back.
        : this(httpClient, config.Value, time, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(3))
    {
    }

    // For tests, which poll without waiting.
    internal SavePageNow(HttpClient httpClient, ArchiveBotConfig config, TimeProvider time, TimeSpan pollInterval,
        TimeSpan captureTimeout)
    {
        _httpClient = httpClient;
        _config = config;
        _time = time;
        _pollInterval = pollInterval;
        _captureTimeout = captureTimeout;
    }

    public async Task<Capture> Save(string url)
    {
        if (string.IsNullOrEmpty(_config.ArchiveAccessKey) || string.IsNullOrEmpty(_config.ArchiveSecretKey))
        {
            return Capture.Failed(Capture.NoKeys, "No archive.org keys are configured");
        }

        // skip_first_archive: we never ask whether this is the URL's first capture, and the check slows every one.
        var (status, start) = await Send(new HttpRequestMessage(HttpMethod.Post, "/save")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["url"] = url, ["skip_first_archive"] = "1" })
        });
        if (start?.JobId is not { } jobId)
        {
            return Capture.Failed(start?.StatusExt ?? $"http-{(int)status}", start?.Message);
        }

        var deadline = _time.GetUtcNow() + _captureTimeout;
        while (_time.GetUtcNow() < deadline)
        {
            await Task.Delay(_pollInterval, _time);
            // A status check that fails, 429 included, is asked again: the capture is still running.
            var (_, job) = await Send(new HttpRequestMessage(HttpMethod.Get, $"/save/status/{jobId}"));
            // The capture is of where the URL redirects to (original_url), but the snapshot keeps the URL asked for,
            // which plays back through the redirect.
            if (job?.Status == "success" && job.Timestamp != null)
            {
                return Capture.Saved(new Uri($"https://web.archive.org/web/{job.Timestamp}/{url}"));
            }

            if (job?.Status == "error")
            {
                return Capture.Failed(job.StatusExt ?? "error:unknown", job.Message);
            }
        }

        return Capture.Failed("timeout", $"still capturing after {_captureTimeout.TotalMinutes:0} minutes");
    }

    private async Task<(HttpStatusCode Status, Job? Body)> Send(HttpRequestMessage request)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("LOW", $"{_config.ArchiveAccessKey}:{_config.ArchiveSecretKey}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _httpClient.SendAsync(request);
        var content = await response.Content.ReadAsStringAsync();
        try
        {
            return (response.StatusCode, JsonSerializer.Deserialize<Job>(content));
        }
        catch (JsonException)
        {
            // An HTML error page, from Save Page Now or a proxy in front of it.
            return (response.StatusCode, null);
        }
    }

    private sealed record Job(
        [property: JsonPropertyName("job_id")] string? JobId,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("status_ext")] string? StatusExt,
        [property: JsonPropertyName("message")] string? Message,
        [property: JsonPropertyName("timestamp")] string? Timestamp);
}

// What decides what stops after a failed capture.
public enum CaptureFault
{
    None,
    // This URL only, so nothing pauses. A permanent one isn't tried again.
    Url,
    // The site, by Save Page Now's account. Counts towards pausing that host only.
    Site,
    // No reason given. Counts towards pausing the host, and every host when several fail together.
    Unexplained,
    // Save Page Now itself, or our account: every save pauses straight away.
    SaveService,
    // Save Page Now still counts our earlier captures as running: every save waits SessionLimitPause.
    Busy,
    // The worker waits out RateLimitCooldown and the job retries.
    RateLimited
}

// One capture: the snapshot, or Save Page Now's reason there isn't one, such as "error:blocked-url".
public sealed record Capture(Uri? Snapshot, string? Error, string? Message)
{
    public const string NoKeys = "no-keys";

    // The status_ext codes in the SPN2 API documentation, checked against what it answered (Nov 2026). Most describe
    // the target, whatever the documentation says: error:internal-server-error is the site answering 500,
    // error:too-many-requests a site answering 429, error:unauthorized a site that wants a login. A code not listed
    // here, error:no-request included, is the site's.
    private static readonly HashSet<string> SaveServiceErrors =
    [
        NoKeys, "error:blocked-client-ip", "error:max-daily-bandwidth", "error:max-daily-bandwidth-from-ip"
    ];

    // Save Page Now's own trouble or no reason: one URL can cause these, so only several sites failing them together
    // pauses every save.
    private static readonly HashSet<string> UnexplainedErrors =
    [
        "error:cannot-fetch", "error:capture-location-error", "error:celery", "error:job-failed",
        "error:no-browsers-available", "error:proxy-error", "error:unknown", "timeout"
    ];

    // error:no-request is a site turning Save Page Now away with a 403.
    private static readonly HashSet<string> PermanentErrors =
    [
        "error:bad-request", "error:blocked", "error:blocked-url", "error:filesize-limit", "error:ftp-access-denied",
        "error:invalid-url-syntax", "error:method-not-allowed", "error:no-access", "error:no-request", "error:not-found",
        "error:too-many-redirects", "error:unauthorized"
    ];

    public static Capture Saved(Uri snapshot) => new(snapshot, null, null);

    public static Capture Failed(string error, string? message) => new(null, error, message);

    // Save Page Now resting the host for a few minutes. It sometimes comes back as the status_ext's text.
    private bool HostResting => Error == "error:host-crawling-paused"
        || $"{Error} {Message}".Contains("crawling this host is paused", StringComparison.OrdinalIgnoreCase);

    // Captured 5 times today already, or the host resting: the URL and the site are fine, and a later try should work.
    private bool Later => Error == "error:too-many-daily-captures" || HostResting;

    public bool Permanent => Error != null && PermanentErrors.Contains(Error);

    // How long to wait before trying this URL again, when the failure is only a matter of waiting, so a job can come
    // back then without spending one of its retries.
    public TimeSpan? RetryAfter => this switch
    {
        { Error: "error:user-session-limit" } => ArchiveBotConfig.SessionLimitPause,
        { Error: "error:too-many-daily-captures" } => TimeSpan.FromHours(12),
        _ when HostResting => TimeSpan.FromMinutes(10),
        _ => null
    };

    public CaptureFault Fault => this switch
    {
        { Snapshot: not null } => CaptureFault.None,
        // Save Page Now itself answering 429: we're starting captures too fast.
        { Error: "http-429" } => CaptureFault.RateLimited,
        // The keys were refused: nothing saves until they're fixed.
        { Error: "http-401" or "http-403" } => CaptureFault.SaveService,
        { Error: "error:user-session-limit" } => CaptureFault.Busy,
        { Error: { } e } when SaveServiceErrors.Contains(e) => CaptureFault.SaveService,
        _ when Permanent || Later => CaptureFault.Url,
        // A bare 500 and the like: one source answered 500 every time (Nov 2026).
        { Error: { } e } when UnexplainedErrors.Contains(e) || !e.StartsWith("error:") => CaptureFault.Unexplained,
        _ => CaptureFault.Site
    };

    public string Reason => string.IsNullOrWhiteSpace(Message) ? Error ?? "" : $"{Error}: {Message.Trim()}";
}
