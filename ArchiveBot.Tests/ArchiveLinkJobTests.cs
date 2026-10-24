using System.Net;
using CoasterpediaServices.ArchiveBot.Links;
using Xunit;

namespace CoasterpediaServices.ArchiveBot.Tests;

public class ArchiveLinkJobTests
{
    private static readonly TimeSpan Recheck = LinkStates.RecheckInterval;

    private readonly JobHarness _h = new();

    // Runs the job again after the recheck interval, as later edits would.
    private async Task<string> RewriteOnDays(string wikitext, int checks)
    {
        for (var i = 0; i < checks; i++)
        {
            if (i > 0)
            {
                _h.Time.Advance(Recheck);
            }

            wikitext = await _h.Rewrite(wikitext);
        }

        return wikitext;
    }

    // The reference itself

    [Fact]
    public async Task Text_around_the_citation_is_kept()
    {
        _h.Sites.Answer("https://live.example/a", HttpStatusCode.OK);
        _h.Wayback.Captured("https://live.example/a", "20150320101010");

        var output = await _h.Rewrite("A.<ref>See {{cite web|url=https://live.example/a|title=A}}, p. 4.</ref>");

        Assert.StartsWith("A.<ref>See {{cite web|url=https://live.example/a|title=A|archive-url=", output);
        Assert.EndsWith("}}, p. 4.</ref>", output);
    }

    [Fact]
    public async Task Other_templates_in_the_reference_are_kept()
    {
        _h.Sites.Answer("https://live.example/a", HttpStatusCode.OK);
        _h.Wayback.Captured("https://live.example/a", "20150320101010");

        var output = await _h.Rewrite("A.<ref>{{cite web|url=https://live.example/a|title=A}} and {{cite book|title=Other}}</ref>");

        Assert.EndsWith("}} and {{cite book|title=Other}}</ref>", output);
    }

    [Fact]
    public async Task A_back_reference_is_untouched()
    {
        Assert.Equal("A.<ref name=\"a\" /> end", await _h.Rewrite("A.<ref name=\"a\" /> end"));
    }

    [Fact]
    public async Task An_ignored_site_is_untouched()
    {
        const string page = "A.<ref>{{cite web|url=https://paywall.example/a|title=A}}</ref>";
        Assert.Equal(page, await _h.Rewrite(page));
        Assert.Empty(_h.Sites.Requests);
    }

    [Fact]
    public async Task A_second_pass_changes_nothing()
    {
        _h.Sites.Answer("https://live.example/a", HttpStatusCode.OK);
        _h.Sites.Answer("https://live.example/c", HttpStatusCode.OK);
        _h.Wayback.Captured("https://live.example/a", "20150320101010");

        var once = await _h.Rewrite(string.Join("\n",
            "A.<ref>See {{cite web|url=https://live.example/a|title=A|access-date=26 March 2015}}, p. 4.</ref>",
            "B.<ref>{{cite web|url=https://live.example/c|title=C}}</ref>"));

        Assert.Equal(once, await _h.Rewrite(once));
    }

    // Choosing a snapshot

    [Fact]
    public async Task The_snapshot_is_the_last_one_on_or_before_the_access_date()
    {
        _h.Sites.Answer("https://live.example/a", HttpStatusCode.OK);
        _h.Wayback.Captured("https://live.example/a", "20140101000000", "20150326180000", "20190101000000");

        var output = await _h.Rewrite("A.<ref>{{cite web|url=https://live.example/a|title=A|access-date=26 March 2015}}</ref>");

        Assert.Contains("archive-url=https://web.archive.org/web/20150326180000/https://live.example/a|archive-date=2015-03-26", output);
    }

    [Fact]
    public async Task Without_an_earlier_snapshot_the_first_one_after_the_access_date_is_used()
    {
        _h.Sites.Answer("https://live.example/a", HttpStatusCode.OK);
        _h.Wayback.Captured("https://live.example/a", "20160501000000", "20190101000000");

        var output = await _h.Rewrite("A.<ref>{{cite web|url=https://live.example/a|title=A|access-date=2015-03-26}}</ref>");

        Assert.Contains("/web/20160501000000/", output);
    }

    [Fact]
    public async Task With_only_a_publication_date_the_first_snapshot_after_it_is_used()
    {
        _h.Sites.Answer("https://live.example/a", HttpStatusCode.OK);
        _h.Wayback.Captured("https://live.example/a", "20120101000000", "20180605000000", "20230101000000");

        var output = await _h.Rewrite("A.<ref>{{cite web|url=https://live.example/a|title=A|date=1 June 2018}}</ref>");

        Assert.Contains("/web/20180605000000/", output);
    }

    [Fact]
    public async Task Without_dates_the_latest_snapshot_is_used()
    {
        _h.Sites.Answer("https://live.example/a", HttpStatusCode.OK);
        _h.Wayback.Captured("https://live.example/a", "20120101000000", "20230101000000");

        var output = await _h.Rewrite("A.<ref>{{cite web|url=https://live.example/a|title=A}}</ref>");

        Assert.Contains("/web/20230101000000/", output);
    }

    [Fact]
    public async Task Only_successful_captures_are_asked_for()
    {
        _h.Sites.Answer("https://live.example/a", HttpStatusCode.OK);
        await _h.Rewrite("A.<ref>{{cite web|url=https://live.example/a|title=A}}</ref>");

        Assert.All(_h.Wayback.Calls, c => Assert.Equal("statuscode:20[036]", c.Filter));
    }

    // Saving to the Wayback Machine

    [Fact]
    public async Task A_live_link_without_a_snapshot_is_saved()
    {
        _h.Sites.Answer("https://live.example/c", HttpStatusCode.OK);

        var output = await _h.Rewrite("A.<ref>{{cite web|url=https://live.example/c|title=C}}</ref>");

        Assert.Equal(["https://live.example/c"], _h.Archive.Saved);
        Assert.Contains($"archive-url=https://web.archive.org/web/{FakeArchive.SavedAt}/https://live.example/c|archive-date=2026-10-24", output);
    }

    [Fact]
    public async Task A_link_blocking_the_bot_is_still_saved()
    {
        _h.Sites.Answer("https://blocked.example/a", HttpStatusCode.Forbidden);
        await _h.Rewrite("A.<ref>{{cite web|url=https://blocked.example/a|title=A}}</ref>");

        Assert.Equal(["https://blocked.example/a"], _h.Archive.Saved);
    }

    [Fact]
    public async Task A_failing_link_is_not_saved()
    {
        _h.Sites.Answer("https://dead.example/gone", HttpStatusCode.NotFound);
        await RewriteOnDays("A.<ref>{{cite web|url=https://dead.example/gone|title=A}}</ref>", 3);

        Assert.Empty(_h.Archive.Saved);
    }

    [Fact]
    public async Task A_page_redirecting_to_the_home_page_is_not_saved_or_given_its_archive()
    {
        _h.Sites.Answer("https://soft.example/article", HttpStatusCode.OK, "https://soft.example/");
        _h.Wayback.Captured("https://soft.example/", "20240101000000");

        var output = await _h.Rewrite("A.<ref>{{cite web|url=https://soft.example/article|title=A}}</ref>");

        Assert.Empty(_h.Archive.Saved);
        Assert.DoesNotContain("archive-url", output);
        Assert.DoesNotContain(_h.Wayback.Calls, c => c.Url == "https://soft.example/");
    }

    [Fact]
    public async Task A_page_redirecting_to_a_landing_page_gets_its_own_archive_and_dies_on_the_third_check()
    {
        const string url = "https://www.cedarpoint.com/play/rides/top-thrill-dragster";
        _h.Sites.Answer(url, HttpStatusCode.OK, "https://www.sixflags.com/cedarpoint");
        _h.Wayback.Captured(url, "20190601000000", "20250101000000");
        _h.Wayback.Captured("https://www.sixflags.com/cedarpoint", "20260101000000");
        const string page = "A.<ref>{{cite web|url=" + url + "|title=Top Thrill Dragster|access-date=2019-07-01}}</ref>";

        var first = await _h.Rewrite(page);
        Assert.Contains("archive-url=https://web.archive.org/web/20190601000000/" + url, first);
        Assert.DoesNotContain("url-status", first);

        var third = await RewriteOnDays(first, 3);
        Assert.Contains("url-status=dead", third);
        Assert.DoesNotContain("Dead link", third);
        Assert.Empty(_h.Archive.Saved);
        Assert.DoesNotContain(_h.Wayback.Calls, c => c.Url.Contains("sixflags.com"));
    }

    [Fact]
    public async Task A_moved_page_uses_its_new_address()
    {
        _h.Sites.Answer("https://moved.example/old", HttpStatusCode.OK, "https://moved.example/new");

        await _h.Rewrite("A.<ref>{{cite web|url=https://moved.example/old|title=A}}</ref>");

        Assert.Contains(_h.Wayback.Calls, c => c.Url == "https://moved.example/new");
        Assert.Equal(["https://moved.example/new"], _h.Archive.Saved);
    }

    [Fact]
    public async Task An_ignored_redirect_saves_the_cited_address()
    {
        _h.Sites.Answer("https://www.facebook.com/park/posts/1", HttpStatusCode.OK, "https://www.facebook.com/login/?next=x");

        await _h.Rewrite("A.<ref>{{cite web|url=https://www.facebook.com/park/posts/1|title=A}}</ref>");

        Assert.Equal(["https://www.facebook.com/park/posts/1"], _h.Archive.Saved);
    }

    // Live and dead, over time

    [Fact]
    public async Task A_404_is_marked_dead_only_on_its_third_check()
    {
        _h.Sites.Answer("https://dead.example/gone", HttpStatusCode.NotFound);
        const string page = "A.<ref>{{cite web|url=https://dead.example/gone|title=A}}</ref>";

        var afterTwo = await RewriteOnDays(page, 2);
        Assert.DoesNotContain("url-status=dead", afterTwo);
        Assert.DoesNotContain("Dead link", afterTwo);

        _h.Time.Advance(Recheck);
        var afterThree = await _h.Rewrite(afterTwo);
        Assert.Contains("url-status=dead}}{{Dead link}}</ref>", afterThree);
    }

    [Fact]
    public async Task Editing_again_within_three_days_does_not_count_as_another_check()
    {
        _h.Sites.Answer("https://dead.example/gone", HttpStatusCode.NotFound);
        var page = "A.<ref>{{cite web|url=https://dead.example/gone|title=A}}</ref>";

        for (var i = 0; i < 5; i++)
        {
            page = await _h.Rewrite(page);
            _h.Time.Advance(TimeSpan.FromHours(12));
        }

        Assert.Single(_h.Sites.Requests);
        Assert.DoesNotContain("Dead link", page);
    }

    [Fact]
    public async Task A_link_that_fails_now_and_then_never_goes_dead()
    {
        const string url = "https://flaky.example/news";
        var page = $"A.<ref>{{{{cite web|url={url}|title=A}}}}</ref>";
        var answers = new[] { HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK };
        _h.Wayback.Captured(url, "20250101000000");

        foreach (var answer in answers)
        {
            _h.Sites.Answer(url, answer);
            page = await _h.Rewrite(page);
            Assert.DoesNotContain("url-status=dead", page);
            _h.Time.Advance(TimeSpan.FromDays(7));
        }
    }

    [Fact]
    public async Task One_success_brings_a_dead_link_back()
    {
        _h.Sites.Answer("https://back.example/x", HttpStatusCode.OK);
        await _h.Links.Save("https://back.example/x", new LinkState(LinkHealth.Dead, 3, _h.Time.Now.AddDays(-3), null));

        var output = await _h.Rewrite("A.<ref>Quote. {{cite web|url=https://back.example/x|title=A|url-status=dead}}{{dead link|date=May 2024}} More.</ref>");

        Assert.StartsWith("A.<ref>Quote. {{cite web", output);
        Assert.EndsWith("}} More.</ref>", output);
        Assert.Contains("url-status=live", output);
        Assert.DoesNotContain("ead link", output);
    }

    [Fact]
    public async Task A_link_already_marked_dead_stays_marked_until_the_checks_decide()
    {
        // What the one-check version left behind. One more failure must not undo it, and nor may an unknown.
        _h.Sites.Answer("https://dead.example/gone", HttpStatusCode.NotFound);
        const string page = "A.<ref>{{cite web|url=https://dead.example/gone|title=A|url-status=dead}}{{Dead link}}</ref>";

        Assert.Equal(page, await _h.Rewrite(page));
    }

    [Fact]
    public async Task A_site_marked_dead_by_hand_is_never_fetched()
    {
        var output = await _h.Rewrite("A.<ref>{{cite web|url=https://closed.example/a|title=A}}</ref>");

        Assert.Empty(_h.Sites.Requests);
        Assert.Empty(_h.Archive.Saved);
        Assert.Contains("url-status=dead}}{{Dead link}}", output);
    }

    [Fact]
    public async Task A_link_cited_on_many_pages_is_fetched_once()
    {
        _h.Sites.Answer("https://park.example/", HttpStatusCode.OK);
        _h.Wayback.Captured("https://park.example/", "20250101000000");

        await _h.Rewrite("A.<ref>{{cite web|url=https://park.example/|title=Park}}</ref>");
        await _h.Rewrite("B.<ref>{{cite news|url=https://park.example/|title=Park home}}</ref>");

        Assert.Single(_h.Sites.Requests);
    }
}
