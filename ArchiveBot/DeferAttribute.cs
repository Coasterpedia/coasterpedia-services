using Hangfire.Common;
using Hangfire.States;

namespace CoasterpediaServices.ArchiveBot;

// A job throws DeferredException to come back later without spending one of its retries: waiting its turn isn't a
// failed try. Runs before AutomaticRetry (Order 20), which only acts on a job still headed for the failed state.
public sealed class DeferAttribute : JobFilterAttribute, IElectStateFilter
{
    public DeferAttribute()
    {
        Order = 0;
    }

    public void OnStateElection(ElectStateContext context)
    {
        if (context.CandidateState is FailedState { Exception: DeferredException deferred })
        {
            context.CandidateState = new ScheduledState(deferred.Until.UtcDateTime) { Reason = deferred.Message };
        }
    }
}

public sealed class DeferredException(DateTimeOffset until, string message) : Exception(message)
{
    public DateTimeOffset Until { get; } = until;
}
