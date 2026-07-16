using System.Net;
using CoasterpediaServices.Common.Options;
using CoasterpediaServices.Common.Wiki;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CoasterpediaServices.Common;

public static class CommonServiceCollectionExtensions
{
    public static IServiceCollection AddCommon(this IServiceCollection services, IConfiguration configuration)
    {
        var coasterpediaConfig = configuration.GetRequiredSection(nameof(CoasterpediaConfig)).Get<CoasterpediaConfig>()
                                 ?? throw new InvalidOperationException("CoasterpediaConfig configuration is missing");

        services.AddOptions<CoasterpediaConfig>()
            .Bind(configuration.GetSection(nameof(CoasterpediaConfig)))
            .ValidateOnStart();
        
        services.AddHttpClient<CoasterpediaClient>(config =>
            {
                config.BaseAddress = new Uri(coasterpediaConfig.BaseUrl);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All
            })
            .ConfigureHttpClient(c => { c.DefaultRequestHeaders.UserAgent.ParseAdd("ArchiveBot/1.0"); });
        
        services.AddSingleton<WikiSiteAccessor>();
        
        return services;
    }
}