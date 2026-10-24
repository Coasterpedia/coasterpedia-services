using System.Globalization;
using System.Text.RegularExpressions;
using CoasterpediaServices.ArchiveBot.Clients.Archive;
using CoasterpediaServices.ArchiveBot.Clients.Wayback;
using CoasterpediaServices.ArchiveBot.Clients.WebClient;
using CoasterpediaServices.ArchiveBot.Options;
using CoasterpediaServices.Common.Wiki;
using Hangfire;
using MarketAlly.IronWiki.Analysis;
using MarketAlly.IronWiki.Nodes;
using MarketAlly.IronWiki.Parsing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WikiClientLibrary.Pages;
using static CoasterpediaServices.ArchiveBot.ArgumentUtilities;

namespace CoasterpediaServices.ArchiveBot;

public partial class ArchiveLinkJob
{
    private static readonly string[] CitationDateFormats =
        ["yyyy-MM-dd", "d MMMM yyyy", "d MMM yyyy", "MMMM d, yyyy", "MMM d, yyyy", "yyyy-MM", "MMMM yyyy", "MMM yyyy", "yyyy"];

    private readonly WikiSiteAccessor _siteAccessor;
    private readonly IWaybackClient _waybackClient;
    private readonly PacedArchiveClient _archiveClient;
    private readonly WebClient _webClient;
    private readonly BotConfigProvider _botConfigProvider;
    private readonly ILogger<ArchiveLinkJob> _logger;
    private readonly ArchiveBotConfig _archiveBotConfig;

    public ArchiveLinkJob(WikiSiteAccessor siteAccessor, IWaybackClient waybackClient, PacedArchiveClient archiveClient, WebClient webClient,
        BotConfigProvider botConfigProvider, ILogger<ArchiveLinkJob> logger, IOptions<ArchiveBotConfig> archiveBotConfig)
    {
        _siteAccessor = siteAccessor;
        _waybackClient = waybackClient;
        _archiveClient = archiveClient;
        _webClient = webClient;
        _botConfigProvider = botConfigProvider;
        _logger = logger;
        _archiveBotConfig = archiveBotConfig.Value;
    }

    // Jobs scheduled before the revision ID was passed in still name this overload.
    [Queue(ArchiveBotConfig.ArchiveQueue)]
    public Task Run(string pageName) => Run(pageName, null);

    [Queue(ArchiveBotConfig.ArchiveQueue)]
    public async Task Run(string pageName, long? revisionId)
    {
        var site = await _siteAccessor.GetCoasterpedia(_archiveBotConfig.BotUsername, _archiveBotConfig.BotPassword);

        var page = new WikiPage(site, pageName);
        await page.RefreshAsync(PageQueryOptions.FetchContent | PageQueryOptions.ResolveRedirects);
        // A later edit scheduled its own run, which covers this one.
        if (revisionId != null && page.LastRevisionId != revisionId)
        {
            _logger.LogInformation("{PageName} has been edited since revision {RevisionId}, skipping", pageName, revisionId);
            return;
        }

        var botConfig = await _botConfigProvider.Get(site);
        var newContent = await RewriteReferences(page.Content, botConfig);
        if (newContent == page.Content)
        {
            _logger.LogInformation("No references edited");
            return;
        }

        _logger.LogInformation("Saving page");
        await page.EditAsync(new WikiPageEditOptions
        {
            Content = newContent,
            Summary = "Add archive links",
            Bot = true
        });
    }

    // Checks and archives every citation on the page, returning the new wikitext. Saves to the Wayback
    // Machine as it goes, but never edits the wiki.
    public async Task<string> RewriteReferences(string content, BotConfig botConfig)
    {
        var parser = new WikitextParser();
        var analyzer = new DocumentAnalyzer();
        var document = await parser.ParseAsync(content);
        var metadata = analyzer.Analyze(document);
        _logger.LogInformation("Found {ReferencesCount} references", metadata.References.Count);

        var newContent = content;
        foreach (var reference in metadata.References)
        {
            var referenceMetadata = await parser.ParseAsync(reference.Content);
            var citationTemplate = referenceMetadata.EnumerateDescendants<Template>().FirstOrDefault();
            if (citationTemplate?.Name == null || !botConfig.CitationTemplates.Contains(citationTemplate.Name.ToString().Trim().ToLower()))
            {
                _logger.LogInformation("Citation invalid, skipping {CitationTemplate}", citationTemplate);
                continue;
            }

            // The reference can hold text and other templates around the citation; only the citation is rewritten.
            var originalTemplate = citationTemplate.ToString();
            if (!reference.Content.Contains(originalTemplate))
            {
                _logger.LogWarning("Citation {CitationTemplate} did not round-trip, skipping", citationTemplate);
                continue;
            }

            var url = citationTemplate.Arguments.FirstOrDefault(x => x.Name?.ToString().ToLower() == "url")?.Value.ToString().Trim();
            if (url == null)
            {
                _logger.LogInformation("Skipping reference {CitationTemplate}, no URL in citation", citationTemplate);
                continue;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                _logger.LogInformation("Skipping reference {CitationTemplate}, URL invalid", citationTemplate);
                continue;
            }

            var statusOverride = botConfig.SiteConfig
                ?.FirstOrDefault(x => x.Key == uri.Host && !x.Value.Equals("IgnoreRedirect", StringComparison.CurrentCultureIgnoreCase)).Value;
            var ignoreRedirect =
                botConfig.SiteConfig?.Any(x => x.Key == uri.Host && x.Value.Equals("IgnoreRedirect", StringComparison.CurrentCultureIgnoreCase)) ?? false;

            if (statusOverride?.ToLower() is "ignore")
            {
                _logger.LogInformation("Skipping reference {CitationTemplate}, URL set to ignore", citationTemplate);
                continue;
            }

            _logger.LogInformation("Processing reference {CitationTemplate}", citationTemplate);

            var oldArchiveFormat = citationTemplate.Arguments.FirstOrDefault(x => x.Name?.ToString().Trim().ToLower() == "archiveurl");
            if (oldArchiveFormat != null)
            {
                citationTemplate.Arguments.Remove(oldArchiveFormat);
                UpdateArgument(citationTemplate, "archive-url", oldArchiveFormat.Value.ToString());
            }

            var oldArchiveDateFormat = citationTemplate.Arguments.FirstOrDefault(x => x.Name?.ToString().Trim().ToLower() == "archivedate");
            if (oldArchiveDateFormat != null)
            {
                citationTemplate.Arguments.Remove(oldArchiveDateFormat);
                UpdateArgument(citationTemplate, "archive-date", oldArchiveDateFormat.Value.ToString());
            }

            var urlStatus = citationTemplate.Arguments.FirstOrDefault(x => x.Name?.ToString().ToLower() == "url-status")?.Value.ToString().Trim();
            var hasArchive = !string.IsNullOrWhiteSpace(citationTemplate.Arguments.FirstOrDefault(x => x.Name?.ToString().Trim().ToLower() == "archive-url")
                ?.Value.ToString().Trim());
            var urlAvailable = statusOverride == null ? await _webClient.CheckUrlAvailable(uri.ToString()) : new StatusResponse(false, null);

            var redirectTarget = ignoreRedirect ? null : RedirectTarget(urlAvailable, uri);
            // A page that now lands on the site's home page has usually been removed: neither the home
            // page nor its archive stands in for it.
            var redirectsToRoot = redirectTarget != null && IsSiteRoot(redirectTarget) && !IsSiteRoot(uri);

            if (!hasArchive)
            {
                _logger.LogInformation("Reference does not currently contain archive link, checking web archive");
                var accessDate = ParseCitationDate(GetArgument(citationTemplate, "access-date") ?? GetArgument(citationTemplate, "accessdate"));
                var publishDate = ParseCitationDate(GetArgument(citationTemplate, "date"));
                var snapshot = await _waybackClient.FindSnapshot(url, accessDate, publishDate);

                if (snapshot == null && redirectTarget != null && !redirectsToRoot)
                {
                    _logger.LogInformation("Checking web archive for redirect link");
                    snapshot = await _waybackClient.FindSnapshot(redirectTarget.ToString(), accessDate, publishDate);
                }

                if (snapshot != null)
                {
                    _logger.LogInformation("Archive found, updating reference");
                    UpdateArgument(citationTemplate, "archive-url", snapshot.Url);
                    UpdateArgument(citationTemplate, "archive-date", snapshot.Captured.ToString("yyyy-MM-dd"));
                    hasArchive = true;
                }
                else if (statusOverride != null)
                {
                    _logger.LogInformation("Archive not found and manual status set, skipping archive");
                }
                else if (urlAvailable.Available == false || redirectsToRoot)
                {
                    // Saving now would capture the error or the home page, not the cited page.
                    _logger.LogInformation("Archive not found and URL is dead or redirects to the home page, not saving");
                }
                else
                {
                    _logger.LogInformation("Archive not found, saving page");
                    var saveResponse = await _archiveClient.SavePage(redirectTarget?.ToString() ?? url);
                    var location = saveResponse.Headers?.Location;

                    if (location != null)
                    {
                        _logger.LogInformation("Saving page successful, updating reference");
                        UpdateArgument(citationTemplate, "archive-url", location.ToString());
                        UpdateArgument(citationTemplate, "archive-date", DateTime.UtcNow.ToString("yyyy-MM-dd"));
                        hasArchive = true;
                    }
                    else
                    {
                        _logger.LogInformation("Saving page failed");
                    }
                }
            }

            if (statusOverride != null && (urlStatus == null || !urlStatus.Equals(statusOverride, StringComparison.CurrentCultureIgnoreCase)))
            {
                _logger.LogInformation("URL status is manually overwritten, updating to {siteConfig}", statusOverride);
                UpdateArgument(citationTemplate, "url-status", statusOverride.ToLower());
            }
            else if (urlAvailable.Available == null && urlStatus == null)
            {
                _logger.LogInformation("URL status is unknown, updating to live");
                UpdateArgument(citationTemplate, "url-status", "live");
            }
            else if (urlAvailable.Available == true && urlStatus != "live")
            {
                _logger.LogInformation("Updating URL status to live");
                UpdateArgument(citationTemplate, "url-status", "live");
            }
            else if (urlAvailable.Available == false && urlStatus is null or "live")
            {
                _logger.LogInformation("Updating URL status to dead");
                UpdateArgument(citationTemplate, "url-status", "dead");
            }

            var newTemplate = citationTemplate.ToString();
            var referenceContent = reference.Content.Replace(originalTemplate, newTemplate);
            var taggedDead = DeadLinkRegex().IsMatch(referenceContent);
            if (urlAvailable.Available == false && !hasArchive && !taggedDead)
            {
                _logger.LogInformation("Adding dead link template");
                referenceContent = referenceContent.Replace(newTemplate, newTemplate + "{{Dead link}}");
            }
            else if ((urlAvailable.Available == true || hasArchive) && taggedDead)
            {
                _logger.LogInformation("Removing dead link template");
                referenceContent = DeadLinkRegex().Replace(referenceContent, "");
            }

            if (referenceContent != reference.Content)
            {
                newContent = newContent.Replace(reference.Content, referenceContent);
            }
        }

        return newContent;
    }

    // Compared as URIs, so "https://example.com" and "https://example.com/" are not a redirect.
    private static Uri? RedirectTarget(StatusResponse statusResponse, Uri original)
    {
        var final = statusResponse.ResponseMessage?.RequestMessage?.RequestUri;
        return final != null && final != original ? final : null;
    }

    private static bool IsSiteRoot(Uri uri) => uri.AbsolutePath is "/" or "";

    private static DateTime? ParseCitationDate(string? value) =>
        DateTime.TryParseExact(value, CitationDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date)
            ? date
            : null;

    [GeneratedRegex(@"\{\{\s*[Dd]ead[ _]link\s*(\|[^{}]*)?\}\}")]
    private static partial Regex DeadLinkRegex();
}
