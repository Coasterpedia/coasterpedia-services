using CoasterpediaServices.Projector.Data;
using CoasterpediaServices.Projector.Model;
using CoasterpediaServices.Projector.Vocabulary;

namespace CoasterpediaServices.Projector.Mapping;

/// <summary>
/// Park_Companies row → public park company record. Pure, like the other
/// mappers. Predecessors and successors are lists of Refs, as for
/// manufacturers (DR-010 §12.18).
/// </summary>
public static class ParkCompanyMapper
{
    /// <summary>The Vocabularies key for the park company infobox's allowed values.</summary>
    public const string VocabType = "park_company";

    /// <summary>Every link the rows' predecessors and successors cite, for one batched resolve.</summary>
    public static IEnumerable<string> LinkTargets(IEnumerable<ParkCompanyRow> rows) =>
        rows.SelectMany(r => Wikitext.RefList(r.Predecessor).Concat(Wikitext.RefList(r.Successor))).Select(x => x.Target).OfType<string>();

    public static ParkCompanyContent Map(ParkCompanyRow row, Vocabularies vocab, string publicBaseUrl, List<DataIssue> issues,
        IReadOnlyDictionary<string, int>? resolved = null)
    {
        var title = row.PageName;
        var headquarters = Wikitext.Strip(row.Headquarters ?? "");
        return new ParkCompanyContent
        {
            Id = row.PageId,
            Name = FieldMapper.Name(row.Name, title),
            Title = title,
            Url = Wikitext.ArticleUrl(publicBaseUrl, title),
            Status = FieldMapper.Status(row.PageId, VocabType, row.Status, vocab, issues),
            Established = FieldMapper.Precise(row.PageId, "established", row.Established, row.EstablishedPrecision, issues),
            Closed = FieldMapper.Precise(row.PageId, "closed", row.Closed, row.ClosedPrecision, issues),
            Headquarters = headquarters.Length == 0 ? null : headquarters,
            Country = FieldMapper.Country(row.PageId, VocabType, row.Country, vocab, issues),
            Website = FieldMapper.Website(row.PageId, row.Website, issues),
            Predecessors = FieldMapper.Refs(row.PageId, "predecessors", row.Predecessor, resolved ?? new Dictionary<string, int>(), issues),
            Successors = FieldMapper.Refs(row.PageId, "successors", row.Successor, resolved ?? new Dictionary<string, int>(), issues),
        };
    }
}
