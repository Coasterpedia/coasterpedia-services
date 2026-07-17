namespace CoasterpediaServices.ImageFetch.Fetchers;

public interface ICollectionFetcher
{
    bool CanHandle(Uri uri);

    Task<IReadOnlyList<CollectionItem>> FetchAsync(Uri uri, CancellationToken cancellationToken);
}
