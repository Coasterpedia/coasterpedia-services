namespace CoasterpediaServices.ArchiveBot;

public class NoRedirectHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        InnerHandler = new HttpClientHandler
        {
            AllowAutoRedirect = false
        };

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}