namespace CoasterpediaServices.ImageFetch.Fetchers;

public class ImageFetchCollectionDispatcher
{
    private readonly IReadOnlyList<ICollectionFetcher> _fetchers;

    public ImageFetchCollectionDispatcher(IEnumerable<ICollectionFetcher> fetchers)
    {
        _fetchers = fetchers.ToList();
    }

    public bool CanHandle(Uri uri) => _fetchers.Any(f => f.CanHandle(uri));

    public Task<IReadOnlyList<CollectionItem>> FetchAsync(Uri uri, CancellationToken cancellationToken)
    {
        var fetcher = _fetchers.FirstOrDefault(f => f.CanHandle(uri))
                      ?? throw new ImageFetchException(400, "Unrecognised collection source.");

        return fetcher.FetchAsync(uri, cancellationToken);
    }
}
