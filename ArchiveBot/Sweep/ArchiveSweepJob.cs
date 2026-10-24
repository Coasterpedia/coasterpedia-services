using CoasterpediaServices.ArchiveBot.Options;
using CoasterpediaServices.Common.Wiki;
using Hangfire;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WikiClientLibrary.Generators;
using WikiClientLibrary.Sites;

namespace CoasterpediaServices.ArchiveBot.Sweep;

/// <summary>
/// Goes through the articles nobody edits, which the edit-triggered runs never reach. Each run queues
/// its share of the cycle onto the sweep queue, oldest-checked first, and nothing while the last share
/// is still queued. That queue waits behind the edit-triggered one on the same single worker, so the
/// sweep uses only spare time and Wayback saves stay paced.
/// </summary>
public sealed class ArchiveSweepJob
{
    private readonly WikiSiteAccessor _siteAccessor;
    private readonly BotConfigProvider _botConfigProvider;
    private readonly IPageCheckStore _pageChecks;
    private readonly IBackgroundJobClient _jobs;
    private readonly JobStorage _storage;
    private readonly TimeProvider _time;
    private readonly ArchiveBotConfig _config;
    private readonly ILogger<ArchiveSweepJob> _logger;

    public ArchiveSweepJob(WikiSiteAccessor siteAccessor, BotConfigProvider botConfigProvider, IPageCheckStore pageChecks,
        IBackgroundJobClient jobs, JobStorage storage, TimeProvider time, IOptions<ArchiveBotConfig> config, ILogger<ArchiveSweepJob> logger)
    {
        _siteAccessor = siteAccessor;
        _botConfigProvider = botConfigProvider;
        _pageChecks = pageChecks;
        _jobs = jobs;
        _storage = storage;
        _time = time;
        _config = config.Value;
        _logger = logger;
    }

    [AutomaticRetry(Attempts = 0)]
    public async Task Run()
    {
        var waiting = _storage.GetMonitoringApi().EnqueuedCount(ArchiveBotConfig.SweepQueue);
        if (waiting > 0)
        {
            _logger.LogInformation("Archive sweep: {Waiting} articles from the last batch still queued", waiting);
            return;
        }

        var site = await _siteAccessor.GetCoasterpedia(_config.BotUsername, _config.BotPassword);
        var botConfig = await _botConfigProvider.Get(site);
        var citing = await CitingArticles(site, botConfig.CitationTemplates);
        var sweep = _config.Sweep;
        var quota = SweepPlan.Quota(citing.Count, TimeSpan.FromMinutes(sweep.IntervalMinutes), sweep.Cycle);
        var batch = SweepPlan.Pick(citing, await _pageChecks.All(), _time.GetUtcNow(), sweep.Cycle, quota);

        if (batch.Gone.Count > 0)
        {
            await _pageChecks.Forget(batch.Gone);
        }

        foreach (var page in batch.Pages)
        {
            _jobs.Enqueue<ArchiveLinkJob>(job => job.RunFromSweep(page.Title));
        }

        _logger.LogInformation("Archive sweep: queued {Queued} of {Due} due, of {Citing} citing articles; forgot {Gone}",
            batch.Pages.Count, batch.Due, citing.Count, batch.Gone.Count);
    }

    // Articles transcluding any citation template the bot handles, once each.
    private static async Task<List<CitingArticle>> CitingArticles(WikiSite site, IEnumerable<string> citationTemplates)
    {
        var pages = new Dictionary<long, CitingArticle>();
        foreach (var template in citationTemplates)
        {
            var generator = new TranscludedInGenerator(site, "Template:" + template) { NamespaceIds = [0], PaginationSize = 500 };
            await foreach (var stub in generator.EnumItemsAsync())
            {
                pages.TryAdd(stub.Id, new CitingArticle(stub.Id, stub.Title!));
            }
        }

        return pages.Values.ToList();
    }
}
