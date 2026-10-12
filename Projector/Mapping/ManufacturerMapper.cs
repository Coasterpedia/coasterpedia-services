using CoasterpediaServices.Projector.Data;
using CoasterpediaServices.Projector.Model;
using CoasterpediaServices.Projector.Vocabulary;

namespace CoasterpediaServices.Projector.Mapping;

/// <summary>
/// Manufacturers row → public manufacturer record. Pure, like the other
/// mappers. Predecessor and successor are held back: the columns hold lists
/// and notes ("Bankrupt December 3, 2001, bought by [[S&amp;S Worldwide]]"),
/// not the one link the contract's Ref promises (DR-010 §12.8).
/// </summary>
public static class ManufacturerMapper
{
    /// <summary>The Vocabularies key for the manufacturer infobox's allowed values.</summary>
    public const string VocabType = "manufacturer";

    public static ManufacturerContent Map(ManufacturerRow row, Vocabularies vocab, string publicBaseUrl, List<DataIssue> issues)
    {
        var title = row.PageName;
        var headquarters = Wikitext.Strip(row.Headquarters ?? "");
        return new ManufacturerContent
        {
            Id = row.PageId,
            Name = FieldMapper.Name(row.Name, title),
            Title = title,
            Url = Wikitext.ArticleUrl(publicBaseUrl, title),
            Status = FieldMapper.Status(row.PageId, VocabType, row.Status, vocab, issues),
            Founded = FieldMapper.Precise(row.PageId, "founded", row.Founded, row.FoundedPrecision, issues),
            Headquarters = headquarters.Length == 0 ? null : headquarters,
            Country = FieldMapper.Country(row.PageId, VocabType, row.Country, vocab, issues),
            Website = FieldMapper.Website(row.PageId, row.Website, issues),
        };
    }
}
