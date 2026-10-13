using CoasterpediaServices.Projector.Data;
using CoasterpediaServices.Projector.Model;
using CoasterpediaServices.Projector.Vocabulary;

namespace CoasterpediaServices.Projector.Mapping;

/// <summary>
/// Amusement_Parks row → public park record. Pure, like AttractionMapper.
/// History's owner and operator spells arrived with park companies (DR-010
/// §12.16), the current ones as owners and operators with §12.18; name
/// history still waits.
/// </summary>
public static class ParkMapper
{
    /// <summary>The Vocabularies key for the park infobox's allowed values.</summary>
    public const string VocabType = "park";

    /// <summary>Statuses after which no spell is in force, whatever History says.</summary>
    private static readonly HashSet<string> Closed = ["defunct", "cancelled"];

    /// <summary>Every company link the parks' History rows cite, for one batched resolve.</summary>
    public static IEnumerable<string> LinkTargets(IEnumerable<ParkRow> rows) =>
        rows.SelectMany(r => r.History).SelectMany(h => Wikitext.RefList(h.Name)).Select(x => x.Target).OfType<string>();

    /// <param name="resolved">Link target → page ID, through redirects; a red link is absent and keeps a null id.</param>
    /// <param name="resort">The park's resort, from <see cref="Resorts"/>; null when it is in none.</param>
    public static ParkContent Map(ParkRow row, Vocabularies vocab, string publicBaseUrl, List<DataIssue> issues,
        IReadOnlyDictionary<string, int>? resolved = null, Resort? resort = null)
    {
        var title = row.PageName;
        var dates = new ParkDates(
            FieldMapper.Edtf(row.PageId, "opened", row.OpenedEdtf, row.OpenedEarliest, row.OpenedLatest, issues),
            FieldMapper.Edtf(row.PageId, "closed", row.ClosedEdtf, row.ClosedEarliest, row.ClosedLatest, issues),
            FieldMapper.Precise(row.PageId, "opening", row.Opening, row.OpeningPrecision, issues));
        var place = Wikitext.Strip(row.Location ?? "");
        var status = FieldMapper.Status(row.PageId, VocabType, row.Status, vocab, issues);
        var history = History(row, status, resolved ?? new Dictionary<string, int>(), issues);
        return new ParkContent
        {
            Id = row.PageId,
            Name = FieldMapper.Name(row.Name, title),
            Title = title,
            Url = Wikitext.ArticleUrl(publicBaseUrl, title),
            Status = status,
            Dates = dates.IsEmpty ? null : dates,
            Place = place.Length == 0 ? null : place,
            State = State(row, vocab, issues),
            Country = FieldMapper.Country(row.PageId, VocabType, row.Country, vocab, issues),
            Coordinates = FieldMapper.Coordinates(row.PageId, row.Lat, row.Lon, issues),
            Resort = resort,
            Website = FieldMapper.Website(row.PageId, row.Website, issues),
            Owners = Current(history?.Owners),
            Operators = Current(history?.Operators),
            History = history,
        };
    }

    /// <summary>The companies of the spell in force, from history; null when none is.</summary>
    private static IReadOnlyList<Ref>? Current(IReadOnlyList<DatedRef>? spells) =>
        spells?.Where(s => s.Current).Select(s => s.Company).ToList() is { Count: > 0 } now ? now : null;

    /// <summary>
    /// History's owner and operator spells, oldest first (the page numbers
    /// them newest first: Position 1 is in force). Each link in a spell is one
    /// company with the spell's dates; unlinked text is a Ref with no id, and a
    /// placeholder ("Unknown") names nobody. Cells are split as every list of
    /// Refs is (Wikitext.RefList): notes beside a link ("(95%)") go, and a
    /// company on a line of its own is kept, linked or not.
    /// </summary>
    private static ParkHistory? History(ParkRow row, string? status, IReadOnlyDictionary<string, int> resolved,
        List<DataIssue> issues)
    {
        List<DatedRef>? Role(string role)
        {
            var spells = row.History.Where(h => h.Type == role).OrderByDescending(h => h.Position ?? 0).ToList();
            var refs = new List<DatedRef>();
            foreach (var h in spells)
            {
                var start = FieldMapper.Precise(row.PageId, $"history.{role}s.start", h.Start, h.StartPrecision, issues);
                var end = FieldMapper.Precise(row.PageId, $"history.{role}s.end", h.End, h.EndPrecision, issues);
                var current = h.Position == 1 && end == null && !Closed.Contains(status ?? "");
                foreach (var company in FieldMapper.Refs(row.PageId, $"history.{role}s", h.Name, resolved, issues) ?? [])
                {
                    refs.Add(new DatedRef(company, start, end, current));
                }
            }

            return refs.Count == 0 ? null : refs;
        }

        var history = new ParkHistory(Role("owner"), Role("operator"));
        return history.Owners == null && history.Operators == null ? null : history;
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
