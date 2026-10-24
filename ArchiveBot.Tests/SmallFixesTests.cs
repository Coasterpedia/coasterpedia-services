using System.Net;
using CoasterpediaServices.ArchiveBot.Links;
using CoasterpediaServices.ArchiveBot.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CoasterpediaServices.ArchiveBot.Tests;

public class SiteRuleTests
{
    private static readonly BotConfig Config = new([], new Dictionary<string, string>
    {
        ["facebook.com"] = "IgnoreRedirect",
        ["m.facebook.com"] = "Ignore",
        ["Newspapers.com"] = "Ignore"
    }, null);

    [Theory]
    [InlineData("facebook.com", "IgnoreRedirect")]
    [InlineData("www.facebook.com", "IgnoreRedirect")]
    [InlineData("business.facebook.com", "IgnoreRedirect")]
    [InlineData("m.facebook.com", "Ignore")]
    [InlineData("www.newspapers.com", "Ignore")]
    [InlineData("WWW.NEWSPAPERS.COM", "Ignore")]
    [InlineData("notfacebook.com", null)]
    [InlineData("facebook.com.example", null)]
    [InlineData("example.com", null)]
    public void A_rule_covers_its_subdomains_and_the_most_specific_wins(string host, string? rule)
    {
        Assert.Equal(rule, Config.SiteRule(host));
    }

    [Fact]
    public void No_site_config_has_no_rules()
    {
        Assert.Null(new BotConfig([], null, null).SiteRule("facebook.com"));
    }

    [Fact]
    public async Task A_rule_on_the_bare_domain_applies_to_www_in_the_job()
    {
        var h = new JobHarness();
        const string page = "A.<ref>{{cite web|url=https://www.paywall.example/a|title=A}}</ref>";

        Assert.Equal(page, await h.Rewrite(page));
        Assert.Empty(h.Sites.Requests);
    }
}

public class BotConfigProviderTests
{
    private const string Good = """{"citationTemplates": ["cite web"], "siteConfig": {"a.example": "Ignore"}}""";
    private const string Edited = """{"citationTemplates": ["cite web", "cite news"], "siteConfig": {}}""";

    private readonly FixedTime _time = new(DateTimeOffset.Parse("2026-10-24T12:00:00Z"));
    private readonly BotConfigProvider _provider;
    private int _fetches;

    public BotConfigProviderTests()
    {
        _provider = new BotConfigProvider(_time, NullLogger<BotConfigProvider>.Instance);
    }

    private Func<Task<string?>> Page(string? content) => () =>
    {
        _fetches++;
        return Task.FromResult(content);
    };

    [Fact]
    public async Task The_page_is_read_once_until_it_is_old()
    {
        await _provider.Get(Page(Good));
        _time.Advance(BotConfigProvider.MaxAge - TimeSpan.FromSeconds(1));
        await _provider.Get(Page(Edited));
        Assert.Equal(1, _fetches);
    }

    [Fact]
    public async Task An_edit_to_the_page_is_picked_up_once_the_copy_is_old()
    {
        await _provider.Get(Page(Good));
        _time.Advance(BotConfigProvider.MaxAge);

        var config = await _provider.Get(Page(Edited));
        Assert.Equal(["cite web", "cite news"], config.CitationTemplates);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{"siteConfig": {}}""")]
    [InlineData(null)]
    public async Task A_broken_page_keeps_the_last_good_copy(string? broken)
    {
        await _provider.Get(Page(Good));
        _time.Advance(BotConfigProvider.MaxAge);

        var config = await _provider.Get(Page(broken));
        Assert.Equal(["cite web"], config.CitationTemplates);

        // And isn't asked for again on every citation.
        await _provider.Get(Page(broken));
        Assert.Equal(2, _fetches);
    }

    [Fact]
    public async Task A_broken_page_with_no_good_copy_fails()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => _provider.Get(Page("{ not json")));
    }
}

public class EditSummaryTests
{
    [Theory]
    [InlineData(1, 0, 0, 0, "Added an archive link to 1 citation")]
    [InlineData(3, 0, 0, 0, "Added archive links to 3 citations")]
    [InlineData(3, 1, 0, 0, "Added archive links to 3 citations and marked 1 link as dead")]
    [InlineData(0, 2, 1, 0, "Marked 2 links as dead and marked 1 link as live again")]
    [InlineData(2, 1, 1, 4, "Added archive links to 2 citations, marked 1 link as dead, marked 1 link as live again and updated 4 other citations")]
    [InlineData(0, 0, 0, 1, "Updated 1 citation")]
    [InlineData(0, 0, 0, 0, "Updated citations")]
    public void The_summary_says_what_changed(int archived, int dead, int live, int updated, string summary)
    {
        var changes = new CitationChanges { Archived = archived, MarkedDead = dead, MarkedLive = live, Updated = updated };
        Assert.Equal(summary, changes.Summary());
    }

    [Fact]
    public async Task A_run_counts_what_it_did_to_each_citation()
    {
        var h = new JobHarness();
        h.Sites.Answer("https://live.example/a", HttpStatusCode.OK);
        h.Sites.Answer("https://live.example/b", HttpStatusCode.OK);
        h.Sites.Answer("https://back.example/x", HttpStatusCode.OK);
        h.Wayback.Captured("https://live.example/a", "20200101000000");
        h.Wayback.Captured("https://back.example/x", "20200101000000");
        await h.Links.Save("https://back.example/x", new LinkState(LinkHealth.Dead, 3, h.Time.Now.AddDays(-3), null));

        var summary = await h.Summary(string.Join("\n",
            "A.<ref>{{cite web|url=https://live.example/a|title=A}}</ref>",
            "B.<ref>{{cite web|url=https://live.example/b|title=B}}</ref>",
            "C.<ref>{{cite web|url=https://closed.example/c|title=C}}</ref>",
            "D.<ref>{{cite web|url=https://back.example/x|title=D|archive-url=https://web.archive.org/web/20200101000000/https://back.example/x|url-status=dead}}</ref>",
            "E.<ref>{{cite web|url=https://live.example/b|title=B}}</ref>"));

        // A and B archived (B saved), C closed by hand, D answering again. E is B's text, rewritten with it.
        Assert.Equal("Added archive links to 2 citations, marked 1 link as dead and marked 1 link as live again", summary);
    }
}
