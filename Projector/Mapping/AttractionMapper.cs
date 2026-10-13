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
        rows.SelectMany(r => Wikitext.Refs(r.Manufacturer)
                .Concat(ProductRefs(r.Product))
                .Concat(r.Components.SelectMany(c => Wikitext.Refs(c.Product)))
                .Select(x => x.Target)
                .Concat(LocationMapper.LinkTargets(r.Park, r.Owner))
                .Concat(r.PastLocations.SelectMany(p => LocationMapper.LinkTargets(p.Park, p.Owner)))
                .Concat(r.Elements.Select(Wikitext.NormaliseTitle)))
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
            Dates = Dates(row, issues),
            Location = Location(row, vocab, resolved, issues),
            Manufacturers = Wikitext.Refs(row.Manufacturer)
                .Select(x => x.Target != null && resolved.TryGetValue(x.Target, out var id) ? x.Ref with { Id = id } : x.Ref)
                .ToList(),
            Products = Products(row, resolved, productIds, issues),
            Restraints = FieldMapper.Choices(row.PageId, "restraints", row.Type, "restraints1", row.Restraints, vocab, issues),
            Stats = StatsMapper.Map(row.PageId, row.Stats, issues),
            Restrictions = Restrictions(row, issues),
            Coaster = Coaster(row, vocab, resolved, issues),
            Ride = Ride(row, issues),
            WaterSlide = WaterSlide(row, vocab, resolved, issues),
            History = History(row, vocab, resolved, issues),
        };
    }

    private static AttractionDates? Dates(AttractionRow r, List<DataIssue> issues)
    {
        var dates = new AttractionDates(
            FieldMapper.Edtf(r.PageId, "opened", r.OpenedEdtf, r.OpenedEarliest, r.OpenedLatest, issues),
            FieldMapper.Edtf(r.PageId, "closed", r.ClosedEdtf, r.ClosedEarliest, r.ClosedLatest, issues),
            // Announced dates have no EDTF columns (not part of A9): exact, at Cargo's precision.
            FieldMapper.Precise(r.PageId, "opening", r.Opening, r.OpeningPrecision, issues),
            FieldMapper.Precise(r.PageId, "closing", r.Closing, r.ClosingPrecision, issues));
        return dates.IsEmpty ? null : dates;
    }

    private static AttractionLocation Location(AttractionRow r, Vocabularies vocab, IReadOnlyDictionary<string, int> resolved,
        List<DataIssue> issues)
    {
        var kind = Kind(r.PageId, "location.kind", r.LocationKind, issues);
        var where = LocationMapper.Project(kind, r.OnLoan, r.Park, r.Owner, r.Location, r.Type, vocab, resolved);
        var section = Text(r.Section);
        return new AttractionLocation
        {
            Kind = kind,
            OnLoan = r.OnLoan,
            Park = where.Park,
            Owner = where.Owner,
            Place = where.Place,
            State = State(r.PageId, "location.state", r.Type, r.State, vocab, issues),
            Country = FieldMapper.Country(r.PageId, r.Type, r.Country, vocab, issues),
            Coordinates = FieldMapper.Coordinates(r.PageId, r.Lat, r.Lon, issues),
            // A subsection with no section names nothing a reader can find.
            Area = section == null ? null : new Area(section, Text(r.Subsection)),
        };
    }

    /// <summary>LocationKind as Travelling.classify stored it. Open vocabulary: any value is published; a blank one is reported.</summary>
    private static string? Kind(int pageId, string field, string? kind, List<DataIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            issues.Add(new DataIssue(pageId, field, "", "missing"));
            return null;
        }

        return Wikitext.Slug(kind);
    }

    /// <summary>The infobox's state list (US states and territories); a value outside it is null and reported.</summary>
    private static string? State(int pageId, string field, string vocabType, string? value, Vocabularies vocab, List<DataIssue> issues)
    {
        var text = Text(value);
        if (text == null)
        {
            return null;
        }

        var wiki = vocab.Match(vocabType, "state", text);
        if (wiki == null)
        {
            issues.Add(new DataIssue(pageId, field, text, "not in the infobox's allowed list"));
        }

        return wiki;
    }

    private static Restrictions? Restrictions(AttractionRow r, List<DataIssue> issues)
    {
        var units = r.Stats.Units;
        Measurement? Height(double? v, string field) => StatsMapper.RiderHeight(r.PageId, v, units, $"restrictions.{field}", issues);
        Measurement? Weight(double? v, string field) => StatsMapper.Weight(r.PageId, v, units, $"restrictions.{field}", issues);
        var restrictions = new Restrictions
        {
            MinHeight = Height(r.MinHeight, "min_height"),
            MinHeightDriver = Height(r.MinHeightDriver, "min_height_driver"),
            MinHeightStanding = Height(r.MinHeightStanding, "min_height_standing"),
            MinHeightUpsideDown = Height(r.MinHeightUpsideDown, "min_height_upside_down"),
            MinHeightUnaccompanied = Height(r.MinHeightUnaccompanied, "min_height_unaccompanied"),
            MaxHeightWithoutChild = Height(r.MaxHeightWithoutChild, "max_height_without_child"),
            MaxHeight = Height(r.MaxHeight, "max_height"),
            MinAge = Age(r.PageId, r.MinAge, "min_age", issues),
            MinAgeDriver = Age(r.PageId, r.MinAgeDriver, "min_age_driver", issues),
            MinAgeUnaccompanied = Age(r.PageId, r.MinAgeUnaccompanied, "min_age_unaccompanied", issues),
            MaxAgeWithoutChild = Age(r.PageId, r.MaxAgeWithoutChild, "max_age_without_child", issues),
            MaxAge = Age(r.PageId, r.MaxAge, "max_age", issues),
            ChildrenOnly = r.ChildrenOnly,
            AdultOnlyWithChild = r.AdultOnlyWithChild,
            MinWeight = Weight(r.MinWeight, "min_weight"),
            MaxWeight = Weight(r.MaxWeight, "max_weight"),
            MaxWeightPerTube = Weight(r.MaxWeightPerTube, "max_weight_per_tube"),
        };
        return restrictions.IsEmpty ? null : restrictions;
    }

    private static int? Age(int pageId, int? age, string field, List<DataIssue> issues)
    {
        if (age is not { } a)
        {
            return null;
        }

        if (a <= 0)
        {
            issues.Add(new DataIssue(pageId, $"restrictions.{field}", a.ToString(System.Globalization.CultureInfo.InvariantCulture), "not a positive number"));
            return null;
        }

        return a;
    }

    private static CoasterDetail? Coaster(AttractionRow r, Vocabularies vocab, IReadOnlyDictionary<string, int> resolved,
        List<DataIssue> issues)
    {
        if (r.Type != "coaster")
        {
            return null;
        }

        // A Page list: titles, not links, in layout order with repeats kept.
        var elements = r.Elements
            .Select(Wikitext.NormaliseTitle)
            .OfType<string>()
            .Select(title => new Ref(resolved.TryGetValue(title, out var id) ? id : null, title))
            .ToList();
        var detail = new CoasterDetail
        {
            Propulsion = FieldMapper.Choice(r.PageId, "coaster.propulsion", r.Type, "class", r.Propulsion, vocab, issues),
            TrackMaterial = FieldMapper.Choice(r.PageId, "coaster.track_material", r.Type, "track_material", r.TrackMaterial, vocab, issues),
            SupportMaterial = FieldMapper.Choice(r.PageId, "coaster.support_material", r.Type, "support_material", r.SupportMaterial, vocab, issues),
            Elements = elements.Count == 0 ? null : elements,
        };
        return detail.IsEmpty ? null : detail;
    }

    private static RideDetail? Ride(AttractionRow r, List<DataIssue> issues)
    {
        if (r.Type != "ride")
        {
            return null;
        }

        var extra = new[]
            {
                (r.Coord2Lat, r.Coord2Lon), (r.Coord3Lat, r.Coord3Lon), (r.Coord4Lat, r.Coord4Lon),
                (r.Coord5Lat, r.Coord5Lon), (r.Coord6Lat, r.Coord6Lon),
            }
            .Select(c => FieldMapper.Coordinates(r.PageId, c.Item1, c.Item2, issues))
            .OfType<Coordinates>()
            .ToList();
        var detail = new RideDetail
        {
            Indoor = r.Indoor,
            Enclosed = r.Enclosed,
            DarkRide = r.DarkRide,
            Water = r.Water,
            // Text by A4's decision ("36 horses, 2 chariots" says more than a number): published only when plain.
            Cabins = int.TryParse(r.Cabins?.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var cabins) && cabins > 0 ? cabins : null,
            Units = Count(r.PageId, "ride.units", r.NumberOfRides, issues),
            ExtraCoordinates = extra.Count == 0 ? null : extra,
        };
        return detail.IsEmpty ? null : detail;
    }

    private static WaterSlideDetail? WaterSlide(AttractionRow r, Vocabularies vocab, IReadOnlyDictionary<string, int> resolved,
        List<DataIssue> issues)
    {
        if (r.Type != "water_slide")
        {
            return null;
        }

        // One row per Product member (2,444 on 2026-10-12), in slot order; a slot names one product.
        var components = r.Components
            .OrderBy(c => c.Ordering)
            .Select(c => (Ref: Wikitext.Refs(c.Product).FirstOrDefault(), Variant: Text(c.Variant)))
            .Where(c => c.Ref.Ref != null)
            .Select(c => new AttractionProduct(
                c.Ref.Target != null && resolved.TryGetValue(c.Ref.Target, out var id) ? c.Ref.Ref with { Id = id } : c.Ref.Ref,
                c.Variant))
            .ToList();
        var detail = new WaterSlideDetail
        {
            Classes = FieldMapper.Choices(r.PageId, "water_slide.classes", r.Type, "class1", r.SlideClasses, vocab, issues),
            Environment = FieldMapper.Choice(r.PageId, "water_slide.environment", r.Type, "environment", r.Environment, vocab, issues),
            Containment = FieldMapper.Choice(r.PageId, "water_slide.containment", r.Type, "containment", r.Containment, vocab, issues),
            Exit = FieldMapper.Choice(r.PageId, "water_slide.exit", r.Type, "exit", r.ExitType, vocab, issues),
            Lanes = Count(r.PageId, "water_slide.lanes", r.NumberOfSlides, issues),
            Components = components.Count == 0 ? null : components,
        };
        return detail.IsEmpty ? null : detail;
    }

    /// <summary>Identical units at the location: an Integer since the rebuild (A4), so only a non-positive one is wrong.</summary>
    private static int? Count(int pageId, string field, int? n, List<DataIssue> issues)
    {
        if (n is not { } v)
        {
            return null;
        }

        if (v <= 0)
        {
            issues.Add(new DataIssue(pageId, field, v.ToString(System.Globalization.CultureInfo.InvariantCulture), "not a positive number"));
            return null;
        }

        return v;
    }

    /// <summary>Past stints, oldest first: the page numbers them newest first (Ordering 1 is the latest before now).</summary>
    private static AttractionHistory? History(AttractionRow r, Vocabularies vocab, IReadOnlyDictionary<string, int> resolved,
        List<DataIssue> issues)
    {
        var locations = new List<PastLocation>();
        foreach (var p in r.PastLocations.OrderByDescending(p => p.Ordering))
        {
            if (p.Ordering is not { } index)
            {
                issues.Add(new DataIssue(r.PageId, "history.locations", "", "a past location with no Ordering"));
                continue;
            }

            var kind = Kind(r.PageId, $"history.locations[{index}].kind", p.LocationKind, issues);
            var where = LocationMapper.Project(kind, p.OnLoan, p.Park, p.Owner, p.Location, r.Type, vocab, resolved);
            locations.Add(new PastLocation
            {
                Index = index,
                Kind = kind,
                OnLoan = p.OnLoan,
                Park = where.Park,
                Owner = where.Owner,
                Place = where.Place,
                State = State(r.PageId, $"history.locations[{index}].state", r.Type, p.State, vocab, issues),
                Country = FieldMapper.Country(r.PageId, r.Type, p.Country, vocab, issues),
                Coordinates = FieldMapper.Coordinates(r.PageId, p.Lat, p.Lon, issues),
                Opened = FieldMapper.Edtf(r.PageId, $"history.locations[{index}].opened", p.OpenedEdtf, p.OpenedEarliest, p.OpenedLatest, issues),
                Closed = FieldMapper.Edtf(r.PageId, $"history.locations[{index}].closed", p.ClosedEdtf, p.ClosedEarliest, p.ClosedLatest, issues),
            });
        }

        return locations.Count == 0 ? null : new AttractionHistory(locations);
    }

    /// <summary>Plain text, or null when blank.</summary>
    private static string? Text(string? wikitext)
    {
        var text = Wikitext.Strip(wikitext ?? "");
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// Every link in the Product list, in page order, each target once; a
    /// member with no link is one unlinked Ref, as the infobox stores it.
    /// </summary>
    private static List<(string? Target, Ref Ref)> ProductRefs(string? product)
    {
        var refs = new List<(string? Target, Ref Ref)>();
        var seen = new HashSet<string>();
        foreach (var x in FieldMapper.Split(product).SelectMany(Wikitext.Refs))
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
        var variants = FieldMapper.Split(row.ProductVariant).Select(Wikitext.Strip).Where(v => v.Length > 0).ToList();
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
