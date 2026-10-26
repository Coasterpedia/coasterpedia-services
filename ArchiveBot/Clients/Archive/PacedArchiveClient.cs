using System.Net;
using System.Text.RegularExpressions;
using CoasterpediaServices.ArchiveBot.Options;
using Microsoft.Extensions.Logging;
using Refit;

namespace CoasterpediaServices.ArchiveBot.Clients.Archive;

// Wraps Wayback saves with pacing and a pause on 429. Only use from jobs on the single-worker archive
// queue: holding that worker is what holds back every other queued save.
public partial class PacedArchiveClient
{
    private readonly IArchiveClient _archiveClient;
    private readonly ILogger<PacedArchiveClient> _logger;
    private readonly TimeSpan _saveInterval;

    public PacedArchiveClient(IArchiveClient archiveClient, ILogger<PacedArchiveClient> logger)
        : this(archiveClient, logger, ArchiveBotConfig.SaveInterval)
    {
    }

    // For tests, which save without pacing.
    internal PacedArchiveClient(IArchiveClient archiveClient, ILogger<PacedArchiveClient> logger, TimeSpan saveInterval)
    {
        _archiveClient = archiveClient;
        _logger = logger;
        _saveInterval = saveInterval;
    }

    public async Task<IApiResponse> SavePage(string url)
    {
        var response = await _archiveClient.SavePage(url);
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
