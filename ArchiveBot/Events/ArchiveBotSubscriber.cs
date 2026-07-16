using CoasterpediaServices.Common;
using Hangfire;

namespace CoasterpediaServices.ArchiveBot.Events;

public class ArchiveBotSubscriber : IEventSubscriber
{
    private static readonly TimeSpan Delay = TimeSpan.FromMinutes(20);

    public EventFilter Filter { get; } = new()
    {
        Schemas = ["/mediawiki/revision/create/2.0.0"],
        Namespaces = ["0"],
        ExcludeUsers = ["ArchiveBot"]
    };

    public void OnMatched(EventBusEvent evt)
    {
        BackgroundJob.Schedule<ArchiveLinkJob>(job => job.Run(evt.PageTitle!), Delay);
    }
}
