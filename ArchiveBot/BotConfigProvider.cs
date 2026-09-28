using System.Text.Json;
using CoasterpediaServices.ArchiveBot.Options;
using Microsoft.Extensions.Logging;
using WikiClientLibrary.Pages;
using WikiClientLibrary.Sites;

namespace CoasterpediaServices.ArchiveBot;

// Loads User:ArchiveBot/Config.json once and shares it between the archive jobs.
public class BotConfigProvider
{
    private BotConfig? _botConfig;
    private readonly ILogger<BotConfigProvider> _logger;

    public BotConfigProvider(ILogger<BotConfigProvider> logger)
    {
        _logger = logger;
    }

    public async Task<BotConfig> Get(WikiSite site)
    {
        if (_botConfig != null)
        {
            return _botConfig;
        }

        _logger.LogInformation("Fetching bot config");
        var configPage = new WikiPage(site, "User:ArchiveBot/Config.json");
        await configPage.RefreshAsync(PageQueryOptions.FetchContent);
        if (configPage.Content == null)
        {
            throw new InvalidOperationException("User:ArchiveBot/Config.json is missing");
        }

        _botConfig = JsonSerializer.Deserialize<BotConfig>(configPage.Content, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        })!;
        return _botConfig;
    }
}
