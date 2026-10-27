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
            // An upload creates the page and is saved now, while the source still shows the licence the file was
            // imported under. A later edit waits behind everything else, so a bulk edit can't hold up citations.
            if (evt.ParentRevisionId == null)
            {
                BackgroundJob.Enqueue<ArchiveFileSourceJob>(job => job.Run(evt.PageTitle!));
            }
            else
            {
                BackgroundJob.Enqueue<ArchiveFileSourceJob>(job => job.Backfill(evt.PageTitle!));
            }

            return;
        }

        BackgroundJob.Schedule<ArchiveLinkJob>(job => job.Run(evt.PageTitle!, evt.RevisionId), ArchiveBotConfig.Delay);
    }
}
