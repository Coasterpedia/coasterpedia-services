using CoasterpediaServices.ArchiveBot.Links;
using Xunit;

namespace CoasterpediaServices.ArchiveBot.Tests;

public class LinkStateTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-10-24T12:00:00Z");

    private static LinkState After(params CheckResult[] results)
    {
        LinkState? state = null;
        var now = T0;
        foreach (var result in results)
        {
            state = LinkStates.Decide(state, result, null, now);
            now += LinkStates.RecheckInterval;
        }

        return state!;
    }

    [Fact]
    public void A_first_success_is_alive()
    {
        Assert.Equal(LinkHealth.Alive, After(CheckResult.Success).Health);
    }

    [Fact]
    public void One_failure_is_dying_not_dead()
    {
        var state = After(CheckResult.Failure);
        Assert.Equal(LinkHealth.Dying, state.Health);
        Assert.Equal(1, state.Failures);
    }

    [Fact]
    public void Three_failures_in_a_row_are_dead()
    {
        Assert.Equal(LinkHealth.Dying, After(CheckResult.Failure, CheckResult.Failure).Health);
        Assert.Equal(LinkHealth.Dead, After(CheckResult.Failure, CheckResult.Failure, CheckResult.Failure).Health);
    }

    [Fact]
    public void A_success_between_failures_starts_the_count_again()
    {
        var state = After(CheckResult.Failure, CheckResult.Failure, CheckResult.Success, CheckResult.Failure, CheckResult.Failure);
        Assert.Equal(LinkHealth.Dying, state.Health);
        Assert.Equal(2, state.Failures);
    }

    [Fact]
    public void One_success_revives_a_dead_link()
    {
        var state = After(CheckResult.Failure, CheckResult.Failure, CheckResult.Failure, CheckResult.Success);
        Assert.Equal(LinkHealth.Alive, state.Health);
        Assert.Equal(0, state.Failures);
    }

    [Fact]
    public void An_inconclusive_check_keeps_what_was_known()
    {
        Assert.Equal(LinkHealth.Unknown, After(CheckResult.Inconclusive).Health);
        Assert.Equal(LinkHealth.Alive, After(CheckResult.Success, CheckResult.Inconclusive).Health);

        var dying = After(CheckResult.Failure, CheckResult.Inconclusive);
        Assert.Equal(LinkHealth.Dying, dying.Health);
        Assert.Equal(1, dying.Failures);
    }

    [Fact]
    public void An_inconclusive_check_still_moves_the_clock()
    {
        var state = LinkStates.Decide(After(CheckResult.Failure), CheckResult.Inconclusive, null, T0.AddDays(5));
        Assert.Equal(T0.AddDays(5), state.LastChecked);
    }

    [Fact]
    public void Only_a_success_keeps_a_redirect()
    {
        Assert.Equal("https://a.example/new", LinkStates.Decide(null, CheckResult.Success, "https://a.example/new", T0).RedirectTarget);
        var moved = LinkStates.Decide(null, CheckResult.Success, "https://a.example/new", T0);
        Assert.Null(LinkStates.Decide(moved, CheckResult.Failure, null, T0.AddDays(3)).RedirectTarget);
    }

    [Theory]
    [InlineData(LinkHealth.Alive, 6, false)]
    [InlineData(LinkHealth.Alive, 7, true)]
    [InlineData(LinkHealth.Dying, 2, false)]
    [InlineData(LinkHealth.Dying, 3, true)]
    [InlineData(LinkHealth.Dead, 3, true)]
    [InlineData(LinkHealth.Unknown, 2, false)]
    [InlineData(LinkHealth.Unknown, 3, true)]
    public void Live_links_are_rechecked_weekly_and_the_rest_every_three_days(LinkHealth health, int days, bool due)
    {
        var state = new LinkState(health, 0, T0, null);
        Assert.Equal(due, LinkStates.IsDue(state, T0.AddDays(days)));
    }

    [Fact]
    public void A_link_never_seen_is_due()
    {
        Assert.True(LinkStates.IsDue(null, T0));
    }

    [Fact]
    public void State_round_trips_through_its_encoding()
    {
        var state = new LinkState(LinkHealth.Dying, 2, T0, "https://a.example/new");
        Assert.Equal(state, LinkState.Decode(state.Encode()));
        Assert.Null(LinkState.Decode(null));
    }
}
