using CoasterpediaServices.ArchiveBot;
using CoasterpediaServices.Common;
using CoasterpediaServices.ImageFetch;
using CoasterpediaServices.InternalApi.Auth;
using CoasterpediaServices.InternalApi.Options;
using Hangfire;
using Hangfire.Redis.StackExchange;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<RedisOptions>()
    .Bind(builder.Configuration.GetSection(nameof(RedisOptions)))
    .ValidateOnStart();

builder.Services.AddArchiveBot(builder.Configuration);
builder.Services.AddCommon(builder.Configuration);
builder.Services.AddImageFetch(builder.Configuration);

builder.Services.AddProblemDetails();
builder.Services.AddControllers()
    // ImageFetch's controller lives in a separate class library, so it needs to be added
    // as an application part explicitly - it isn't picked up by default part discovery.
    .AddApplicationPart(typeof(ImageFetchController).Assembly);

var redisOptions = builder.Configuration.GetRequiredSection(nameof(RedisOptions)).Get<RedisOptions>()
                    ?? throw new InvalidOperationException("RedisOptions configuration is missing");

// builder.Services.AddHangfire(config => config
//     .UseSimpleAssemblyNameTypeSerializer()
//     .UseRecommendedSerializerSettings()
//     .UseRedisStorage(redisOptions.ConnectionString, new RedisStorageOptions { Db = redisOptions.Db }));
// builder.Services.AddHangfireServer();

var app = builder.Build();

// app.UseHangfireDashboard("/hangfire", new DashboardOptions
// {
//     Authorization = [new CloudflareAccessAuthorizationFilter()]
// });

app.MapControllers();

app.Run();
