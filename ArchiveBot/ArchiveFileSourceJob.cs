using System.Text.RegularExpressions;
using CoasterpediaServices.ArchiveBot.Clients.Archive;
using CoasterpediaServices.ArchiveBot.Options;
using CoasterpediaServices.Common.Wiki;
using Hangfire;
using MarketAlly.IronWiki.Nodes;
using MarketAlly.IronWiki.Parsing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WikiClientLibrary.Generators;
using WikiClientLibrary.Pages;
using WikiClientLibrary.Sites;
using static CoasterpediaServices.ArchiveBot.ArgumentUtilities;

namespace CoasterpediaServices.ArchiveBot;

// Archives the page an image was imported from, so the licence it was shown under at upload
// time is preserved, and records the snapshot on the file page's {{Information}} template.
// Retries over about three days rather than Hangfire's default seven hours: a site can turn Save Page Now away for
// hours at a time (Wikimapia has), and a requeue days later then succeeds.
[AutomaticRetry(Attempts = 10, DelaysInSeconds = [300, 900, 1800, 3600, 7200, 14400, 28800, 43200, 86400, 86400])]
public partial class ArchiveFileSourceJob
{
    private static readonly string[] InformationTemplates = ["information", "image documentation"];

    // A file page created longer ago than this is an older file being edited, not an upload.
    private static readonly TimeSpan UploadWindow = TimeSpan.FromDays(1);

    private readonly WikiSiteAccessor _siteAccessor;
    private readonly PacedArchiveClient _archiveClient;
    private readonly BotConfigProvider _botConfigProvider;
    private readonly ILogger<ArchiveFileSourceJob> _logger;
    private readonly ArchiveBotConfig _archiveBotConfig;

    public ArchiveFileSourceJob(WikiSiteAccessor siteAccessor, PacedArchiveClient archiveClient, BotConfigProvider botConfigProvider,
        ILogger<ArchiveFileSourceJob> logger, IOptions<ArchiveBotConfig> archiveBotConfig)
    {
        _siteAccessor = siteAccessor;
        _archiveClient = archiveClient;
        _botConfigProvider = botConfigProvider;
        _logger = logger;
        _archiveBotConfig = archiveBotConfig.Value;
    }

    // An upload, saved straight away while the source still shows the licence the file was imported under.
    // An older file sent here is moved to the backfill queue, as jobs queued before the subscriber split were.
    [Queue(ArchiveBotConfig.ArchiveQueue)]
    public async Task Run(string pageName)
    {
        var site = await _siteAccessor.GetCoasterpedia(_archiveBotConfig.BotUsername, _archiveBotConfig.BotPassword);
        if (await CreatedAt(site, pageName) is { } created && created < DateTime.UtcNow - UploadWindow)
        {
            _logger.LogInformation("{PageName} was created {Created:yyyy-MM-dd}, moving to the backfill queue", pageName, created);
            BackgroundJob.Enqueue<ArchiveFileSourceJob>(job => job.Backfill(pageName));
            return;
        }

        await Archive(site, pageName);
    }

    // An edit to an older file page. Runs only when no citation or sweep job is waiting: a bulk edit of file pages
    // queues one of these per page.
    [Queue(ArchiveBotConfig.FileBackfillQueue)]
    public async Task Backfill(string pageName)
    {
        var site = await _siteAccessor.GetCoasterpedia(_archiveBotConfig.BotUsername, _archiveBotConfig.BotPassword);
        await Archive(site, pageName);
    }

    private static async Task<DateTime?> CreatedAt(WikiSite site, string pageName)
    {
        var generator = new RevisionsGenerator(site, new WikiPageStub(pageName)) { TimeAscending = true, PaginationSize = 1 };
        await foreach (var revision in generator.EnumItemsAsync())
        {
            return revision.TimeStamp;
        }

        return null;
    }

    private async Task Archive(WikiSite site, string pageName)
    {
        var page = new WikiPage(site, pageName);
        await page.RefreshAsync(PageQueryOptions.FetchContent | PageQueryOptions.ResolveRedirects);
        var content = page.Content;
        if (!page.Exists || string.IsNullOrWhiteSpace(content))
        {
            _logger.LogInformation("File page {PageName} does not exist or is empty", pageName);
            return;
        }

        var parser = new WikitextParser();
        var document = await parser.ParseAsync(content);
        var templates = document.EnumerateDescendants<Template>().ToList();

        var information = templates.FirstOrDefault(x => InformationTemplates.Contains(NormaliseName(x)));
        if (information == null)
        {
            _logger.LogInformation("No information template on {PageName}, skipping", pageName);
            return;
        }

        if (!string.IsNullOrWhiteSpace(GetArgument(information, "archive-url")))
        {
            _logger.LogInformation("Source already archived for {PageName}", pageName);
            return;
        }

        var botConfig = await _botConfigProvider.Get(site);
        var sourceUrl = FindSourceUrl(templates, information, botConfig.ImageSourceTemplates ?? []);
        if (sourceUrl == null)
        {
            _logger.LogInformation("No source URL on {PageName}, skipping", pageName);
            return;
        }

        var statusOverride = botConfig.SiteRule(sourceUrl.Host);
        if (statusOverride?.ToLower() is "ignore")
        {
            _logger.LogInformation("Skipping source {SourceUrl}, URL set to ignore", sourceUrl);
            return;
        }

        var originalTemplate = information.ToString();
        if (!content.Contains(originalTemplate))
        {
            _logger.LogWarning("Information template on {PageName} did not round-trip, skipping", pageName);
            return;
        }

        _logger.LogInformation("Saving source {SourceUrl} for {PageName}", sourceUrl, pageName);
        var saveResponse = await _archiveClient.SavePage(sourceUrl.ToString());
        var location = saveResponse.Headers?.Location;
        if (location == null)
        {
            // Throw rather than skip so Hangfire retries: the snapshot is only useful while the source still
            // shows the licence the file was uploaded under.
            throw new InvalidOperationException($"Saving {sourceUrl} failed with {PacedArchiveClient.FailureReason(saveResponse)}");
        }

        // Keep block-formatted templates ({{Information\n|a=\n|b=\n}}) one argument per line.
        var suffix = information.Arguments.LastOrDefault()?.Value.ToString().EndsWith('\n') == true ? "\n" : "";
        UpdateArgument(information, "archive-url", location + suffix);
        UpdateArgument(information, "archive-date", DateTime.UtcNow.ToString("yyyy-MM-dd") + suffix);

        _logger.LogInformation("Saving page");
        await page.EditAsync(new WikiPageEditOptions
        {
            Content = content.Replace(originalTemplate, information.ToString()),
            Summary = "Archive image source",
            Bot = true
        });
    }

    // Prefer the source card's link (it's what the licence was verified against), then the
    // Information url=, then a link in its source=.
    private static Uri? FindSourceUrl(List<Template> templates, Template information, List<string> sourceTemplates)
    {
        var candidates = templates
            .Where(x => sourceTemplates.Contains(NormaliseName(x)))
            .Select(x => x.Arguments.FirstOrDefault(a => a.Name == null || a.Name.ToString().Trim() == "1")?.Value.ToString())
            .Append(GetArgument(information, "url"))
            .Append(GetArgument(information, "source"));

        foreach (var candidate in candidates)
        {
            var match = candidate == null ? null : UrlRegex().Match(candidate);
            if (match is { Success: true } && Uri.TryCreate(match.Value, UriKind.Absolute, out var uri))
            {
                return uri;
            }
        }

        return null;
    }

    private static string NormaliseName(Template template) =>
        template.Name?.ToString().Trim().Replace('_', ' ').ToLower() ?? "";

    [GeneratedRegex(@"https?://[^\s\[\]|{}<>""]+")]
    private static partial Regex UrlRegex();
}
