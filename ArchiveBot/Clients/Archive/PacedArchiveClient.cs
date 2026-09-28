using System.Net;
using CoasterpediaServices.ArchiveBot.Options;
using Microsoft.Extensions.Logging;
using Refit;

namespace CoasterpediaServices.ArchiveBot.Clients.Archive;

// Wraps Wayback saves with pacing and a pause on 429. Only use from jobs on the single-worker archive
// queue: holding that worker is what holds back every other queued save.
public class PacedArchiveClient
{
    private readonly IArchiveClient _archiveClient;
    private readonly ILogger<PacedArchiveClient> _logger;

    public PacedArchiveClient(IArchiveClient archiveClient, ILogger<PacedArchiveClient> logger)
    {
        _archiveClient = archiveClient;
        _logger = logger;
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
        await Task.Delay(ArchiveBotConfig.SaveInterval);
        return response;
    }
}
