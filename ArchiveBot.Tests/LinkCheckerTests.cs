using System.Net;
using CoasterpediaServices.ArchiveBot.Links;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using LinkWebClient = CoasterpediaServices.ArchiveBot.Clients.WebClient.WebClient;

namespace CoasterpediaServices.ArchiveBot.Tests;

public class LinkCheckerTests
{
    private readonly FakeSites _sites = new();
    private readonly FixedTime _time = new(DateTimeOffset.Parse("2026-10-24T12:00:00Z"));
    private readonly InMemoryLinkStateStore _store = new();
    private readonly LinkChecker _checker;

    public LinkCheckerTests()
    {
        _checker = new LinkChecker(new LinkWebClient(new HttpClient(_sites)), _store, _time, NullLogger<LinkChecker>.Instance);
    }

    [Fact]
    public async Task A_link_is_checked_once_until_it_is_due_again()
    {
        _sites.Answer("https://park.example/", HttpStatusCode.OK);

        await _checker.Check(new Uri("https://park.example/"));
        _time.Advance(TimeSpan.FromDays(6));
        await _checker.Check(new Uri("https://park.example/"));
        Assert.Single(_sites.Requests);

        _time.Advance(TimeSpan.FromDays(1));
        await _checker.Check(new Uri("https://park.example/"));
        Assert.Equal(2, _sites.Requests.Count);
    }

    [Fact]
    public async Task Fragments_are_one_link()
    {
        _sites.Answer("https://park.example/rides", HttpStatusCode.OK);

        await _checker.Check(new Uri("https://park.example/rides#coasters"));
        await _checker.Check(new Uri("https://park.example/rides#flats"));
        Assert.Single(_sites.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task A_refusal_says_nothing_about_the_page(HttpStatusCode status)
    {
        _sites.Answer("https://blocked.example/a", status);
        Assert.Equal(LinkHealth.Unknown, (await _checker.Check(new Uri("https://blocked.example/a"))).Health);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Gone)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task An_error_counts_as_a_failure(HttpStatusCode status)
    {
        _sites.Answer("https://broken.example/a", status);
        var state = await _checker.Check(new Uri("https://broken.example/a"));
        Assert.Equal(LinkHealth.Dying, state.Health);
        Assert.Equal(1, state.Failures);
    }

    [Fact]
    public async Task A_server_that_does_not_answer_counts_as_a_failure()
    {
        _sites.Unreachable("https://gone.example/a");
        Assert.Equal(LinkHealth.Dying, (await _checker.Check(new Uri("https://gone.example/a"))).Health);
    }

    [Fact]
    public async Task A_redirect_is_remembered_between_checks()
    {
        _sites.Answer("https://park.example/old", HttpStatusCode.OK, "https://park.example/new");

        await _checker.Check(new Uri("https://park.example/old"));
        var cached = await _checker.Check(new Uri("https://park.example/old"));
        Assert.Single(_sites.Requests);
        Assert.Equal("https://park.example/new", cached.RedirectTarget);
    }

    [Fact]
    public async Task A_trailing_slash_is_not_a_redirect()
    {
        _sites.Answer("https://park.example", HttpStatusCode.OK, "https://park.example/");
        Assert.Null((await _checker.Check(new Uri("https://park.example"))).RedirectTarget);
    }

    [Fact]
    public async Task A_link_dies_after_three_failed_checks_three_days_apart()
    {
        _sites.Answer("https://broken.example/a", HttpStatusCode.NotFound);
        var uri = new Uri("https://broken.example/a");

        Assert.Equal(LinkHealth.Dying, (await _checker.Check(uri)).Health);
        _time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(LinkHealth.Dying, (await _checker.Check(uri)).Health);
        _time.Advance(TimeSpan.FromDays(2));
        Assert.Equal(LinkHealth.Dying, (await _checker.Check(uri)).Health);
        _time.Advance(TimeSpan.FromDays(3));
        Assert.Equal(LinkHealth.Dead, (await _checker.Check(uri)).Health);
        Assert.Equal(3, _sites.Requests.Count);
    }
}
