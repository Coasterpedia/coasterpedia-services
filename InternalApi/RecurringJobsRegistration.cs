using CoasterpediaServices.ArchiveBot;
using CoasterpediaServices.ArchiveBot.Options;
using CoasterpediaServices.Projector;
using CoasterpediaServices.Projector.Options;
using Hangfire;
using Microsoft.Extensions.Options;

namespace CoasterpediaServices.InternalApi;

/// <summary>
/// Registers the recurring jobs once Redis will take them. Doing this inline at startup crashed the
/// process whenever Redis wasn't ready yet (e.g. "LOADING" right after both containers restart), and
/// a container that dies within seconds of starting isn't reliably restarted by Docker - so nothing,
/// ImageFetch included, came back until a manual restart. Retrying here keeps the API up meanwhile.
/// </summary>
public sealed class RecurringJobsRegistration(
    IRecurringJobManager jobs,
    IOptions<ProjectorConfig> projectorConfig,
    IOptions<ArchiveBotConfig> archiveBotConfig,
    ILogger<RecurringJobsRegistration> logger) : BackgroundService
{
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(1);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                jobs.UseProjectorJobs(projectorConfig.Value);
                jobs.UseArchiveBotJobs(archiveBotConfig.Value);
                if (attempt > 1)
                    logger.LogInformation("Registered recurring jobs after {Attempts} attempts", attempt);
                return;
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(e, "Couldn't register recurring jobs (attempt {Attempt}), retrying in {Delay}",
                    attempt, delay);
            }

            await Task.Delay(delay, stoppingToken);
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxDelay.Ticks));
        }
    }
}
