namespace CoasterpediaServices.ImageFetch.Fetchers;

public interface ISourceFetcher
{
    bool CanHandle(Uri uri);

    Task<FetchResult> FetchAsync(Uri uri, CancellationToken cancellationToken);
}
