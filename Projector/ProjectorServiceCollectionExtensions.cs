using System.Net;
using CoasterpediaServices.Common;
using CoasterpediaServices.Common.Options;
using CoasterpediaServices.Projector.Data;
using CoasterpediaServices.Projector.Events;
using CoasterpediaServices.Projector.Jobs;
using CoasterpediaServices.Projector.Options;
using CoasterpediaServices.Projector.Push;
using CoasterpediaServices.Projector.State;
using CoasterpediaServices.Projector.Vocabulary;
using Hangfire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CoasterpediaServices.Projector;

public static class ProjectorServiceCollectionExtensions
{
    /// <summary>Needs an IConnectionMultiplexer registered by the host (it shares Hangfire's Redis).</summary>
    public static IServiceCollection AddProjector(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ProjectorConfig>()
            .Bind(configuration.GetSection(nameof(ProjectorConfig)));

        var coasterpediaConfig = configuration.GetRequiredSection(nameof(CoasterpediaConfig)).Get<CoasterpediaConfig>()
                                 ?? throw new InvalidOperationException("CoasterpediaConfig configuration is missing");

        services.AddHttpClient(VocabularySource.HttpClientName, c =>
            {
                c.BaseAddress = new Uri(coasterpediaConfig.BaseUrl.TrimEnd('/') + "/");
                c.DefaultRequestHeaders.UserAgent.ParseAdd("CoasterpediaProjector/1.0 (https://coasterpedia.net)");
                c.Timeout = TimeSpan.FromSeconds(30);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All });

        services.AddHttpClient(EdgeClient.HttpClientName, c =>
        {
            c.DefaultRequestHeaders.UserAgent.ParseAdd("CoasterpediaProjector/1.0 (https://coasterpedia.net)");
            c.Timeout = TimeSpan.FromSeconds(60);
        });

        services.AddSingleton<CargoReader>();
        services.AddSingleton<VocabularySource>();
        services.AddSingleton<ProjectionState>();
        services.AddSingleton<EdgeClient>();
        // Registration order is pass order; attractions stay first.
        services.AddSingleton<IKindProjection, AttractionProjection>();
        services.AddSingleton<IKindProjection, ParkProjection>();
        services.AddSingleton<IKindProjection, ManufacturerProjection>();
        services.AddSingleton<IKindProjection, ProductProjection>();
        services.AddSingleton<ProjectionService>();
        services.AddSingleton<ProjectorJobs>();
        services.AddSingleton<IEventSubscriber, ProjectorSubscriber>();

        return services;
    }

    /// <summary>Registers the recurring jobs when enabled, and removes them when not.</summary>
    public static void UseProjectorJobs(this IRecurringJobManager jobs, ProjectorConfig config)
    {
        if (!config.Enabled)
        {
            jobs.RemoveIfExists("projector-sweep");
            jobs.RemoveIfExists("projector-drain");
            jobs.RemoveIfExists("projector-settle");
            return;
        }

        jobs.AddOrUpdate<ProjectorJobs>("projector-sweep", j => j.Sweep(), config.SweepCron);
        jobs.AddOrUpdate<ProjectorJobs>("projector-drain", j => j.Drain(), "*/5 * * * *");
        jobs.AddOrUpdate<ProjectorJobs>("projector-settle", j => j.Settle(), "47 * * * *");
    }
}
