using CoasterpediaServices.ArchiveBot;
using CoasterpediaServices.Common;
using CoasterpediaServices.InternalApi.Auth;
using CoasterpediaServices.InternalApi.Events;
using CoasterpediaServices.InternalApi.Options;
using Hangfire;
using Hangfire.Redis.StackExchange;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<RedisOptions>()
    .Bind(builder.Configuration.GetSection(nameof(RedisOptions)))
    .ValidateOnStart();

builder.Services.AddArchiveBot(builder.Configuration);
builder.Services.AddCommon(builder.Configuration);

var redisOptions = builder.Configuration.GetRequiredSection(nameof(RedisOptions)).Get<RedisOptions>()
                    ?? throw new InvalidOperationException("RedisOptions configuration is missing");

builder.Services.AddHangfire(config => config
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseRedisStorage(redisOptions.ConnectionString, new RedisStorageOptions { Db = redisOptions.Db }));
builder.Services.AddHangfireServer();

builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHangfireDashboard("/", new DashboardOptions
{
    Authorization = [new CloudflareAccessAuthorizationFilter()]
});
app.MapEventIngest();

app.Run();
