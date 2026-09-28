using System.Text.RegularExpressions;
using CoasterpediaServices.ArchiveBot.Clients.Archive;
using CoasterpediaServices.ArchiveBot.Options;
using CoasterpediaServices.Common.Wiki;
using Hangfire;
using MarketAlly.IronWiki.Nodes;
using MarketAlly.IronWiki.Parsing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WikiClientLibrary.Pages;
using static CoasterpediaServices.ArchiveBot.ArgumentUtilities;

namespace CoasterpediaServices.ArchiveBot;

// Archives the page an image was imported from, so the licence it was shown under at upload
// time is preserved, and records the snapshot on the file page's {{Information}} template.
public partial class ArchiveFileSourceJob
{
    private static readonly string[] InformationTemplates = ["information", "image documentation"];

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

    [Queue(ArchiveBotConfig.ArchiveQueue)]
    public async Task Run(string pageName)
    {
        var site = await _siteAccessor.GetCoasterpedia(_archiveBotConfig.BotUsername, _archiveBotConfig.BotPassword);

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

        var statusOverride = botConfig.SiteConfig?.GetValueOrDefault(sourceUrl.Host);
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
            throw new InvalidOperationException($"Saving {sourceUrl} failed with status {saveResponse.StatusCode}");
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

    private static string? GetArgument(Template template, string key) =>
        template.Arguments.FirstOrDefault(x => x.Name?.ToString().Trim().ToLower() == key)?.Value.ToString().Trim();

    private static string NormaliseName(Template template) =>
        template.Name?.ToString().Trim().Replace('_', ' ').ToLower() ?? "";

    [GeneratedRegex(@"https?://[^\s\[\]|{}<>""]+")]
    private static partial Regex UrlRegex();
}
