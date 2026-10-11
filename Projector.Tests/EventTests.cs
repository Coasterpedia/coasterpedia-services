using System.Text.Json.Nodes;
using CoasterpediaServices.Common;
using CoasterpediaServices.Projector.Events;
using CoasterpediaServices.Projector.Options;
using Xunit;

namespace CoasterpediaServices.Projector.Tests;

public class EventTests
{
    // The filter never touches state.
    private static readonly EventFilter Filter =
        new ProjectorSubscriber(null!, Microsoft.Extensions.Options.Options.Create(new ProjectorConfig())).Filter;

    // Shaped as EventBus REL1_46's createPageMoveEvent builds it.
    private static EventBusEvent Move(int from, int to) => EventBusEvent.FromJson(JsonNode.Parse($$"""
        {
          "$schema": "/mediawiki/page/move/1.0.0",
          "page_id": 171,
          "page_title": "Draft:Gold_Rusher",
          "page_namespace": {{to}},
          "rev_id": 9002,
          "performer": { "user_text": "Patroller", "user_groups": ["*", "user", "autopatrolled"] },
          "prior_state": { "page_title": "Gold_Rusher", "page_namespace": {{from}}, "rev_id": 9001 }
        }
        """))!;

    [Fact]
    public void A_move_out_of_the_main_namespace_matches()
    {
        var evt = Move(from: 0, to: 118);
        Assert.Equal("0", evt.PriorNamespace);
        Assert.True(Filter.Matches(evt));
    }

    [Fact]
    public void A_move_into_the_main_namespace_matches() => Assert.True(Filter.Matches(Move(from: 118, to: 0)));

    [Fact]
    public void A_move_elsewhere_does_not_match() => Assert.False(Filter.Matches(Move(from: 2, to: 118)));

    [Fact]
    public void An_edit_has_no_prior_namespace()
    {
        var evt = EventBusEvent.FromJson(JsonNode.Parse("""
            { "$schema": "/mediawiki/revision/create/2.0.0", "page_id": 171, "page_title": "Draft:Gold_Rusher", "page_namespace": 118 }
            """))!;
        Assert.Null(evt.PriorNamespace);
        Assert.False(Filter.Matches(evt));
    }
}
