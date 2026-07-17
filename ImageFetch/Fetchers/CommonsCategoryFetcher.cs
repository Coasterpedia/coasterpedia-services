using CoasterpediaServices.ImageFetch.Clients.Commons;
using WikiClientLibrary.Generators;

namespace CoasterpediaServices.ImageFetch.Fetchers;

public class CommonsCategoryFetcher : ICollectionFetcher
{
    private const string CategoryPathPrefix = "/wiki/Category:";
    private const int MaxItems = 500;

    private readonly CommonsSiteAccessor _siteAccessor;

    public CommonsCategoryFetcher(CommonsSiteAccessor siteAccessor)
    {
        _siteAccessor = siteAccessor;
    }

    public bool CanHandle(Uri uri) =>
        uri.Host == "commons.wikimedia.org" && uri.AbsolutePath.StartsWith(CategoryPathPrefix, StringComparison.Ordinal);

    public async Task<IReadOnlyList<CollectionItem>> FetchAsync(Uri uri, CancellationToken cancellationToken)
    {
        var commonsSite = await _siteAccessor.GetCommonsAsync();
        var categoryName = Uri.UnescapeDataString(uri.AbsolutePath[("/wiki/".Length)..]);

        var generator = new CategoryMembersGenerator(commonsSite, categoryName)
        {
            MemberTypes = CategoryMemberTypes.File
        };

        var items = new List<CollectionItem>();
        await foreach (var page in generator.EnumPagesAsync().WithCancellation(cancellationToken))
        {
            if (items.Count >= MaxItems)
            {
                break;
            }

            // Keep ':' literal (the namespace separator) - CommonsFetcher matches on a literal
            // "/wiki/File:" prefix, so an escaped colon would make this URL unrecognisable to it.
            var titleUnderscored = page.Title!.Replace(' ', '_');
            var encodedTitle = Uri.EscapeDataString(titleUnderscored).Replace("%3A", ":");
            // Special:FilePath is a public redirect straight to a thumbnail - no imageinfo
            // round-trip needed, safe to hotlink from an <img> same as the Flickr CDN URL.
            var thumbUrl = $"https://commons.wikimedia.org/wiki/Special:FilePath/{encodedTitle["File:".Length..]}?width=150";
            items.Add(new CollectionItem($"https://commons.wikimedia.org/wiki/{encodedTitle}", page.Title, thumbUrl));
        }

        return items;
    }
}
