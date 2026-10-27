using CoasterpediaServices.ArchiveBot;
using CoasterpediaServices.ArchiveBot.Options;
using CoasterpediaServices.Common;
using CoasterpediaServices.ImageFetch;
using CoasterpediaServices.InternalApi;
using CoasterpediaServices.InternalApi.Auth;
using CoasterpediaServices.InternalApi.Options;
using CoasterpediaServices.Projector;
using CoasterpediaServices.Projector.Jobs;
using Hangfire;
using Hangfire.Redis.StackExchange;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<RedisOptions>()
    .Bind(builder.Configuration.GetSection(nameof(RedisOptions)))
    .ValidateOnStart();

builder.Services.AddArchiveBot(builder.Configuration);
builder.Services.AddCommon(builder.Configuration);
builder.Services.AddImageFetch(builder.Configuration);
builder.Services.AddProjector(builder.Configuration);

builder.Services.AddProblemDetails();
builder.Services.AddControllers()
    // ImageFetch's controller lives in a separate class library, so it needs to be added
    // as an application part explicitly - it isn't picked up by default part discovery.
    .AddApplicationPart(typeof(ImageFetchController).Assembly);

var redisOptions = builder.Configuration.GetRequiredSection(nameof(RedisOptions)).Get<RedisOptions>()
                    ?? throw new InvalidOperationException("RedisOptions configuration is missing");

// The Projector keeps its state beside Hangfire's, under projector: keys.
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect($"{redisOptions.ConnectionString},defaultDatabase={redisOptions.Db},abortConnect=false"));

// Hangfire shares that connection, so it too tolerates a Redis that isn't up yet (abortConnect=false).
builder.Services.AddHangfire((services, config) => config
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseRedisStorage(services.GetRequiredService<IConnectionMultiplexer>(), new RedisStorageOptions { Db = redisOptions.Db }));
builder.Services.AddHangfireServer();
// Wayback saves run one at a time on their own server so a bulk upload doesn't trip archive.org's rate limiter.
// Redis storage takes queues strictly in this order: edits and uploads, then older files' sources, then the sweep.
// The sweep refills its queue whenever it empties, so anything behind it would never run; a file backlog ends.
builder.Services.AddHangfireServer(o =>
{
    o.ServerName = "archive";
    o.Queues = [ArchiveBotConfig.ArchiveQueue, ArchiveBotConfig.FileBackfillQueue, ArchiveBotConfig.SweepQueue];
    o.WorkerCount = 1;
});
// One worker, so a flush, a sweep and a drain never overlap (ProjectorJobs).
builder.Services.AddHangfireServer(o =>
{
    o.ServerName = "projector";
    o.Queues = [ProjectorJobs.Queue];
    o.WorkerCount = 1;
});
builder.Services.AddHostedService<RecurringJobsRegistration>();

var app = builder.Build();

app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = [new CloudflareAccessAuthorizationFilter()]
});

app.MapControllers();

app.Run();
