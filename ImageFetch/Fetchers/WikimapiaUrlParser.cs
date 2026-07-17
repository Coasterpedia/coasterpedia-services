using System.Text.RegularExpressions;

namespace CoasterpediaServices.ImageFetch.Fetchers;

/// <summary>
/// Wikimapia URLs come in three shapes that all need to resolve to the same (objectId, photoId) pair:
/// a plain object page (/13942468/Slug), a map-browser deep link where the object lives in a
/// "show=" param inside the URL fragment (#lang=en&amp;...&amp;show=/13942468/Slug&amp;...), and either
/// form with a trailing "/photo/{id}" pointing at one specific photo (in the path, in the "show="
/// value, or appended as its own fragment e.g. "/13942468/Slug#/photo/2445486").
/// </summary>
internal static class WikimapiaUrlParser
{
    private static readonly Regex ObjectPathRegex = new(
        @"^/?(?<objectId>\d+)(?:/[^/]*)?(?:/photo/(?<photoId>\d+))?/?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsWikimapiaHost(Uri uri) =>
        uri.Host == "wikimapia.org" || uri.Host.EndsWith(".wikimapia.org", StringComparison.OrdinalIgnoreCase);

    public static bool TryParse(Uri uri, out string objectId, out string? photoId)
    {
        objectId = string.Empty;
        photoId = null;

        if (!IsWikimapiaHost(uri))
        {
            return false;
        }

        var fragment = uri.Fragment.TrimStart('#');
        var pathToParse = fragment.Contains('=')
            ? ExtractFragmentParam(fragment, "show") is { } show ? Uri.UnescapeDataString(show) : null
            : uri.AbsolutePath + fragment;

        if (pathToParse == null)
        {
            return false;
        }

        var match = ObjectPathRegex.Match(pathToParse);
        if (!match.Success)
        {
            return false;
        }

        objectId = match.Groups["objectId"].Value;
        photoId = match.Groups["photoId"].Success ? match.Groups["photoId"].Value : null;
        return true;
    }

    private static string? ExtractFragmentParam(string fragment, string key)
    {
        foreach (var part in fragment.Split('&'))
        {
            var eq = part.IndexOf('=');
            if (eq > 0 && part[..eq] == key)
            {
                return part[(eq + 1)..];
            }
        }

        return null;
    }
}
