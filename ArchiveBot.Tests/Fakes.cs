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

    private readonly HashSet<string> _excluded = [];

    private readonly HashSet<string> _restrictedHosts = [];

    public List<(string Url, string? From, string? To, int Limit, string Filter)> Calls { get; } = [];

    public void Captured(string url, params string[] timestamps) => _captures[url] = timestamps.Order().ToList();

    // Answers as the CDX server does for a site that asked to be left out.
    public void Excluded(string url) => _excluded.Add(url);

    // Answers as the CDX server does for a site closed to public searches. Its captures still play back.
    public void Restricted(string host) => _restrictedHosts.Add(host);

    public async Task<List<List<string>>?> GetCaptures(string url, string? fromTimestamp, string? toTimestamp, int limit, string filter)
    {
        Calls.Add((url, fromTimestamp, toTimestamp, limit, filter));
        if (_excluded.Contains(url))
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "https://web.archive.org/cdx/search/cdx");
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden) { RequestMessage = request, Content = new StringContent("") };
            response.Headers.Add("x-archive-wayback-runtime-error", "AdministrativeAccessControlException: Blocked Site Error");
            throw await ApiException.Create(request, HttpMethod.Get, response, new RefitSettings());
        }

        if (_restrictedHosts.Contains(new Uri(url).Host))
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "https://web.archive.org/cdx/search/cdx");
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                RequestMessage = request,
                Content = new StringContent("<title>403 Forbidden</title>\n<h1>Forbidden</h1>\n<p>This type of CDX query requires authorization.</p>")
            };
            throw await ApiException.Create(request, HttpMethod.Get, response, new RefitSettings());
        }

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

        return rows;
    }
}

// Wayback playback without following redirects: the nearest capture to any timestamp is the one set, or none.
public sealed class FakePlayback : HttpMessageHandler
{
    private readonly Dictionary<string, (string Timestamp, bool Redirect)> _nearest = new();

    public List<Uri> Requests { get; } = [];

    public void Captured(string url, string timestamp, bool redirect = false) => _nearest[url] = (timestamp, redirect);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        var path = request.RequestUri!.PathAndQuery;
        var timestamp = path[5..19];
        var url = path[23..];
        if (!_nearest.TryGetValue(url, out var nearest))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        var response = new HttpResponseMessage(HttpStatusCode.Found);
        if (timestamp != nearest.Timestamp)
        {
            response.Headers.Location = new Uri($"https://web.archive.org/web/{nearest.Timestamp}id_/{url}");
        }
        else if (nearest.Redirect)
        {
            response.Headers.Location = new Uri($"https://web.archive.org/web/{nearest.Timestamp}id_/https://elsewhere.example/");
        }
        else
        {
            response.StatusCode = HttpStatusCode.OK;
        }

        return Task.FromResult(response);
    }

    public WaybackPlayback Client() => new(new HttpClient(this) { BaseAddress = new Uri("https://web.archive.org") });
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
    public FakePlayback Playback { get; } = new();
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
        Job = new ArchiveLinkJob(null!, new PacedWaybackClient(Wayback, Playback.Client(), NullLogger<PacedWaybackClient>.Instance, TimeSpan.Zero, TimeSpan.Zero),
            new PacedArchiveClient(Archive, NullLogger<PacedArchiveClient>.Instance, TimeSpan.Zero),
            checker, null!, PageChecks, Time, NullLogger<ArchiveLinkJob>.Instance,
            Microsoft.Extensions.Options.Options.Create(new ArchiveBotConfig { BotUsername = "ArchiveBot", BotPassword = "unused" }));
    }

    public async Task<string> Rewrite(string wikitext) => (await Job.RewriteReferences(wikitext, Config)).Content;

    public async Task<string> Summary(string wikitext) => (await Job.RewriteReferences(wikitext, Config)).Changes.Summary();
}
