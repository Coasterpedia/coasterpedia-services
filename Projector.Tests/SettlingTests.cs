using System.Text.Json.Nodes;
using CoasterpediaServices.Common;
using CoasterpediaServices.Projector.State;
using Xunit;

namespace CoasterpediaServices.Projector.Tests;

/// <summary>DR-010 §6.3, case by case.</summary>
public class SettlingTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-10-11T12:00:00Z");
    private static readonly TimeSpan Week = TimeSpan.FromDays(7);

    private static RecordState Settled(string hash = "a", long revision = 10) => new(hash, revision, T0, hash, null);

    private static RecordState PendingOver(string settled, string hash, long revision, DateTimeOffset clock) =>
        new(hash, revision, T0, settled, clock);

    [Fact]
    public void A_trusted_change_settles_at_once()
    {
        var (next, op) = Settling.Decide(Settled(), "b", 11, trusted: true, T0);
        Assert.Equal(SettleOp.Settled, op);
        Assert.Equal(new RecordState("b", 11, T0, "b", null), next);
    }

    [Fact]
    public void An_untrusted_change_waits_and_keeps_the_settled_version()
    {
        var (next, op) = Settling.Decide(Settled(), "b", 11, trusted: false, T0);
        Assert.Equal(SettleOp.Pending, op);
        Assert.Equal("a", next.SettledHash);
        Assert.Equal(T0, next.ClockFrom);
    }

    [Fact]
    public void Every_edit_restarts_the_clock()
    {
        var later = T0.AddDays(6);
        var (next, _) = Settling.Decide(PendingOver("a", "b", 11, T0), "c", 12, trusted: false, later);
        Assert.Equal(later, next.ClockFrom);
    }

    [Fact]
    public void A_trusted_edit_on_top_of_a_pending_run_does_not_vouch_for_it()
    {
        var (next, op) = Settling.Decide(PendingOver("a", "b", 11, T0), "c", 12, trusted: true, T0.AddDays(1));
        Assert.Equal(SettleOp.Pending, op);
        Assert.Equal("a", next.SettledHash);
    }

    [Fact]
    public void A_re_store_into_a_pending_run_keeps_its_clock()
    {
        var (next, op) = Settling.Decide(PendingOver("a", "b", 11, T0), "b2", 11, trusted: true, T0.AddDays(3));
        Assert.Equal(SettleOp.Pending, op);
        Assert.Equal(T0, next.ClockFrom);
    }

    [Fact]
    public void A_revert_to_the_settled_content_discards_the_run()
    {
        var (next, op) = Settling.Decide(PendingOver("a", "b", 11, T0), "a", 12, trusted: false, T0.AddHours(1));
        Assert.Equal(SettleOp.Discard, op);
        Assert.False(next.Pending);
        Assert.Null(next.ClockFrom);
        Assert.Equal(12, next.Revision);
    }

    [Fact]
    public void A_new_page_by_an_untrusted_editor_has_never_settled()
    {
        var (next, op) = Settling.Decide(null, "a", 1, trusted: false, T0);
        Assert.Equal(SettleOp.Pending, op);
        Assert.Null(next.SettledHash);
        Assert.True(next.Pending);
    }

    [Fact]
    public void A_run_is_due_a_week_after_its_last_change()
    {
        var run = PendingOver("a", "b", 11, T0);
        Assert.False(Settling.IsDue(run, Week, T0.AddDays(7).AddSeconds(-1)));
        Assert.True(Settling.IsDue(run, Week, T0.AddDays(7)));
        Assert.False(Settling.IsDue(Settled(), Week, T0.AddYears(1)));
    }

    [Fact]
    public void Trusted_needs_a_trusted_event_for_exactly_this_revision()
    {
        var last = Settled(revision: 10);
        Assert.True(Settling.IsTrusted(last, 11, new PageTrust(11, 0)));
        Assert.False(Settling.IsTrusted(last, 12, new PageTrust(11, 0))); // event for 12 missing: waits
        Assert.False(Settling.IsTrusted(null, 1, PageTrust.None));
        Assert.True(Settling.IsTrusted(null, 1, new PageTrust(1, 0)));
    }

    [Fact]
    public void Any_untrusted_revision_since_the_last_projection_makes_it_wait()
    {
        var last = Settled(revision: 10);
        Assert.False(Settling.IsTrusted(last, 12, new PageTrust(12, 11)));
        Assert.True(Settling.IsTrusted(last, 12, new PageTrust(12, 9)));
    }

    [Fact]
    public void A_re_store_with_no_new_revision_is_trusted()
    {
        Assert.True(Settling.IsTrusted(Settled(revision: 10), 10, PageTrust.None));
        Assert.True(Settling.IsTrusted(Settled(revision: 10), 10, new PageTrust(0, 10))); // absorbed by a sweep: prose only
    }

    [Fact]
    public void A_flush_absorbs_only_a_revision_it_knows_was_trusted()
    {
        var last = Settled(revision: 10);
        Assert.True(Settling.CanAbsorb(last, 11, new PageTrust(11, 0), sweep: false));
        Assert.False(Settling.CanAbsorb(last, 11, new PageTrust(0, 11), sweep: false));
        // No event yet: Cargo may hold the edit back, and absorbing would let it in later as a "re-store".
        Assert.False(Settling.CanAbsorb(last, 11, PageTrust.None, sweep: false));
        Assert.False(Settling.CanAbsorb(last, 12, new PageTrust(12, 11), sweep: false));
    }

    [Fact]
    public void The_sweep_absorbs_any_newer_revision()
    {
        var last = Settled(revision: 10);
        Assert.True(Settling.CanAbsorb(last, 11, new PageTrust(0, 11), sweep: true));
        Assert.True(Settling.CanAbsorb(last, 11, PageTrust.None, sweep: true));
        Assert.False(Settling.CanAbsorb(last, 10, PageTrust.None, sweep: true));
    }

    [Fact]
    public void State_round_trips_and_pre_settlement_state_reads_as_settled()
    {
        var pending = PendingOver("a", "b", 11, T0);
        Assert.Equal(pending, RecordState.Decode(pending.Encode()));
        Assert.Equal(new RecordState("x", 5, T0, null, T0), RecordState.Decode(new RecordState("x", 5, T0, null, T0).Encode()));
        Assert.Equal(new RecordState("x", 5, T0, "x", null), RecordState.Decode($"x|5|{T0.ToUnixTimeSeconds()}"));
    }

    [Fact]
    public void Events_carry_the_revision_and_the_performers_groups()
    {
        var evt = EventBusEvent.FromJson(JsonNode.Parse("""
            {"$schema":"/mediawiki/revision/create/2.0.0","page_id":171,"page_title":"Gold_Rusher","page_namespace":0,
             "rev_id":5001,"performer":{"user_text":"Alex","user_groups":["*","user","autoconfirmed","sysop"]}}
            """));
        Assert.Equal(5001, evt!.RevisionId);
        Assert.Contains("sysop", evt.UserGroups!);

        var anon = EventBusEvent.FromJson(JsonNode.Parse("""{"page_title":"X","performer":{"user_text":"1.2.3.4"}}"""));
        Assert.Null(anon!.UserGroups);
        Assert.Null(anon.RevisionId);
    }
}
