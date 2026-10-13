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

    [GeneratedRegex(@"[\s-]+")]
    private static partial Regex SlugSeparatorPattern();

    [GeneratedRegex(@"<br\s*/?>|\n", RegexOptions.IgnoreCase)]
    private static partial Regex BreakPattern();

    [GeneratedRegex(@"<small>.*?</small>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex SmallPattern();

    [GeneratedRegex(@"\([^()]*\)")]
    private static partial Regex ParenthesesPattern();

    // A comma separates two names unless what follows is a company or name suffix ("The Gravity Group, LLC", "Clair Hain, Jr.").
    [GeneratedRegex(@",(?!\s*(?:LLC|L\.L\.C\.|Inc|Ltd|Limited|Co|Corp|Corporation|Jr|Sr|GmbH|AG|S\.?A|S\.?A\.?U|S\.?L|B\.?V|N\.?V|plc|LLP|L\.?P|S\.?p\.?A|Pty|K\.?K|Oy|AB|A/S)(?:\W|$))\s*", RegexOptions.IgnoreCase)]
    private static partial Regex NameCommaPattern();

    // Joining words and punctuation left between links: "[[A]] and [[B]]", "[[A]], [[B]]", "[[A]] / [[B]]".
    [GeneratedRegex(@"^(?:[\s,;/&+–—-]|\band\b)*$", RegexOptions.IgnoreCase)]
    private static partial Regex JoinerPattern();

    [GeneratedRegex(@"^[?~\s]+$")]
    private static partial Regex QuestionMarksPattern();

    /// <summary>
    /// A cell that may name several things, as Refs (DR-010 §12.18). Pieces are
    /// split at line breaks. A piece with links gives its links, and anything
    /// else in it is a note ("(2016-present)", "(95%)") and goes; text that is
    /// more than a note is passed to <paramref name="dropped"/> to report. A
    /// piece without links is plain names, split at commas unless a suffix
    /// follows ("The Gravity Group, LLC"), with bracketed notes removed and
    /// placeholders left out. Deduplicated by target, else by name.
    /// </summary>
    public static IReadOnlyList<(string? Target, Ref Ref)> RefList(string? wikitext, Action<string>? dropped = null)
    {
        if (string.IsNullOrWhiteSpace(wikitext))
        {
            return [];
        }

        var refs = new List<(string?, Ref)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? target, string name)
        {
            if (seen.Add(target ?? "\0" + name))
            {
                refs.Add((target, new Ref(null, name)));
            }
        }

        foreach (var piece in BreakPattern().Split(wikitext))
        {
            var links = LinkPattern().Matches(piece);
            if (links.Count > 0)
            {
                foreach (Match m in links)
                {
                    var target = NormaliseTitle(m.Groups[1].Value);
                    if (target == null)
                    {
                        continue;
                    }

                    var display = m.Groups[2].Success ? Strip(m.Groups[2].Value) : null;
                    Add(target, string.IsNullOrEmpty(display) ? target : display);
                }

                var rest = Notes(LinkPattern().Replace(piece, " "));
                if (!JoinerPattern().IsMatch(rest))
                {
                    dropped?.Invoke(Strip(piece));
                }

                continue;
            }

            foreach (var part in NameCommaPattern().Split(Notes(piece)))
            {
                var name = part.Trim().Trim(',', ';').Trim();
                if (name.Length > 0 && !IsPlaceholder(name) && !QuestionMarksPattern().IsMatch(name))
                {
                    Add(null, name);
                }
            }
        }

        return refs;
    }

    /// <summary>Plain text with &lt;small&gt; asides and bracketed notes removed.</summary>
    private static string Notes(string wikitext)
    {
        var text = SmallPattern().Replace(wikitext, " ");
        string before;
        do
        {
            before = text;
            text = ParenthesesPattern().Replace(text, " ");
        } while (text != before);

        return Strip(text);
    }

    /// <summary>
    /// A cell of plain labels (a type, a category): markup stripped, split at
    /// line breaks, blanks and placeholders left out, deduplicated case-blind,
    /// in order.
    /// </summary>
    public static IReadOnlyList<string> Labels(IEnumerable<string?> cells)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in cells)
        {
            foreach (var piece in BreakPattern().Split(cell ?? ""))
            {
                var label = Strip(piece);
                if (label.Length > 0 && !IsPlaceholder(label) && seen.Add(label))
                {
                    result.Add(label);
                }
            }
        }

        return result;
    }

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

    /// <summary>A value that stands for "not known" ("unknown", "?", "TBA"): it names nothing.</summary>
    public static bool IsPlaceholder(string text) => Placeholders.Contains(text.Trim());

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

    /// <summary>
    /// A vocabulary value as the API spells it: lower case, spaces and hyphens
    /// to underscores (DR-010 §6.2), so "Lap-bars" is lap_bars, as the contract
    /// has it. No value published before products had a hyphen.
    /// </summary>
    public static string Slug(string value) =>
        SlugSeparatorPattern().Replace(value.Trim().ToLowerInvariant(), "_");

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
