using CoasterpediaServices.Projector.Data;
using CoasterpediaServices.Projector.Model;
using CoasterpediaServices.Projector.Vocabulary;

namespace CoasterpediaServices.Projector.Mapping;

/// <summary>
/// Cargo row → public record. Pure: no I/O, so the whole contract mapping is
/// testable from fixture rows. Each field the slice publishes is mapped here
/// and nowhere else.
/// </summary>
public static class AttractionMapper
{
    /// <summary>Every link target the rows cite, for one batched resolve.</summary>
    public static IEnumerable<string> LinkTargets(IEnumerable<AttractionRow> rows) =>
        rows.SelectMany(r => Wikitext.Refs(r.Manufacturer))
            .Select(x => x.Target)
            .OfType<string>();

    public static AttractionContent Map(AttractionRow row, Vocabularies vocab,
        IReadOnlyDictionary<string, int> resolved, string publicBaseUrl, List<DataIssue> issues)
    {
        var title = row.PageName;
        return new AttractionContent
        {
            Id = row.PageId,
            Type = row.Type,
            // Name is always set on every table (checked 2026-10-11); the title is the safe fallback.
            Name = string.IsNullOrWhiteSpace(row.Name) ? title : row.Name.Trim(),
            Title = title,
            Url = Wikitext.ArticleUrl(publicBaseUrl, title),
            Status = MapStatus(row, vocab, issues),
            Location = new AttractionLocation(MapCountry(row, vocab, issues), MapCoordinates(row, issues)),
            Manufacturers = Wikitext.Refs(row.Manufacturer)
                .Select(x => x.Target != null && resolved.TryGetValue(x.Target, out var id) ? x.Ref with { Id = id } : x.Ref)
                .ToList(),
        };
    }

    private static string? MapStatus(AttractionRow row, Vocabularies vocab, List<DataIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(row.Status))
        {
            issues.Add(new DataIssue(row.PageId, "status", "", "missing"));
            return null;
        }

        var wiki = vocab.Match(row.Type, "status", row.Status);
        if (wiki == null)
        {
            issues.Add(new DataIssue(row.PageId, "status", row.Status, "not in the infobox's allowed list"));
            return null;
        }

        return Wikitext.Slug(wiki);
    }

    private static Country? MapCountry(AttractionRow row, Vocabularies vocab, List<DataIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(row.Country))
        {
            return null;
        }

        var wiki = vocab.Match(row.Type, "country", row.Country);
        if (wiki == null)
        {
            issues.Add(new DataIssue(row.PageId, "country", row.Country, "not in the infobox's allowed list"));
            return null;
        }

        if (wiki.Equals("ship", StringComparison.OrdinalIgnoreCase))
        {
            // Allowed, but not a country. The location will say ship once it carries kind.
            return null;
        }

        var code = Countries.IsoCode(wiki);
        if (code == null)
        {
            issues.Add(new DataIssue(row.PageId, "country", wiki, "allowed on the wiki but has no ISO code in Countries.cs"));
            return null;
        }

        return new Country(code, wiki);
    }

    private static Coordinates? MapCoordinates(AttractionRow row, List<DataIssue> issues)
    {
        if (row.Lat is not { } lat || row.Lon is not { } lon)
        {
            return null;
        }

        if (lat is < -90 or > 90 || lon is < -180 or > 180 || (lat == 0 && lon == 0))
        {
            issues.Add(new DataIssue(row.PageId, "coordinates", $"{lat},{lon}", "out of range"));
            return null;
        }

        return new Coordinates(lat, lon);
    }
}
