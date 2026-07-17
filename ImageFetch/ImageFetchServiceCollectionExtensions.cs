using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CoasterpediaServices.ImageFetch.Auth;
using CoasterpediaServices.ImageFetch.Clients.Commons;
using CoasterpediaServices.ImageFetch.Clients.Flickr;
using CoasterpediaServices.ImageFetch.Clients.Geograph;
using CoasterpediaServices.ImageFetch.Fetchers;
using CoasterpediaServices.ImageFetch.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Refit;

namespace CoasterpediaServices.ImageFetch;

public static class ImageFetchServiceCollectionExtensions
{
    private const string UserAgent = "CoasterpediaImageFetch/1.0 (https://coasterpedia.net)";

    public static IServiceCollection AddImageFetch(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<FlickrConfig>()
            .Bind(configuration.GetSection(nameof(FlickrConfig)))
            .ValidateOnStart();

        services.AddOptions<CommonsConfig>()
            .Bind(configuration.GetSection(nameof(CommonsConfig)))
            .ValidateOnStart();

        var commonsConfig = configuration.GetRequiredSection(nameof(CommonsConfig)).Get<CommonsConfig>()
                             ?? throw new InvalidOperationException("CommonsConfig configuration is missing");

        services.AddRefitClient<IGeographClient>(new RefitSettings
            {
                ContentSerializer = new SystemTextJsonContentSerializer(new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                })
            })
            .ConfigureHttpClient(c =>
            {
                c.BaseAddress = new Uri("https://api.geograph.org.uk");
                c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            });

        services.AddRefitClient<IFlickrClient>(new RefitSettings
            {
                ContentSerializer = new SystemTextJsonContentSerializer(new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                })
            })
            .ConfigureHttpClient(c =>
            {
                c.BaseAddress = new Uri("https://api.flickr.com");
                c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            });

        services.AddHttpClient<CommonsClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All
            })
            .ConfigureHttpClient(c =>
            {
                c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
                c.DefaultRequestHeaders.Referrer = new Uri("https://coasterpedia.net");
                c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", commonsConfig.AccessToken);
            });
        services.AddSingleton<CommonsSiteAccessor>();

        services.AddSingleton<FlickrLicenseCache>();

        services.AddHttpClient<GeographFetcher>()
            .ConfigureHttpClient(c => c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent));
        services.AddTransient<ISourceFetcher>(sp => sp.GetRequiredService<GeographFetcher>());

        services.AddHttpClient<FlickrFetcher>()
            .ConfigureHttpClient(c => c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent));
        services.AddTransient<ISourceFetcher>(sp => sp.GetRequiredService<FlickrFetcher>());

        services.AddTransient<CommonsFetcher>();
        services.AddTransient<ISourceFetcher>(sp => sp.GetRequiredService<CommonsFetcher>());

        services.AddSingleton<ImageFetchDispatcher>();

        services.AddTransient<FlickrAlbumFetcher>();
        services.AddTransient<ICollectionFetcher>(sp => sp.GetRequiredService<FlickrAlbumFetcher>());

        services.AddTransient<CommonsCategoryFetcher>();
        services.AddTransient<ICollectionFetcher>(sp => sp.GetRequiredService<CommonsCategoryFetcher>());

        services.AddSingleton<ImageFetchCollectionDispatcher>();

        // No cookie jar: the endpoint controls exactly which cookie header is forwarded, per request.
        services.AddHttpClient("WikiUserInfoGate")
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                UseCookies = false,
                AllowAutoRedirect = false
            })
            .ConfigureHttpClient(c => c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent));

        services.AddScoped<SameOriginCsrfFilter>();
        services.AddScoped<WikiUserInfoGateFilter>();

        return services;
    }
}
