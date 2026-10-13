using CoasterpediaServices.Projector.Data;
using CoasterpediaServices.Projector.Model;
using CoasterpediaServices.Projector.Vocabulary;

namespace CoasterpediaServices.Projector.Mapping;

/// <summary>
/// Amusement_Parks row → public park record. Pure, like AttractionMapper. The
/// parks slice publishes only what the rebuilt table holds cleanly; owner,
/// operator and history wait for their own slice (DR-010 §12.7).
/// </summary>
public static class ParkMapper
{
    /// <summary>The Vocabularies key for the park infobox's allowed values.</summary>
    public const string VocabType = "park";

    /// <param name="resort">The park's resort, from <see cref="Resorts"/>; null when it is in none.</param>
    public static ParkContent Map(ParkRow row, Vocabularies vocab, string publicBaseUrl, List<DataIssue> issues,
        Resort? resort = null)
    {
        var title = row.PageName;
        var dates = new ParkDates(
            FieldMapper.Edtf(row.PageId, "opened", row.OpenedEdtf, row.OpenedEarliest, row.OpenedLatest, issues),
            FieldMapper.Edtf(row.PageId, "closed", row.ClosedEdtf, row.ClosedEarliest, row.ClosedLatest, issues),
            FieldMapper.Precise(row.PageId, "opening", row.Opening, row.OpeningPrecision, issues));
        var place = Wikitext.Strip(row.Location ?? "");
        return new ParkContent
        {
            Id = row.PageId,
            Name = FieldMapper.Name(row.Name, title),
            Title = title,
            Url = Wikitext.ArticleUrl(publicBaseUrl, title),
            Status = FieldMapper.Status(row.PageId, VocabType, row.Status, vocab, issues),
            Dates = dates.IsEmpty ? null : dates,
            Place = place.Length == 0 ? null : place,
            State = State(row, vocab, issues),
            Country = FieldMapper.Country(row.PageId, VocabType, row.Country, vocab, issues),
            Coordinates = FieldMapper.Coordinates(row.PageId, row.Lat, row.Lon, issues),
            Resort = resort,
            Website = FieldMapper.Website(row.PageId, row.Website, issues),
        };
    }

    /// <summary>Every member title the resorts name, for one batched resolve.</summary>
    public static IEnumerable<string> MemberTitles(IEnumerable<ResortMemberRow> members) =>
        members.Select(m => Wikitext.NormaliseTitle(m.Park ?? "")).OfType<string>();

    /// <summary>
    /// Park page ID → its resort. A member is matched by its title, resolved
    /// through redirects, so a park moved since the wrapper was saved still
    /// finds its resort. A member that is no page, or not a park, attaches to
    /// nothing. A park two wrappers claim keeps the first by resort name and is
    /// reported; none did on 2026-10-12.
    /// </summary>
    /// <param name="parkIds">The page IDs that are parks, so a member resolving to anything else is ignored.</param>
    public static Dictionary<int, Resort> Resorts(IEnumerable<ResortMemberRow> members, IReadOnlyDictionary<string, int> resolved,
        IReadOnlySet<int> parkIds, List<DataIssue> issues)
    {
        var result = new Dictionary<int, Resort>();
        var ordered = members
            .Select(m => (Row: m, Name: Wikitext.Strip(m.Resort ?? ""), Title: Wikitext.NormaliseTitle(m.Park ?? "")))
            .Where(m => m.Name.Length > 0 && m.Title != null)
            .OrderBy(m => m.Name, StringComparer.Ordinal).ThenBy(m => m.Row.ResortId).ThenBy(m => m.Row.Ordering);
        foreach (var (row, name, title) in ordered)
        {
            if (!resolved.TryGetValue(title!, out var parkId) || !parkIds.Contains(parkId))
            {
                continue;
            }

            if (result.TryGetValue(parkId, out var first))
            {
                if (first.Id != row.ResortId)
                {
                    issues.Add(new DataIssue(parkId, "resort", name, $"also a member of {first.Name}; only that one is published"));
                }

                continue;
            }

            result[parkId] = new Resort(row.ResortId, name);
        }

        return result;
    }

    /// <summary>US states and territories, from the infobox's list; a value outside it is null and reported.</summary>
    private static string? State(ParkRow row, Vocabularies vocab, List<DataIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(row.State))
        {
            return null;
        }

        var wiki = vocab.Match(VocabType, "state", row.State);
        if (wiki == null)
        {
            issues.Add(new DataIssue(row.PageId, "state", row.State, "not in the infobox's allowed list"));
        }

        return wiki;
    }
}
