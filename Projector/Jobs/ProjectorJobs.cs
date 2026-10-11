using CoasterpediaServices.Projector.Push;
using CoasterpediaServices.Projector.State;
using Hangfire;

namespace CoasterpediaServices.Projector.Jobs;

/// <summary>
/// Every Projector job runs on one queue served by one worker, so a flush, a
/// sweep and a drain never overlap and never race on the pushed state.
/// </summary>
public class ProjectorJobs
{
    public const string Queue = "projector";

    private readonly ProjectionService _projection;
    private readonly ProjectionState _state;
    private readonly EdgeClient _edge;

    public ProjectorJobs(ProjectionService projection, ProjectionState state, EdgeClient edge)
    {
        _projection = projection;
        _state = state;
        _edge = edge;
    }

    /// <summary>Project every page edited since the last flush (debounced by ProjectorSubscriber).</summary>
    [Queue(Queue)]
    [AutomaticRetry(Attempts = 3)]
    public async Task FlushPending()
    {
        var ids = await _state.TakePendingAsync();
        if (ids.Length > 0)
        {
            await _projection.ProjectAsync(ids);
        }

        await _edge.DrainAsync();
    }

    /// <summary>Nightly correctness pass (DR-010 §4): catches re-stores no event announced. The first run is the backfill.</summary>
    [Queue(Queue)]
    [AutomaticRetry(Attempts = 1)]
    public async Task Sweep()
    {
        await _projection.ProjectAsync(null);
        await _edge.DrainAsync();
    }

    /// <summary>Hourly: pending runs that have stood their week join the settled view (DR-010 §6.3).</summary>
    [Queue(Queue)]
    [AutomaticRetry(Attempts = 1)]
    public async Task Settle()
    {
        await _projection.SettleDueAsync();
        await _edge.DrainAsync();
    }

    /// <summary>Retries the outbox while the edge is unreachable.</summary>
    [Queue(Queue)]
    [AutomaticRetry(Attempts = 0)]
    public Task Drain() => _edge.DrainAsync();
}
