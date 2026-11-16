using System.Text.RegularExpressions;

namespace CoasterpediaServices.ImageFetch.Fetchers;

// The credit for a Flickr photo: the owner's real name, or the username when the real
// name is empty or carries a link. Some owners use the real-name field as a profile
// banner and a listed link there makes the wiki's spam filter refuse the whole file page. 
// The username is the same person's
// own name for themselves, without the banner.
internal static partial class FlickrCredit
{
    public static string For(string? realname, string username)
    {
        if (string.IsNullOrWhiteSpace(realname)) return username;
        if (!HasLink(realname)) return realname.Trim();
        return string.IsNullOrWhiteSpace(username) ? realname.Trim() : username;
    }

    public static bool HasLink(string text) => LinkPattern().IsMatch(text);

    // A scheme, a leading www., or a bare host followed by a path ("linktr.ee/x").
    [GeneratedRegex(@"https?://|\bwww\.|\b[a-z0-9-]+(\.[a-z0-9-]+)*\.[a-z]{2,}/", RegexOptions.IgnoreCase)]
    private static partial Regex LinkPattern();
}
