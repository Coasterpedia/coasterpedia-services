using System.Net;
using CoasterpediaServices.ArchiveBot.Clients.Archive;
using CoasterpediaServices.ArchiveBot.Clients.Wayback;
using CoasterpediaServices.ArchiveBot.Links;
using CoasterpediaServices.ArchiveBot.Options;
using CoasterpediaServices.ArchiveBot.Sweep;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Refit;
using LinkWebClient = CoasterpediaServices.ArchiveBot.Clients.WebClient.WebClient;

namespace CoasterpediaServices.ArchiveBot.Tests;

public sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

// Answers each cited site as set: a status code and where the redirects ended, or no answer at all.
public sealed class FakeSites : HttpMessageHandler
{
    private readonly Dictionary<Uri, (HttpStatusCode? Status, string? Final)> _answers = new();

    public List<Uri> Requests { get; } = [];

    public void Answer(string url, HttpStatusCode status, string? final = null) => _answers[new Uri(url)] = (status, final);

    public void Unreachable(string url) => _answers[new Uri(url)] = (null, null);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        var (status, final) = _answers[request.RequestUri!];
        if (status == null)
        {
            throw new HttpRequestException("Name or service not known");
        }

        return Task.FromResult(new HttpResponseMessage(status.Value)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, final == null ? request.RequestUri : new Uri(final))
        });
    }
}

// The CDX search, as far as ArchiveBot uses it: captures in a from/to window, first or last.
public sealed class FakeWayback : IWaybackClient
{
    private readonly Dictionary<string, List<string>> _captures = new();

    public List<(string Url, string? From, string? To, int Limit, string Filter)> Calls { get; } = [];

    public void Captured(string url, params string[] timestamps) => _captures[url] = timestamps.Order().ToList();

    public Task<List<List<string>>?> GetCaptures(string url, string? fromTimestamp, string? toTimestamp, int limit, string filter)
    {
        Calls.Add((url, fromTimestamp, toTimestamp, limit, filter));
        var from = fromTimestamp?.PadRight(14, '0');
        var to = toTimestamp?.PadRight(14, '9');
        var inWindow = _captures.GetValueOrDefault(url, [])
            .Where(t => (from == null || string.CompareOrdinal(t, from) >= 0) && (to == null || string.CompareOrdinal(t, to) <= 0))
            .ToList();
        var picked = limit < 0 ? inWindow.TakeLast(-limit) : inWindow.Take(limit);

        var rows = new List<List<string>>();
        if (inWindow.Count > 0)
        {
            rows.Add(["timestamp"]);
            rows.AddRange(picked.Select(t => new List<string> { t }));
        }

        return Task.FromResult<List<List<string>>?>(rows);
    }
}

public sealed class FakeArchive : IArchiveClient
{
    public const string SavedAt = "20261024120000";

    public List<string> Saved { get; } = [];

    public Task<IApiResponse> SavePage(string url)
    {
        Saved.Add(url);
        var response = new HttpResponseMessage(HttpStatusCode.Found)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://web.archive.org/save/" + url)
        };
        response.Headers.Location = new Uri($"https://web.archive.org/web/{SavedAt}/{url}");
        return Task.FromResult<IApiResponse>(new ApiResponse<object>(response, null, new RefitSettings()));
    }
}

// An ArchiveLinkJob wired to fakes, for RewriteReferences. Run never runs here: it needs the wiki.
public sealed class JobHarness
{
    public FakeSites Sites { get; } = new();
    public FakeWayback Wayback { get; } = new();
    public FakeArchive Archive { get; } = new();
    public FixedTime Time { get; } = new(DateTimeOffset.Parse("2026-10-24T12:00:00Z"));
    public InMemoryLinkStateStore Links { get; } = new();
    public InMemoryPageCheckStore PageChecks { get; } = new();

    public BotConfig Config { get; } = new(
        ["cite web", "cite news"],
        new Dictionary<string, string>
        {
            ["www.facebook.com"] = "IgnoreRedirect",
            ["closed.example"] = "Dead",
            ["paywall.example"] = "Ignore"
        },
        null);

    public ArchiveLinkJob Job { get; }

    public JobHarness()
    {
        var checker = new LinkChecker(new LinkWebClient(new HttpClient(Sites)), Links, Time, NullLogger<LinkChecker>.Instance);
        Job = new ArchiveLinkJob(null!, Wayback, new PacedArchiveClient(Archive, NullLogger<PacedArchiveClient>.Instance, TimeSpan.Zero),
            checker, null!, PageChecks, Time, NullLogger<ArchiveLinkJob>.Instance,
            Microsoft.Extensions.Options.Options.Create(new ArchiveBotConfig { BotUsername = "ArchiveBot", BotPassword = "unused" }));
    }

    public Task<string> Rewrite(string wikitext) => Job.RewriteReferences(wikitext, Config);
}
