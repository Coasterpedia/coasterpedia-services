using System.Globalization;
using CoasterpediaServices.Projector.Data;
using CoasterpediaServices.Projector.Model;
using CoasterpediaServices.Projector.Vocabulary;

namespace CoasterpediaServices.Projector.Mapping;

/// <summary>
/// Product row and its variant rows → public product record. Pure, like the
/// other mappers. Installations are not mapped here: the edge counts them from
/// the attractions' products (DR-010 §12.13). Designers are names only, and
/// the open lists (types, lift/launch) are published as written (§12.18).
/// </summary>
public static class ProductMapper
{
    /// <summary>The Vocabularies key for the three product infoboxes' allowed values.</summary>
    public const string VocabType = "product";

    /// <summary>A capability list's API name → the infobox param whose allowed values check it (the first slot; every slot shares the list).</summary>
    public static readonly IReadOnlyDictionary<string, string> OfferedParams = new Dictionary<string, string>
    {
        ["track_material"] = "track_material1",
        ["support_material"] = "support_material1",
        ["restraints"] = "restraints1",
        ["classes"] = "class1",
        ["containment"] = "containment1",
        ["slide_material"] = "slide_material1",
    };

    /// <summary>Every link target the rows cite, for one batched resolve.</summary>
    public static IEnumerable<string> LinkTargets(IEnumerable<ProductRow> rows) =>
        rows.SelectMany(r => Wikitext.Refs(r.Manufacturer))
            .Select(x => x.Target)
            .OfType<string>();

    public static ProductContent Map(ProductRow row, Vocabularies vocab, IReadOnlyDictionary<string, int> resolved,
        string publicBaseUrl, List<DataIssue> issues)
    {
        var title = row.PageName;
        var offered = new ProductOffered
        {
            TrackMaterial = Offered(row.PageId, "track_material", row.TrackMaterial, vocab, issues),
            SupportMaterial = Offered(row.PageId, "support_material", row.SupportMaterial, vocab, issues),
            Restraints = Offered(row.PageId, "restraints", row.Restraints, vocab, issues),
            Classes = Offered(row.PageId, "classes", row.Classes, vocab, issues),
            Containment = Offered(row.PageId, "containment", row.Containment, vocab, issues),
            SlideMaterial = Offered(row.PageId, "slide_material", row.SlideMaterial, vocab, issues),
            RidersPerTube = RidersPerTube(row.PageId, row.RidersTube, issues),
            // Coaster and slide products keep Type as a list, ride products as one cell.
            Types = Wikitext.Labels([.. FieldMapper.Split(row.Types), row.TypeText]) is { Count: > 0 } types ? types : null,
            LiftLaunch = Wikitext.Labels(FieldMapper.Split(row.LiftLaunch)) is { Count: > 0 } lift ? lift : null,
        };
        var variants = Variants(row, issues);

        return new ProductContent
        {
            Id = row.PageId,
            Type = row.Type,
            Name = FieldMapper.Name(row.Name, title),
            Title = title,
            Url = Wikitext.ArticleUrl(publicBaseUrl, title),
            Status = FieldMapper.Status(row.PageId, VocabType, row.Status, vocab, issues),
            // Every link, as on attractions: 9 products name two or more firms (a
            // co-build, or a range that changed hands) on 2026-10-12.
            Manufacturers = Wikitext.Refs(row.Manufacturer)
                .Select(x => x.Target != null && resolved.TryGetValue(x.Target, out var id) ? x.Ref with { Id = id } : x.Ref)
                .ToList(),
            Designers = FieldMapper.Refs(row.PageId, "designers", row.Designer, null, issues),
            Introduced = FieldMapper.Edtf(row.PageId, "introduced", row.IntroducedEdtf, row.IntroducedEarliest, row.IntroducedLatest, issues),
            Discontinued = FieldMapper.Edtf(row.PageId, "discontinued", row.DiscontinuedEdtf, row.DiscontinuedEarliest, row.DiscontinuedLatest, issues),
            Offered = offered.IsEmpty ? null : offered,
            Variants = variants.Count == 0 ? null : variants,
        };
    }

    /// <summary>
    /// Every product page stores one unnamed row (its infobox's specs) and one
    /// row per variant block. The unnamed row is published only when it carries
    /// something: on 2026-10-12, 875 of 1,092 were empty, a product with no
    /// catalogued configuration. Specs both there and in blocks break the
    /// wiki's either-or rule (no page did, 2026-10-12); both are published and
    /// the page reported.
    /// </summary>
    private static List<ProductVariant> Variants(ProductRow row, List<DataIssue> issues)
    {
        var result = new List<ProductVariant>();
        var named = row.Variants.Count(v => !string.IsNullOrWhiteSpace(v.Name));
        foreach (var v in row.Variants.OrderBy(v => v.RowId))
        {
            var name = Wikitext.Strip(v.Name ?? "");
            var group = Wikitext.Strip(v.Group ?? "");
            // The synthesised row stores no Units: the page's are the ones its figures were typed in.
            var stats = Stats(row.PageId, v, v.Stats.Units ?? row.Units, issues);
            var variant = new ProductVariant(name.Length == 0 ? null : name)
            {
                Group = group.Length == 0 ? null : group,
                Introduced = FieldMapper.Edtf(row.PageId, "variants.introduced", v.IntroducedEdtf, v.IntroducedEarliest, v.IntroducedLatest, issues),
                Discontinued = FieldMapper.Edtf(row.PageId, "variants.discontinued", v.DiscontinuedEdtf, v.DiscontinuedEarliest, v.DiscontinuedLatest, issues),
                Stats = stats,
            };

            if (variant.Name == null)
            {
                if (variant == new ProductVariant(null))
                {
                    continue;
                }

                if (named > 0)
                {
                    issues.Add(new DataIssue(row.PageId, "variants", $"{named} variant blocks",
                        "specs in the product infobox and in variant blocks; both published"));
                }
            }

            result.Add(variant);
        }

        return result;
    }

    private static VariantStats? Stats(int pageId, VariantRow v, string? units, List<DataIssue> issues)
    {
        var common = StatsMapper.Map(pageId, v.Stats with { Units = units }, issues);
        var stats = (common == null ? new VariantStats() : new VariantStats(common)) with
        {
            Sweep = v.Sweep is { } s ? Math.Round(s, 6) : null,
            Cabins = Positive(pageId, "cabins", v.Cabins, issues),
            NumberOfSlides = Positive(pageId, "number_of_slides", v.NumberOfSlides, issues),
            MaxWeight = StatsMapper.Weight(pageId, v.MaxWeight, units, "max_weight", issues),
            MaxWeightPerTube = StatsMapper.Weight(pageId, v.MaxWeightPerTube, units, "max_weight_per_tube", issues),
        };
        return stats.IsEmpty ? null : stats;
    }

    private static int? Positive(int pageId, string field, int? value, List<DataIssue> issues)
    {
        if (value is not { } n)
        {
            return null;
        }

        if (n > 0)
        {
            return n;
        }

        issues.Add(new DataIssue(pageId, field, n.ToString(CultureInfo.InvariantCulture), "not a positive number"));
        return null;
    }

    /// <summary>A Cargo list as stored: '#'-separated, trimmed, empties dropped, each value once, in the page's order.</summary>

    /// <summary>Each value checked against the product infoboxes' list and published as its slug; one outside it is dropped and reported.</summary>
    private static IReadOnlyList<string>? Offered(int pageId, string field, string? full, Vocabularies vocab, List<DataIssue> issues) =>
        FieldMapper.Choices(pageId, $"offered.{field}", VocabType, OfferedParams[field], full, vocab, issues);

    /// <summary>Riders_Tube is a list of Integer, so each value is a whole number already; anything else is reported.</summary>
    private static IReadOnlyList<int>? RidersPerTube(int pageId, string? full, List<DataIssue> issues)
    {
        var result = new List<int>();
        foreach (var value in FieldMapper.Split(full))
        {
            if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0)
            {
                if (!result.Contains(n))
                {
                    result.Add(n);
                }
            }
            else
            {
                issues.Add(new DataIssue(pageId, "offered.riders_per_tube", value, "not a positive whole number"));
            }
        }

        return result.Count == 0 ? null : result;
    }
}
