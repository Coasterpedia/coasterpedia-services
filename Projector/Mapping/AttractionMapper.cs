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
            // Never blank on any table (checked 2026-10-11).
            Name = FieldMapper.Name(row.Name, title),
            Title = title,
            Url = Wikitext.ArticleUrl(publicBaseUrl, title),
            Status = FieldMapper.Status(row.PageId, row.Type, row.Status, vocab, issues),
            Location = new AttractionLocation(
                FieldMapper.Country(row.PageId, row.Type, row.Country, vocab, issues),
                FieldMapper.Coordinates(row.PageId, row.Lat, row.Lon, issues)),
            Manufacturers = Wikitext.Refs(row.Manufacturer)
                .Select(x => x.Target != null && resolved.TryGetValue(x.Target, out var id) ? x.Ref with { Id = id } : x.Ref)
                .ToList(),
            Stats = StatsMapper.Map(row.PageId, row.Stats, issues),
        };
    }
}
