using System.Globalization;
using System.Text.RegularExpressions;
using CoasterpediaServices.Projector.Model;
using CoasterpediaServices.Projector.Vocabulary;

namespace CoasterpediaServices.Projector.Mapping;

/// <summary>
/// The field rules every kind shares, so an attraction and a park read a
/// status, a country or a coordinate the same way. Pure, like the mappers.
/// </summary>
/// <remarks><c>vocabType</c> is the Vocabularies key: an attraction type, "park" or "manufacturer".</remarks>
public static partial class FieldMapper
{
    // The infoboxes ask for an external link: [https://example.com example.com].
    [GeneratedRegex(@"^\[(\S+)(?:\s[^\[\]]*)?\]$")]
    private static partial Regex ExternalLink();

    [GeneratedRegex(@"^\d{4}-\d\d-\d\d$")]
    private static partial Regex Day();

    /// <summary>Name is a String column but can still hold markup: a nameless ride is ''unknown''. The title is the safe fallback.</summary>
    public static string Name(string? name, string title)
    {
        var text = Wikitext.Strip(name ?? "");
        return text.Length == 0 ? title : text;
    }

    public static string? Status(int pageId, string vocabType, string? value, Vocabularies vocab, List<DataIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            issues.Add(new DataIssue(pageId, "status", "", "missing"));
            return null;
        }

        var wiki = vocab.Match(vocabType, "status", value);
        if (wiki == null)
        {
            issues.Add(new DataIssue(pageId, "status", value, "not in the infobox's allowed list"));
            return null;
        }

        return Wikitext.Slug(wiki);
    }

    public static Country? Country(int pageId, string vocabType, string? value, Vocabularies vocab, List<DataIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var wiki = vocab.Match(vocabType, "country", value);
        if (wiki == null)
        {
            issues.Add(new DataIssue(pageId, "country", value, "not in the infobox's allowed list"));
            return null;
        }

        if (wiki.Equals("ship", StringComparison.OrdinalIgnoreCase))
        {
            // Allowed, but not a country. An attraction's location will say ship once it carries kind.
            return null;
        }

        var code = Countries.IsoCode(wiki);
        if (code == null)
        {
            issues.Add(new DataIssue(pageId, "country", wiki, "allowed on the wiki but has no ISO code in Countries.cs"));
            return null;
        }

        return new Country(code, wiki);
    }

    public static Coordinates? Coordinates(int pageId, double? lat, double? lon, List<DataIssue> issues)
    {
        if (lat is not { } la || lon is not { } lo)
        {
            return null;
        }

        if (la is < -90 or > 90 || lo is < -180 or > 180 || (la == 0 && lo == 0))
        {
            issues.Add(new DataIssue(pageId, "coordinates", $"{la},{lo}", "out of range"));
            return null;
        }

        return new Coordinates(la, lo);
    }

    /// <summary>
    /// A Date column with no EDTF beside it, at Cargo's precision: 0 date and
    /// time, 1 date, 2 month, 3 year. It holds exact dates only.
    /// </summary>
    public static DateValue? Precise(int pageId, string field, string? date, int? precision, List<DataIssue> issues)
    {
        if (Date(pageId, field, date, issues) is not { } day)
        {
            return null;
        }

        var d = DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        switch (precision)
        {
            case 0 or 1:
                return new DateValue(day, day, day);
            case 2:
                var month = new DateOnly(d.Year, d.Month, 1);
                return new DateValue(d.ToString("yyyy-MM", CultureInfo.InvariantCulture), Iso(month), Iso(month.AddMonths(1).AddDays(-1)));
            case 3:
                return new DateValue(d.ToString("yyyy", CultureInfo.InvariantCulture), Iso(new DateOnly(d.Year, 1, 1)), Iso(new DateOnly(d.Year, 12, 31)));
            default:
                issues.Add(new DataIssue(pageId, field, $"{day} (precision {precision?.ToString(CultureInfo.InvariantCulture) ?? "none"})", "unknown date precision"));
                return null;
        }
    }

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>A stored day, yyyy-mm-dd; null when blank, and reported when it is something else.</summary>
    public static string? Date(int pageId, string field, string? value, List<DataIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var v = value.Trim();
        if (Day().IsMatch(v) && DateOnly.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            return v;
        }

        issues.Add(new DataIssue(pageId, field, v, "not a date"));
        return null;
    }

    /// <summary>A9's columns: the EDTF as the infobox wrote it, and its bounds. A bound is null at an open end.</summary>
    public static DateValue? Edtf(int pageId, string field, string? edtf, string? earliest, string? latest, List<DataIssue> issues)
    {
        var first = Date(pageId, field, earliest, issues);
        var last = Date(pageId, field, latest, issues);
        if (string.IsNullOrWhiteSpace(edtf))
        {
            if (first != null || last != null)
            {
                issues.Add(new DataIssue(pageId, field, $"{first}..{last}", "bounds stored with no EDTF"));
            }

            return null;
        }

        // Every stored EDTF had a bound on 2026-10-12; one with neither would read as "any time".
        if (first == null && last == null)
        {
            issues.Add(new DataIssue(pageId, field, edtf, "EDTF stored with no bounds"));
            return null;
        }

        if (first != null && last != null && string.CompareOrdinal(first, last) > 0)
        {
            issues.Add(new DataIssue(pageId, field, edtf, "earliest is after latest"));
            return null;
        }

        return new DateValue(edtf.Trim(), first, last);
    }

    /// <summary>One value from an infobox's allowed list, as its slug; outside the list it is null and reported.</summary>
    public static string? Choice(int pageId, string field, string vocabType, string param, string? value, Vocabularies vocab,
        List<DataIssue> issues)
    {
        var text = value == null ? "" : Wikitext.Strip(value);
        if (text.Length == 0)
        {
            return null;
        }

        var wiki = vocab.Match(vocabType, param, text);
        if (wiki == null)
        {
            issues.Add(new DataIssue(pageId, field, text, "not in the infobox's allowed list"));
            return null;
        }

        return Wikitext.Slug(wiki);
    }

    /// <summary>
    /// A Cargo list field ('#'-separated) checked value by value against one
    /// allowed list, as slugs in page order: a value outside it is dropped and
    /// reported. Null when nothing is left.
    /// </summary>
    public static IReadOnlyList<string>? Choices(int pageId, string field, string vocabType, string param, string? full,
        Vocabularies vocab, List<DataIssue> issues)
    {
        var result = new List<string>();
        foreach (var value in Split(full))
        {
            if (Choice(pageId, field, vocabType, param, value, vocab, issues) is { } slug && !result.Contains(slug))
            {
                result.Add(slug);
            }
        }

        return result.Count == 0 ? null : result;
    }

    /// <summary>A Cargo list field's members, as stored ('#'-separated), trimmed and each once.</summary>
    public static IEnumerable<string> Split(string? full) =>
        string.IsNullOrWhiteSpace(full)
            ? []
            : full.Split('#').Select(v => v.Trim()).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// One http(s) URL, bare or as the single external link the infobox asks
    /// for. Anything else (two links, a note after the link, no scheme) is
    /// null and reported: it is wikitext, and wikitext is never published.
    /// </summary>
    public static string? Website(int pageId, string? value, List<DataIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var v = value.Trim();
        var link = ExternalLink().Match(v);
        var url = link.Success ? link.Groups[1].Value : v;
        if (!url.Any(char.IsWhiteSpace)
            && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return url;
        }

        issues.Add(new DataIssue(pageId, "website", v, "not a single http(s) URL"));
        return null;
    }
}
