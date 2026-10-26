using System.Text.Json;
using CoasterpediaServices.ArchiveBot.Clients.Archive;
using CoasterpediaServices.ArchiveBot.Clients.Wayback;
using CoasterpediaServices.ArchiveBot.Events;
using CoasterpediaServices.ArchiveBot.Links;
using CoasterpediaServices.ArchiveBot.Options;
using CoasterpediaServices.ArchiveBot.Sweep;
using CoasterpediaServices.Common;
using Hangfire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Refit;

namespace CoasterpediaServices.ArchiveBot;

using WebClient_WebClient = Clients.WebClient.WebClient;

public static class ArchiveBotServiceCollectionExtensions
{
    public static IServiceCollection AddArchiveBot(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ArchiveLinkJob>();
        services.AddSingleton<ArchiveFileSourceJob>();
        services.AddSingleton<BotConfigProvider>();
        services.AddSingleton<PacedArchiveClient>();
        services.AddSingleton<PacedWaybackClient>();
        services.AddSingleton<LinkChecker>();
        services.AddSingleton<ArchiveSweepJob>();
        // InternalApi's IConnectionMultiplexer; the local runner registers in-memory stores first.
        services.TryAddSingleton<ILinkStateStore, RedisLinkStateStore>();
        services.TryAddSingleton<IPageCheckStore, RedisPageCheckStore>();
        services.TryAddSingleton(TimeProvider.System);

        services.AddOptions<ArchiveBotConfig>()
            .Bind(configuration.GetSection(nameof(ArchiveBotConfig)))
            .ValidateOnStart();

        services.AddHttpClient<WebClient_WebClient>()
            .ConfigureHttpClient(c =>
            {
                c.DefaultRequestHeaders.UserAgent.ParseAdd("CoasterpediaArchiveBot/1.0 (https://coasterpedia.net)");
                c.Timeout = TimeSpan.FromSeconds(30);
            });

        services.AddRefitGeneratedClient<IWaybackClient>(new RefitSettings
        {
            ContentSerializer = new SystemTextJsonContentSerializer(new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            })
        }).ConfigureHttpClient(c =>
        {
            c.BaseAddress = new Uri("https://web.archive.org");
            c.DefaultRequestHeaders.UserAgent.ParseAdd("CoasterpediaArchiveBot/1.0 (https://coasterpedia.net)");
            c.Timeout = TimeSpan.FromSeconds(65);
        });

        services.AddHttpClient<WaybackPlayback>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
            .ConfigureHttpClient(c =>
            {
                c.BaseAddress = new Uri("https://web.archive.org");
                c.DefaultRequestHeaders.UserAgent.ParseAdd("CoasterpediaArchiveBot/1.0 (https://coasterpedia.net)");
                c.Timeout = TimeSpan.FromSeconds(65);
            });

        services.AddRefitGeneratedClient<IArchiveClient>(new RefitSettings
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

    // Turning the sweep off stops new batches; articles already queued still run.
    public static void UseArchiveBotJobs(this IRecurringJobManager jobs, ArchiveBotConfig config)
    {
        if (!config.Sweep.Enabled)
        {
            jobs.RemoveIfExists("archive-sweep");
            return;
        }

        jobs.AddOrUpdate<ArchiveSweepJob>("archive-sweep", j => j.Run(), Cron.MinuteInterval(config.Sweep.IntervalMinutes));
    }
}
