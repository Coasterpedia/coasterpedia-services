using CoasterpediaServices.ArchiveBot.Options;
using CoasterpediaServices.Common;
using Hangfire;

namespace CoasterpediaServices.ArchiveBot.Events;

public class ArchiveBotSubscriber : IEventSubscriber
{
    private const string FileNamespace = "6";

    public EventFilter Filter { get; } = new()
    {
        Schemas = ["/mediawiki/revision/create/2.0.0"],
        Namespaces = ["0", FileNamespace],
        ExcludeUsers = ["ArchiveBot"]
    };

    public void OnMatched(EventBusEvent evt)
    {
        if (evt.Namespace == FileNamespace)
        {
            // No delay: the source page should be captured as close to upload as possible,
            // while it still shows the licence the file was imported under.
            BackgroundJob.Enqueue<ArchiveFileSourceJob>(job => job.Run(evt.PageTitle!));
            return;
        }

        BackgroundJob.Schedule<ArchiveLinkJob>(job => job.Run(evt.PageTitle!), ArchiveBotConfig.Delay);
    }
}
