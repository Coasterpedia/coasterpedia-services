using System.Text.RegularExpressions;

namespace CoasterpediaServices.ArchiveBot.Links;

// A redirect that answers 200 but no longer reaches the cited page. Built from a sample of the
// wiki's own citations (2026-10-24): real moves keep their depth, their slug or their ID; removed
// pages land on a home page, a not-found page, or a landing page above where they were, as every
// Cedar Fair park page did when its site moved to sixflags.com.
public static partial class SoftNotFound
{
    public static bool IsSoftNotFound(Uri original, Uri final)
    {
        if (NotFoundMarker().IsMatch(final.PathAndQuery) && !NotFoundMarker().IsMatch(original.PathAndQuery))
        {
            return true;
        }

        var from = Segments(original);
        var to = Segments(final);

        // A home page that moved is still the home page.
        if (IsHome(from))
        {
            return false;
        }

        if (IsHome(to))
        {
            return true;
        }

        if (to.Count >= from.Count)
        {
            return false;
        }

        // A park or section landing page, such as sixflags.com/cedarpoint or sixflags.com/blog,
        // unless it kept the article's own slug.
        if (to.Count == 1)
        {
            return Slug(to[0]) != Slug(from[^1]);
        }

        // A section further up the article's own path (/news/story/article → /content/news). An ID
        // never counts: /collection/id/14322/rec/13 → /collection/id/14322 is the same item.
        var last = to[^1];
        return !last.Any(char.IsDigit) && from.Take(from.Count - 1).Any(s => Slug(s) == Slug(last));
    }

    private static List<string> Segments(Uri uri) =>
        uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(s => Uri.UnescapeDataString(s).ToLowerInvariant()).ToList();

    private static bool IsHome(List<string> segments) => segments.Count == 0 || (segments.Count == 1 && HomeAlias().IsMatch(segments[0]));

    private static string Slug(string segment) => NonAlphanumeric().Replace(Extension().Replace(segment, ""), "");

    [GeneratedRegex(@"(^|/)404(\.[a-z0-9]+)?(/|$)|not-?found", RegexOptions.IgnoreCase)]
    private static partial Regex NotFoundMarker();

    // index.html, default.aspx, home, or a language root such as en or en-gb.
    [GeneratedRegex(@"^((index|default|home|start|welcome)(\.[a-z0-9]+)?|[a-z]{2}([-_][a-z]{2})?)$")]
    private static partial Regex HomeAlias();

    [GeneratedRegex(@"\.[a-z0-9]{2,5}$")]
    private static partial Regex Extension();

    [GeneratedRegex("[^a-z0-9]")]
    private static partial Regex NonAlphanumeric();
}
