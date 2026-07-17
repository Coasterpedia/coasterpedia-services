namespace CoasterpediaServices.ImageFetch.Fetchers;

public class ImageFetchDispatcher
{
    private readonly IReadOnlyList<ISourceFetcher> _fetchers;

    public ImageFetchDispatcher(IEnumerable<ISourceFetcher> fetchers)
    {
        _fetchers = fetchers.ToList();
    }

    public bool CanHandle(Uri uri) => _fetchers.Any(f => f.CanHandle(uri));

    public async Task<FetchResult> FetchAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new ImageFetchException(400, "Invalid URL.");
        }

        var fetcher = _fetchers.FirstOrDefault(f => f.CanHandle(uri));
        if (fetcher == null)
        {
            throw new ImageFetchException(400,
                "Unrecognised source. Supported: Wikimedia Commons, Geograph, Flickr.");
        }

        return await fetcher.FetchAsync(uri, cancellationToken);
    }
}
