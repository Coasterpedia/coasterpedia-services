using System.Net;
using System.Text;
using CoasterpediaServices.ArchiveBot.Clients.Archive;
using CoasterpediaServices.ArchiveBot.Options;
using Xunit;

namespace CoasterpediaServices.ArchiveBot.Tests;

public class SavePageNowTests
{
    private const string Url = "https://wikimapia.org/25002991/photo/2928345";

    // Answers each request with the next of the bodies given, as SPN2 would.
    private sealed class Script(params (HttpStatusCode Status, string Body)[] answers) : HttpMessageHandler
    {
        private int _next;

        public List<(HttpMethod Method, string Path, string? Auth, string? Form)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString(),
                request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            var (status, body) = answers[Math.Min(_next++, answers.Length - 1)];
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static readonly ArchiveBotConfig Keys = new()
    {
        BotUsername = "ArchiveBot", BotPassword = "unused", ArchiveAccessKey = "access", ArchiveSecretKey = "secret"
    };

    private static SavePageNow Client(Script script, ArchiveBotConfig? config = null, TimeSpan? timeout = null) =>
        new(new HttpClient(script) { BaseAddress = new Uri("https://web.archive.org") }, config ?? Keys, TimeProvider.System,
            TimeSpan.Zero, timeout ?? TimeSpan.FromMinutes(1));

    [Fact]
    public async Task A_capture_is_started_then_asked_after_until_it_has_a_snapshot()
    {
        var script = new Script(
            (HttpStatusCode.OK, """{"url":"https://wikimapia.org/25002991/photo/2928345","job_id":"spn2-abc"}"""),
            (HttpStatusCode.OK, """{"status":"pending","job_id":"spn2-abc"}"""),
            (HttpStatusCode.OK, """{"status":"success","job_id":"spn2-abc","timestamp":"20261103101500","original_url":"https://wikimapia.org/25002991/photo/2928345/"}"""));

        var capture = await Client(script).Save(Url);

        Assert.Equal(new Uri($"https://web.archive.org/web/20261103101500/{Url}"), capture.Snapshot);
        Assert.Equal(CaptureFault.None, capture.Fault);
        Assert.Equal((HttpMethod.Post, "/save", "LOW access:secret"), (script.Requests[0].Method, script.Requests[0].Path, script.Requests[0].Auth));
        Assert.Contains("skip_first_archive=1", script.Requests[0].Form);
        Assert.Contains("if_not_archived_within=1d", script.Requests[0].Form);
        Assert.Equal("/save/status/spn2-abc", script.Requests[2].Path);
    }

    [Fact]
    public async Task A_recent_capture_given_straight_away_is_used()
    {
        var script = new Script((HttpStatusCode.OK,
            """{"status":"success","url":"https://www.lago.be/","timestamp":"20261103091200","message":"The same snapshot had been made 3 hours ago."}"""));

        var capture = await Client(script).Save("https://www.lago.be/");

        Assert.Equal(new Uri("https://web.archive.org/web/20261103091200/https://www.lago.be/"), capture.Snapshot);
        Assert.Single(script.Requests);
    }

    [Fact]
    public async Task A_failed_capture_says_why()
    {
        var script = new Script(
            (HttpStatusCode.OK, """{"job_id":"spn2-abc"}"""),
            (HttpStatusCode.OK, """{"status":"error","status_ext":"error:blocked-url","message":"This URL is in the Save Page Now service block list and cannot be captured."}"""));

        var capture = await Client(script).Save(Url);

        Assert.Null(capture.Snapshot);
        Assert.Equal("error:blocked-url: This URL is in the Save Page Now service block list and cannot be captured.", capture.Reason);
        Assert.Equal(CaptureFault.Url, capture.Fault);
        Assert.True(capture.Permanent);
    }

    [Fact]
    public async Task A_capture_refused_at_the_start_waits_without_spending_a_retry()
    {
        var script = new Script((HttpStatusCode.OK,
            """{"status":"error","status_ext":"error:user-session-limit","message":"You have already reached the limit of active Save Page Now sessions. Please wait for a minute and then try again."}"""));

        var capture = await Client(script).Save(Url);

        Assert.Equal(CaptureFault.Busy, capture.Fault);
        Assert.Equal(ArchiveBotConfig.SessionLimitPause, capture.RetryAfter);
        Assert.Single(script.Requests);
    }

    [Fact]
    public async Task Save_Page_Now_answering_429_is_a_rate_limit()
    {
        var capture = await Client(new Script((HttpStatusCode.TooManyRequests, "<html>Too Many Requests</html>"))).Save(Url);

        Assert.Equal(CaptureFault.RateLimited, capture.Fault);
    }

    [Fact]
    public async Task The_site_answering_429_is_the_sites_fault()
    {
        var script = new Script(
            (HttpStatusCode.OK, """{"job_id":"spn2-abc"}"""),
            (HttpStatusCode.OK, """{"status":"error","status_ext":"error:too-many-requests","message":"The target server is limiting requests."}"""));

        Assert.Equal(CaptureFault.Site, (await Client(script).Save(Url)).Fault);
    }

    [Fact]
    public async Task An_error_page_instead_of_an_answer_is_unexplained()
    {
        var capture = await Client(new Script((HttpStatusCode.InternalServerError, "<html>500 Internal Server Error</html>"))).Save(Url);

        Assert.Equal("http-500", capture.Error);
        Assert.Equal(CaptureFault.Unexplained, capture.Fault);
    }

    // What Save Page Now answered, Nov 2026.
    [Theory]
    [InlineData("error:internal-server-error", "The target server returned an internal server error https://funspotamericaatlanta.com/arieforceone-roller-coaster/. (HTTP status=500)", CaptureFault.Site, false)]
    [InlineData("error:no-request", "The target server blocks access to http://www.dollywood.com/themepark/rides/LightningRod. (HTTP status=403)", CaptureFault.Url, true)]
    [InlineData("error:host-crawling-paused", "Please try again in ~1 min. Crawling this host is paused because they notified us that they are overloaded right now.", CaptureFault.Url, false)]
    [InlineData("please try again in ~10 min. crawling this host is paused because they notified us that they are overloaded right now. (http status=429)", "Job failed.", CaptureFault.Url, false)]
    [InlineData("error:invalid-host-resolution", "Cannot resolve host content.sixflags.com.", CaptureFault.Site, false)]
    public async Task Failures_are_put_down_to_whoever_save_page_now_names(string error, string message, CaptureFault fault, bool permanent)
    {
        var script = new Script(
            (HttpStatusCode.OK, """{"job_id":"spn2-abc"}"""),
            (HttpStatusCode.OK, System.Text.Json.JsonSerializer.Serialize(new { status = "error", status_ext = error, message })));

        var capture = await Client(script).Save(Url);

        Assert.Equal((fault, permanent), (capture.Fault, capture.Permanent));
    }

    [Fact]
    public async Task Refused_keys_are_save_page_nows_fault()
    {
        var capture = await Client(new Script((HttpStatusCode.Unauthorized, "<html>401</html>"))).Save(Url);

        Assert.Equal(CaptureFault.SaveService, capture.Fault);
    }

    [Fact]
    public async Task A_site_that_wont_load_is_the_sites_fault()
    {
        var script = new Script(
            (HttpStatusCode.OK, """{"job_id":"spn2-abc"}"""),
            (HttpStatusCode.OK, """{"status":"error","status_ext":"error:browsing-timeout","message":"The capture timed out."}"""));

        Assert.Equal(CaptureFault.Site, (await Client(script).Save(Url)).Fault);
    }

    [Fact]
    public async Task A_capture_still_running_at_the_deadline_has_failed()
    {
        var script = new Script(
            (HttpStatusCode.OK, """{"job_id":"spn2-abc"}"""),
            (HttpStatusCode.OK, """{"status":"pending","job_id":"spn2-abc"}"""));

        var capture = await Client(script, timeout: TimeSpan.FromMilliseconds(50)).Save(Url);

        Assert.Equal("timeout", capture.Error);
        Assert.Equal(CaptureFault.Unexplained, capture.Fault);
    }

    [Fact]
    public async Task Without_keys_nothing_is_sent()
    {
        var script = new Script((HttpStatusCode.OK, "{}"));

        var capture = await Client(script, Keys with { ArchiveAccessKey = null }).Save(Url);

        Assert.Equal(CaptureFault.SaveService, capture.Fault);
        Assert.Empty(script.Requests);
    }
}
