using System.Net;
using System.Text;
using CoasterpediaServices.ArchiveBot.Clients.Archive;
using CoasterpediaServices.ArchiveBot.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Refit;
using Xunit;

namespace CoasterpediaServices.ArchiveBot.Tests;

public class ArchiveClientTests
{
    private sealed class Answer(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "text/html")
            });
    }

    private static Task<IApiResponse> Save(HttpStatusCode status, string body) =>
        RestService.For<IArchiveClient>(new HttpClient(new Answer(status, body)) { BaseAddress = new Uri("https://web.archive.org") })
            .SavePage("https://wikimapia.org/25002991/photo/2928345");

    [Fact]
    public async Task A_failed_save_gives_the_reason_on_the_page()
    {
        var response = await Save((HttpStatusCode)523, """
            <div class="col-md-4 col-md-offset-4">
                <h2>Sorry</h2>
                <p>Cannot resolve host nosuch.example. If the site is online, it may be blocking access from our service.</p>
            </div>
            """);

        Assert.Equal("status 523: Cannot resolve host nosuch.example. If the site is online, it may be blocking access from our service.",
            PacedArchiveClient.FailureReason(response));
    }

    private static (PacedArchiveClient Client, FakeArchive Archive, FixedTime Time) Paced()
    {
        var archive = new FakeArchive();
        var time = new FixedTime(DateTimeOffset.Parse("2026-10-27T20:00:00Z"));
        return (new PacedArchiveClient(archive, NullLogger<PacedArchiveClient>.Instance, TimeSpan.Zero, time), archive, time);
    }

    [Fact]
    public async Task Repeated_failed_captures_pause_saves_to_that_host()
    {
        var (client, archive, time) = Paced();
        archive.Failing("www.flickr.com");

        for (var i = 1; i < ArchiveBotConfig.HostFailuresBeforePause; i++)
        {
            await client.SavePage($"https://www.flickr.com/photos/a/{i}/");
            Assert.Null(client.PausedUntil("https://www.flickr.com/photos/b/1/"));
        }

        await client.SavePage("https://www.flickr.com/photos/a/last/");
        Assert.Equal(time.Now + ArchiveBotConfig.HostPause, client.PausedUntil("https://www.flickr.com/photos/b/1/"));
        Assert.Null(client.PausedUntil("https://wikimapia.org/1/photo/2"));

        time.Advance(ArchiveBotConfig.HostPause);
        Assert.Null(client.PausedUntil("https://www.flickr.com/photos/b/1/"));

        // The try after the pause fails, and pauses the host again straight away.
        await client.SavePage("https://www.flickr.com/photos/a/again/");
        Assert.NotNull(client.PausedUntil("https://www.flickr.com/photos/b/1/"));
    }

    [Fact]
    public async Task A_capture_that_works_clears_the_failures()
    {
        var failing = new FakeArchive();
        failing.Failing("www.flickr.com");
        var flaky = new PacedArchiveClient(new Alternating(failing, new FakeArchive()), NullLogger<PacedArchiveClient>.Instance,
            TimeSpan.Zero, TimeProvider.System);

        for (var i = 0; i < ArchiveBotConfig.HostFailuresBeforePause * 2; i++)
        {
            await flaky.SavePage($"https://www.flickr.com/photos/a/{i}/");
        }

        Assert.Null(flaky.PausedUntil("https://www.flickr.com/photos/b/1/"));
    }

    // Fails, then works, then fails.
    private sealed class Alternating(IArchiveClient first, IArchiveClient second) : IArchiveClient
    {
        private int _calls;

        public Task<IApiResponse> SavePage(string url) => _calls++ % 2 == 0 ? first.SavePage(url) : second.SavePage(url);
    }

    [Fact]
    public async Task A_failed_save_without_a_reason_gives_the_status()
    {
        var response = await Save((HttpStatusCode)520, "<html><body>Bad gateway</body></html>");

        Assert.Equal("status 520", PacedArchiveClient.FailureReason(response));
    }
}
