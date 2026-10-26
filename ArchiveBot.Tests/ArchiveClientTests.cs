using System.Net;
using System.Text;
using CoasterpediaServices.ArchiveBot.Clients.Archive;
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

    [Fact]
    public async Task A_failed_save_without_a_reason_gives_the_status()
    {
        var response = await Save((HttpStatusCode)520, "<html><body>Bad gateway</body></html>");

        Assert.Equal("status 520", PacedArchiveClient.FailureReason(response));
    }
}
