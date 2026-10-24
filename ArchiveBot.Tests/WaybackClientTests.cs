using System.Net;
using System.Text;
using System.Web;
using CoasterpediaServices.ArchiveBot.Clients.Wayback;
using Refit;
using Xunit;

namespace CoasterpediaServices.ArchiveBot.Tests;

public class WaybackClientTests
{
    private sealed class Capture(string body) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private static (IWaybackClient Client, Capture Handler) Client(string body)
    {
        var handler = new Capture(body);
        var client = RestService.For<IWaybackClient>(new HttpClient(handler) { BaseAddress = new Uri("https://web.archive.org") });
        return (client, handler);
    }

    [Fact]
    public async Task The_query_is_what_the_CDX_server_expects()
    {
        var (client, handler) = Client("""[["timestamp"],["20151127042738"]]""");

        var snapshot = await client.FindSnapshot("https://www.coaster101.com/a?b=1&c=2", new DateTime(2016, 1, 1), null);

        var request = handler.Requests[0];
        var query = HttpUtility.ParseQueryString(request.Query);
        Assert.Equal("/cdx/search/cdx", request.AbsolutePath);
        Assert.Equal("https://www.coaster101.com/a?b=1&c=2", query["url"]);
        Assert.Equal("20160101235959", query["to"]);
        Assert.Null(query["from"]);
        Assert.Equal("-1", query["limit"]);
        Assert.Equal("statuscode:20[036]", query["filter"]);
        Assert.Equal("json", query["output"]);
        Assert.Equal("timestamp", query["fl"]);

        Assert.Equal("https://web.archive.org/web/20151127042738/https://www.coaster101.com/a?b=1&c=2", snapshot!.Url);
        Assert.Equal(new DateTime(2015, 11, 27, 4, 27, 38), snapshot.Captured);
    }

    [Fact]
    public async Task No_captures_is_no_snapshot()
    {
        var (client, handler) = Client("[]");

        Assert.Null(await client.FindSnapshot("https://example.com/", new DateTime(2016, 1, 1), null));
        // Before, then after.
        Assert.Equal(2, handler.Requests.Count);
    }
}
