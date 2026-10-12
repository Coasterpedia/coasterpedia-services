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
        rows.SelectMany(r => Wikitext.Refs(r.Manufacturer).Concat(ProductRefs(r.Product)))
            .Select(x => x.Target)
            .OfType<string>();

    /// <param name="productIds">Every product page's ID: a product link to any other page is reported.</param>
    public static AttractionContent Map(AttractionRow row, Vocabularies vocab, IReadOnlyDictionary<string, int> resolved,
        IReadOnlySet<int> productIds, string publicBaseUrl, List<DataIssue> issues)
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
            Products = Products(row, resolved, productIds, issues),
            Stats = StatsMapper.Map(row.PageId, row.Stats, issues),
        };
    }

    /// <summary>
    /// Every link in the Product list, in page order, each target once; a
    /// member with no link is one unlinked Ref, as the infobox stores it.
    /// </summary>
    private static List<(string? Target, Ref Ref)> ProductRefs(string? product)
    {
        var refs = new List<(string? Target, Ref Ref)>();
        var seen = new HashSet<string>();
        foreach (var x in ProductMapper.Split(product).SelectMany(Wikitext.Refs))
        {
            if (seen.Add(x.Target ?? "\0" + x.Ref.Name))
            {
                refs.Add(x);
            }
        }

        return refs;
    }

    /// <summary>
    /// The infobox has one variant slot for the whole field, so a variant
    /// belongs to a product only when the page names exactly one of each. Any
    /// other shape is published without variants and reported: on 2026-10-12
    /// that was two coasters, each with two products and one variant.
    /// </summary>
    private static List<AttractionProduct> Products(AttractionRow row, IReadOnlyDictionary<string, int> resolved,
        IReadOnlySet<int> productIds, List<DataIssue> issues)
    {
        var refs = ProductRefs(row.Product);
        var variants = ProductMapper.Split(row.ProductVariant).Select(Wikitext.Strip).Where(v => v.Length > 0).ToList();
        var paired = refs.Count == 1 && variants.Count == 1 ? variants[0] : null;
        if (variants.Count > 0 && paired == null)
        {
            issues.Add(new DataIssue(row.PageId, "products.variant", string.Join("; ", variants),
                refs.Count == 0 ? "a variant with no product" : $"{variants.Count} variants for {refs.Count} products; can't tell which goes with which"));
        }

        // A red link is an article not written yet, not a problem: a Ref with no id.
        var products = new List<AttractionProduct>(refs.Count);
        foreach (var (target, r) in refs)
        {
            var product = r;
            if (target != null && resolved.TryGetValue(target, out var id))
            {
                product = r with { Id = id };
                if (!productIds.Contains(id))
                {
                    issues.Add(new DataIssue(row.PageId, "products", target, "links a page that is not a product"));
                }
            }

            products.Add(new AttractionProduct(product, paired));
        }

        return products;
    }
}
