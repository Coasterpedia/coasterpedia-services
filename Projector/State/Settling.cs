namespace CoasterpediaServices.Projector.State;

/// <summary>What the Projector knows of one record: the latest view as pushed, and the settled one (DR-010 §6.3).</summary>
/// <param name="Revision">The page's revision when last projected. A prose edit moves it without changing Hash.</param>
/// <param name="SettledHash">Content of the settled view; null if the record has never settled.</param>
/// <param name="ClockFrom">When the pending run's clock last restarted; null when nothing is pending.</param>
public sealed record RecordState(string Hash, long Revision, DateTimeOffset UpdatedAt, string? SettledHash, DateTimeOffset? ClockFrom)
{
    public bool Pending => SettledHash != Hash;

    public string Encode() =>
        $"{Hash}|{Revision}|{UpdatedAt.ToUnixTimeSeconds()}|{SettledHash}|{ClockFrom?.ToUnixTimeSeconds()}";

    public static RecordState? Decode(string? value)
    {
        var parts = value?.Split('|');
        return parts?.Length switch
        {
            // Pushed before the settled view existed: settled as it stands, as the edge's migration says too.
            3 => new RecordState(parts[0], long.Parse(parts[1]), Unix(parts[2]), parts[0], null),
            5 => new RecordState(parts[0], long.Parse(parts[1]), Unix(parts[2]),
                parts[3].Length > 0 ? parts[3] : null, parts[4].Length > 0 ? Unix(parts[4]) : null),
            _ => null,
        };
    }

    private static DateTimeOffset Unix(string s) => DateTimeOffset.FromUnixTimeSeconds(long.Parse(s));
}

/// <summary>The newest revision of a page that EventBus reported from a trusted performer, and from an untrusted one. 0 for none.</summary>
public sealed record PageTrust(long Trusted, long Untrusted)
{
    public static readonly PageTrust None = new(0, 0);
}

public enum SettleOp
{
    /// <summary>The new content is both latest and settled.</summary>
    Settled,

    /// <summary>The new content is latest; settled keeps what it had, if anything.</summary>
    Pending,

    /// <summary>The content went back to the settled version: drop the pending run.</summary>
    Discard,
}

/// <summary>The rule of DR-010 §6.3, without Redis or Cargo.</summary>
public static class Settling
{
    /// <summary>
    /// Whether a change seen at <paramref name="revision"/> is all trusted work.
    /// Any untrusted revision since the last projection makes it wait, even
    /// under a trusted edit. With none, a change with no new revision is a
    /// re-store (a template or the Projector's own mapping changed) and
    /// trusted; one with a new revision needs a trusted event for exactly
    /// that revision. A change whose event never arrived therefore waits.
    /// </summary>
    public static bool IsTrusted(RecordState? last, long revision, PageTrust trust)
    {
        var seen = last?.Revision ?? 0;
        if (trust.Untrusted > seen)
        {
            return false;
        }

        return revision == seen || trust.Trusted == revision;
    }

    /// <summary>
    /// An unchanged record takes the page's newer revision, so a later re-store
    /// is recognised as one. A minute after an edit, Cargo may not have stored
    /// it yet, and its event may not have arrived, so a flush takes only a
    /// revision it knows was trusted. By the nightly sweep the edit is stored,
    /// so a record still unchanged then was only a prose edit.
    /// </summary>
    public static bool CanAbsorb(RecordState last, long revision, PageTrust trust, bool sweep) =>
        revision > last.Revision
        && (sweep || (trust.Trusted == revision && trust.Untrusted <= last.Revision));

    public static (RecordState Next, SettleOp Op) Decide(RecordState? last, string hash, long revision, bool trusted,
        DateTimeOffset now)
    {
        // A revert, rollback or fix-forward that restores the settled content:
        // the vandal version never reaches the settled view.
        if (last is { Pending: true } && last.SettledHash == hash)
        {
            return (new RecordState(hash, revision, now, hash, null), SettleOp.Discard);
        }

        // Editing on top of a pending run is not vouching for it.
        if (trusted && last is not { Pending: true })
        {
            return (new RecordState(hash, revision, now, hash, null), SettleOp.Settled);
        }

        // Every edit restarts the clock; a re-store into a pending run is not an edit.
        var clock = last is { Pending: true, ClockFrom: { } from } && revision == last.Revision ? from : now;
        return (new RecordState(hash, revision, now, last?.SettledHash, clock), SettleOp.Pending);
    }

    public static bool IsDue(RecordState state, TimeSpan settleAfter, DateTimeOffset now) =>
        state is { Pending: true, ClockFrom: { } from } && from + settleAfter <= now;
}
