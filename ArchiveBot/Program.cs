using CoasterpediaServices.ArchiveBot;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// ArchiveBot is a self-contained library: InternalApi hosts the web app, Hangfire
// server, and event-ingest endpoint, and composes this project in via AddArchiveBot().
// This entry point exists only as a local testing convenience, running the
// archive-link job directly against a page and exiting.
if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: dotnet run -- \"Page Name\"");
    return 1;
}

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Development"}.json", optional: true)
    .AddUserSecrets(typeof(ArchiveLinkJob).Assembly, optional: true)
    .AddEnvironmentVariables()
    .Build();

var services = new ServiceCollection();
services.AddLogging(b => b.AddConsole().AddConfiguration(configuration.GetSection("Logging")));
services.AddArchiveBot(configuration);

var provider = services.BuildServiceProvider();
var job = provider.GetRequiredService<ArchiveLinkJob>();
await job.Run(args[0]);
return 0;
