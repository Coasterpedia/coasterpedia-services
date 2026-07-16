using System.Text.Json;
using CoasterpediaServices.ArchiveBot.Clients.Archive;
using CoasterpediaServices.ArchiveBot.Clients.Wayback;
using CoasterpediaServices.ArchiveBot.Clients.WebClient;
using CoasterpediaServices.ArchiveBot.Options;
using CoasterpediaServices.Common.Wiki;
using MarketAlly.IronWiki.Analysis;
using MarketAlly.IronWiki.Nodes;
using MarketAlly.IronWiki.Parsing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WikiClientLibrary.Pages;

namespace CoasterpediaServices.ArchiveBot;

public class ArchiveLinkJob
{
    private BotConfig? _botConfig;
    private readonly WikiSiteAccessor _siteAccessor;
    private readonly IWaybackClient _waybackClient;
    private readonly IArchiveClient _archiveClient;
    private readonly WebClient _webClient;
    private readonly ILogger<ArchiveLinkJob> _logger;
    private readonly ArchiveBotConfig _archiveBotConfig;

    public ArchiveLinkJob(WikiSiteAccessor siteAccessor, IWaybackClient waybackClient, IArchiveClient archiveClient, WebClient webClient,
        ILogger<ArchiveLinkJob> logger, IOptions<ArchiveBotConfig> archiveBotConfig)
    {
        _siteAccessor = siteAccessor;
        _waybackClient = waybackClient;
        _archiveClient = archiveClient;
        _webClient = webClient;
        _logger = logger;
        _archiveBotConfig = archiveBotConfig.Value;
    }

    public async Task Run(string pageName)
    {
        var site = await _siteAccessor.GetCoasterpedia(_archiveBotConfig.BotUsername, _archiveBotConfig.BotPassword);

        var page = new WikiPage(site, pageName);
        await page.RefreshAsync(PageQueryOptions.FetchContent | PageQueryOptions.ResolveRedirects);
        if (page.LastRevision != null && page.LastRevision.TimeStamp > DateTime.UtcNow.AddMinutes(-15))
        {
            return;
        }

        var parser = new WikitextParser();
        var analyzer = new DocumentAnalyzer();
        var document = await parser.ParseAsync(page.Content);
        var metadata = analyzer.Analyze(document);
        _logger.LogInformation("Found {ReferencesCount} references", metadata.References.Count);
        if (metadata.References.Count == 0)
        {
            return;
        }

        if (_botConfig == null)
        {
            _logger.LogInformation("Fetching bot config");
            var configPage = new WikiPage(site, "User:ArchiveBot/Config.json");
            await configPage.RefreshAsync(PageQueryOptions.FetchContent);
            if (configPage.Content != null)
            {
                _botConfig = JsonSerializer.Deserialize<BotConfig>(configPage.Content, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
        }

        var newContent = page.Content;
        var editedReferences = false;
        foreach (var reference in metadata.References)
        {
            var referenceMetadata = await parser.ParseAsync(reference.Content);
            var citationTemplate = referenceMetadata.EnumerateDescendants<Template>().FirstOrDefault();
            if (citationTemplate?.Name == null || !_botConfig!.CitationTemplates.Contains(citationTemplate.Name.ToString().Trim().ToLower()))
            {
                _logger.LogInformation("Citation invalid, skipping {CitationTemplate}", citationTemplate);
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

            var statusOverride = _botConfig.SiteConfig
                ?.FirstOrDefault(x => x.Key == uri.Host && !x.Value.Equals("IgnoreRedirect", StringComparison.CurrentCultureIgnoreCase)).Value;
            var ignoreRedirect =
                _botConfig.SiteConfig?.Any(x => x.Key == uri.Host && x.Value.Equals("IgnoreRedirect", StringComparison.CurrentCultureIgnoreCase)) ?? false;

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
                editedReferences = true;
            }

            var oldArchiveDateFormat = citationTemplate.Arguments.FirstOrDefault(x => x.Name?.ToString().Trim().ToLower() == "archivedate");
            if (oldArchiveDateFormat != null)
            {
                citationTemplate.Arguments.Remove(oldArchiveDateFormat);
                UpdateArgument(citationTemplate, "archive-date", oldArchiveDateFormat.Value.ToString());
                editedReferences = true;
            }

            var urlStatus = citationTemplate.Arguments.FirstOrDefault(x => x.Name?.ToString().ToLower() == "url-status")?.Value.ToString().Trim();
            var hasArchive = !string.IsNullOrWhiteSpace(citationTemplate.Arguments.FirstOrDefault(x => x.Name?.ToString().Trim().ToLower() == "archive-url")
                ?.Value.ToString().Trim());
            var urlAvailable = statusOverride == null ? await _webClient.CheckUrlAvailable(uri.ToString()) : new StatusResponse(false, null);

            if (!hasArchive)
            {
                _logger.LogInformation("Reference does not currently contain archive link, checking web archive");
                var availableResponse = await _waybackClient.GetAvailable(url);
                hasArchive = availableResponse.ArchivedSnapshots?.Closest?.Available == true;

                var isRedirect = IsRedirect(urlAvailable, url, out var newUrl);
                if (!hasArchive && isRedirect && statusOverride == null && !ignoreRedirect)
                {
                    _logger.LogInformation("Checking web archive for redirect link");
                    availableResponse = await _waybackClient.GetAvailable(newUrl!);
                    hasArchive = availableResponse.ArchivedSnapshots?.Closest?.Available == true;
                }

                if (hasArchive)
                {
                    _logger.LogInformation("Archive found, updating reference");
                    UpdateArgument(citationTemplate, "archive-url", availableResponse.ArchivedSnapshots.Closest.Url);
                    UpdateArgument(citationTemplate, "archive-date",
                        DateTime.ParseExact(availableResponse.ArchivedSnapshots.Closest.Timestamp, "yyyyMMddHHmmss", null).ToString("yyyy-MM-dd"));
                    editedReferences = true;
                }
                else if (statusOverride == null)
                {
                    _logger.LogInformation("Archive not found, saving page");
                    var saveResponse = await _archiveClient.SavePage(newUrl ?? url);
                    var location = saveResponse.Headers.Location;

                    if (location != null)
                    {
                        _logger.LogInformation("Saving page successful, updating reference");
                        UpdateArgument(citationTemplate, "archive-url", location.ToString());
                        UpdateArgument(citationTemplate, "archive-date", DateTime.UtcNow.ToString("yyyy-MM-dd"));
                        hasArchive = true;
                        editedReferences = true;
                    }
                    else
                    {
                        _logger.LogInformation("Saving page failed");
                    }
                }
                else
                {
                    _logger.LogInformation("Archive not found and manual status set, skipping archive");
                }
            }

            if (statusOverride != null && (urlStatus == null || !urlStatus.Equals(statusOverride, StringComparison.CurrentCultureIgnoreCase)))
            {
                _logger.LogInformation("URL status is manually overwritten, updating to {siteConfig}", statusOverride);
                UpdateArgument(citationTemplate, "url-status", statusOverride.ToLower());
                editedReferences = true;
            }
            else if (urlAvailable.Available == null && urlStatus == null)
            {
                _logger.LogInformation("URL status is unknown, updating to live");
                UpdateArgument(citationTemplate, "url-status", "live");
                editedReferences = true;
            }
            else if (urlAvailable.Available == true && urlStatus != "live")
            {
                _logger.LogInformation("Updating URL status to live");
                UpdateArgument(citationTemplate, "url-status", "live");
                editedReferences = true;
            }
            else if (urlAvailable.Available == false && urlStatus is null or "live")
            {
                _logger.LogInformation("Updating URL status to dead");
                UpdateArgument(citationTemplate, "url-status", "dead");
                editedReferences = true;
            }

            if (urlAvailable.Available == false && !hasArchive && !newContent.Contains(reference.Content + "{{Dead link}}"))
            {
                _logger.LogInformation("Adding dead link template");
                newContent = newContent.Replace(reference.Content, citationTemplate + "{{Dead link}}");
                editedReferences = true;
            }
            else if ((urlAvailable.Available == true || hasArchive) && newContent.Contains(reference.Content + "{{Dead link}}"))
            {
                _logger.LogInformation("Removing dead link template");
                newContent = newContent.Replace(reference.Content + "{{Dead link}}", citationTemplate.ToString());
                editedReferences = true;
            }
            else
            {
                newContent = newContent.Replace(reference.Content, citationTemplate.ToString());
            }
        }

        if (!editedReferences)
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

    private static void UpdateArgument(Template citationTemplate, string key, string value)
    {
        var currentValue = citationTemplate.Arguments.Where(x => x.Name?.ToString().Trim().ToLower() == key).ToList();
        switch (currentValue.Count)
        {
            case > 1:
            {
                foreach (var currentUrlStatus in currentValue)
                {
                    citationTemplate.Arguments.Remove(currentUrlStatus);
                }

                break;
            }
            case 1:
            {
                if (currentValue.Single().Value.ToString() == value)
                {
                    return;
                }

                citationTemplate.Arguments.Remove(currentValue.Single());

                break;
            }
        }

        citationTemplate.Arguments.Add(ArgumentUtilities.CreateArgument(key, value));
    }

    private bool IsRedirect(StatusResponse statusResponse, string originalUrl, out string? newUrl)
    {
        newUrl = statusResponse.ResponseMessage?.RequestMessage?.RequestUri?.ToString();
        return newUrl != null && originalUrl != newUrl;
    }
}