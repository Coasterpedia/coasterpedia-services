using System.Text.Json;
using CoasterpediaServices.ArchiveBot.Clients.Archive;
using CoasterpediaServices.ArchiveBot.Clients.Wayback;
using CoasterpediaServices.ArchiveBot.Events;
using CoasterpediaServices.ArchiveBot.Options;
using CoasterpediaServices.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Refit;

namespace CoasterpediaServices.ArchiveBot;

using WebClient_WebClient = Clients.WebClient.WebClient;

public static class ArchiveBotServiceCollectionExtensions
{
    public static IServiceCollection AddArchiveBot(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ArchiveLinkJob>();

        services.AddOptions<ArchiveBotConfig>()
            .Bind(configuration.GetSection(nameof(ArchiveBotConfig)))
            .ValidateOnStart();

        services.AddHttpClient<WebClient_WebClient>()
            .ConfigureHttpClient(c =>
            {
                c.DefaultRequestHeaders.UserAgent.ParseAdd("CoasterpediaArchiveBot/1.0 (https://coasterpedia.net)");
                c.Timeout = TimeSpan.FromSeconds(30);
            });

        services.AddRefitClient<IWaybackClient>(new RefitSettings
        {
            ContentSerializer = new SystemTextJsonContentSerializer(new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            })
        }).ConfigureHttpClient(c =>
        {
            c.BaseAddress = new Uri("https://archive.org");
            c.DefaultRequestHeaders.UserAgent.ParseAdd("CoasterpediaArchiveBot/1.0 (https://coasterpedia.net)");
            c.Timeout = TimeSpan.FromSeconds(65);
        });

        services.AddRefitClient<IArchiveClient>(new RefitSettings
        {
            HttpMessageHandlerFactory = () => new HttpClientHandler
            {
                AllowAutoRedirect = false
            }
        }).ConfigureHttpClient(c =>
        {
            c.BaseAddress = new Uri("https://web.archive.org");
            c.DefaultRequestHeaders.UserAgent.ParseAdd("CoasterpediaArchiveBot/1.0 (https://coasterpedia.net)");
            c.Timeout = TimeSpan.FromSeconds(240);
        });

        services.AddSingleton<IEventSubscriber, ArchiveBotSubscriber>();

        return services;
    }
}
