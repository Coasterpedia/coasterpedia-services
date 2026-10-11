using CoasterpediaServices.Common;
using CoasterpediaServices.Projector.Jobs;
using CoasterpediaServices.Projector.Options;
using CoasterpediaServices.Projector.State;
using Hangfire;
using Microsoft.Extensions.Options;

namespace CoasterpediaServices.Projector.Events;

/// <summary>
/// Freshness (DR-010 §4): every main-namespace page change marks its page
/// pending, and the first one schedules a flush a minute later. A burst of
/// edits to one page, or to many, exports once. Whether the page is an
/// attraction at all is the flush's question, not this one's.
/// </summary>
public class ProjectorSubscriber : IEventSubscriber
{
    private readonly ProjectionState _state;
    private readonly ProjectorConfig _config;

    public ProjectorSubscriber(ProjectionState state, IOptions<ProjectorConfig> config)
    {
        _state = state;
        _config = config.Value;
    }

    // Schema versions to confirm against the first live events: an unmatched
    // schema is dropped without a trace, and the nightly sweep would hide it.
    public EventFilter Filter { get; } = new()
    {
        Schemas =
        [
            "/mediawiki/revision/create/2.0.0",
            "/mediawiki/page/delete/1.0.0",
            "/mediawiki/page/undelete/1.0.0",
            "/mediawiki/page/move/1.0.0",
        ],
        Namespaces = ["0"],
    };

    public void OnMatched(EventBusEvent evt)
    {
        if (!_config.Enabled || evt.PageId is not { } pageId)
        {
            return;
        }

        if (_state.AddPending(pageId, _config.Debounce))
        {
            BackgroundJob.Schedule<ProjectorJobs>(j => j.FlushPending(), _config.Debounce);
        }
    }
}
