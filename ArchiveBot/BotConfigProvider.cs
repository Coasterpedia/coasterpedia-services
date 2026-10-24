using System.Text.Json;
using CoasterpediaServices.ArchiveBot.Options;
using Microsoft.Extensions.Logging;
using WikiClientLibrary.Pages;
using WikiClientLibrary.Sites;

namespace CoasterpediaServices.ArchiveBot;

// Loads User:ArchiveBot/Config.json and shares it between the archive jobs, fetching it again once it's
// MaxAge old so an edit there takes effect without a restart. If the page can't be read or no longer
// parses, the jobs keep the last good copy and the error is logged; with no good copy yet, they fail.
public class BotConfigProvider
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly TimeProvider _time;
    private readonly ILogger<BotConfigProvider> _logger;
    private BotConfig? _botConfig;
    private DateTimeOffset _fetched;

    public BotConfigProvider(TimeProvider time, ILogger<BotConfigProvider> logger)
    {
        _time = time;
        _logger = logger;
    }

    public Task<BotConfig> Get(WikiSite site) => Get(async () =>
    {
        var configPage = new WikiPage(site, "User:ArchiveBot/Config.json");
        await configPage.RefreshAsync(PageQueryOptions.FetchContent);
        return configPage.Content;
    });

    internal async Task<BotConfig> Get(Func<Task<string?>> fetch)
    {
        var now = _time.GetUtcNow();
        if (_botConfig != null && now - _fetched < MaxAge)
        {
            return _botConfig;
        }

        _logger.LogInformation("Fetching bot config");
        try
        {
            _botConfig = Parse(await fetch());
        }
        catch (Exception e) when (_botConfig != null)
        {
            _logger.LogError(e, "User:ArchiveBot/Config.json couldn't be used, keeping the copy from {Fetched}", _fetched);
        }

        // Also after a failure, so a broken page is tried again in MaxAge rather than on every citation.
        _fetched = now;
        return _botConfig;
    }

    private static BotConfig Parse(string? content)
    {
        if (content == null)
        {
            throw new InvalidOperationException("User:ArchiveBot/Config.json is missing");
        }

        var config = JsonSerializer.Deserialize<BotConfig>(content, JsonOptions);
        if (config?.CitationTemplates == null)
        {
            throw new InvalidOperationException("User:ArchiveBot/Config.json has no citationTemplates list");
        }

        return config;
    }
}
