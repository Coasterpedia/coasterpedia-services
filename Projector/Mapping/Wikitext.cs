using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CoasterpediaServices.Projector.Model;

namespace CoasterpediaServices.Projector.Mapping;

/// <summary>
/// The little wikitext the Projector reads: links in Cargo "Wikitext string"
/// columns. Wikitext is never published (openapi.yaml); links become Refs and
/// everything else is stripped.
/// </summary>
public static partial class Wikitext
{
    // Values that say "nobody knows" rather than naming anything (DR-006's placeholders).
    private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "unknown", "?", "n/a", "none", "-", "—", "tba", "tbc",
    };

    [GeneratedRegex(@"\[\[([^\[\]|]+)(?:\|([^\[\]]*))?\]\]")]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"'{2,}")]
    private static partial Regex QuotePattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacePattern();

    /// <summary>
    /// Every link in the value, in order, deduplicated by target. With no link,
    /// the whole value as one unlinked Ref, unless it is empty or a placeholder.
    /// Ids are left null; <see cref="TitleResolver"/> fills them.
    /// </summary>
    public static IReadOnlyList<(string? Target, Ref Ref)> Refs(string? wikitext)
    {
        if (string.IsNullOrWhiteSpace(wikitext))
        {
            return [];
        }

        var refs = new List<(string?, Ref)>();
        var seen = new HashSet<string>();
        foreach (Match m in LinkPattern().Matches(wikitext))
        {
            var target = NormaliseTitle(m.Groups[1].Value);
            if (target == null || !seen.Add(target))
            {
                continue;
            }

            var display = m.Groups[2].Success ? Strip(m.Groups[2].Value) : null;
            refs.Add((target, new Ref(null, string.IsNullOrEmpty(display) ? target : display)));
        }

        if (refs.Count > 0)
        {
            return refs;
        }

        var text = Strip(wikitext);
        return text.Length == 0 || Placeholders.Contains(text) ? [] : [(null, new Ref(null, text))];
    }

    /// <summary>Plain text: links become their display text, tags and bold/italic go.</summary>
    public static string Strip(string wikitext)
    {
        var text = LinkPattern().Replace(wikitext, m => m.Groups[2].Success ? m.Groups[2].Value : m.Groups[1].Value);
        text = TagPattern().Replace(text, " ");
        text = QuotePattern().Replace(text, "");
        return SpacePattern().Replace(text, " ").Trim();
    }

    /// <summary>
    /// A link target as MediaWiki stores the title: no fragment, spaces not
    /// underscores, first letter upper case. Null for a fragment-only link.
    /// </summary>
    public static string? NormaliseTitle(string target)
    {
        var t = target.Trim().TrimStart(':');
        var hash = t.IndexOf('#');
        if (hash >= 0)
        {
            t = t[..hash];
        }

        t = SpacePattern().Replace(t.Replace('_', ' '), " ").Trim();
        if (t.Length == 0)
        {
            return null;
        }

        var first = char.ConvertFromUtf32(char.ConvertToUtf32(t, 0));
        return first.ToUpper(CultureInfo.InvariantCulture) + t[first.Length..];
    }

    /// <summary>A vocabulary value as the API spells it: lower case, spaces to underscores (DR-010 §6.2).</summary>
    public static string Slug(string value) =>
        SpacePattern().Replace(value.Trim().ToLowerInvariant(), "_");

    /// <summary>Case- and accent-blind sort key, so "Él" sorts with "El".</summary>
    public static string SortKey(string name)
    {
        var decomposed = name.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }

    /// <summary>The article URL, encoded the way MediaWiki's wfUrlencode leaves it.</summary>
    public static string ArticleUrl(string baseUrl, string title)
    {
        var encoded = Uri.EscapeDataString(title.Replace(' ', '_'));
        foreach (var keep in new[] { ";", "@", "$", "!", "*", "(", ")", ",", "/", "~", ":" })
        {
            encoded = encoded.Replace(Uri.EscapeDataString(keep), keep);
        }

        return baseUrl.TrimEnd('/') + "/wiki/" + encoded;
    }
}
