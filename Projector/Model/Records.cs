using System.Text.Json.Serialization;

namespace CoasterpediaServices.Projector.Model;

// The public v1 shapes (api/openapi.yaml), only as far as the current slice
// publishes them. A field the slice does not publish is absent from the type,
// not null: absent and null mean the same thing to a client ("unknown"), and
// adding a property later is not a breaking change.

/// <summary>A pointer to another page. Id is null for unlinked text or a red link.</summary>
public sealed record Ref(int? Id, string Name);

public sealed record Country(string Code, string Name);

public sealed record Coordinates(double Lat, double Lon);

/// <summary>Where in the park (openapi Location.area).</summary>
public sealed record Area(string Section, string? Subsection);

/// <summary>
/// Where an attraction is (openapi Location). Kind and OnLoan are
/// Travelling.classify's stored answer (DR-006); Park and Owner are the
/// columns that answer names, by kind (LocationMapper).
/// </summary>
public sealed record AttractionLocation
{
    public string? Kind { get; init; }
    public bool? OnLoan { get; init; }
    public Ref? Park { get; init; }
    public Ref? Owner { get; init; }
    public string? Place { get; init; }
    public string? State { get; init; }
    public Country? Country { get; init; }
    public Coordinates? Coordinates { get; init; }
    public Area? Area { get; init; }
}

/// <summary>One past stint (openapi AttractionHistory.locations): a location, numbered as the page numbers it, with its dates.</summary>
public sealed record PastLocation
{
    public required int Index { get; init; }
    public string? Kind { get; init; }
    public bool? OnLoan { get; init; }
    public Ref? Park { get; init; }
    public Ref? Owner { get; init; }
    public string? Place { get; init; }
    public string? State { get; init; }
    public Country? Country { get; init; }
    public Coordinates? Coordinates { get; init; }
    public DateValue? Opened { get; init; }
    public DateValue? Closed { get; init; }
}

/// <summary>
/// Past stints and the names carried, each oldest first; either list is absent
/// when empty. Names appear only when one differs from the current name.
/// </summary>
public sealed record AttractionHistory(IReadOnlyList<PastLocation>? Locations, IReadOnlyList<DatedName>? Names = null);

/// <summary>
/// One name the attraction carried (openapi AttractionHistory.names). Location
/// index is the past stint's index (history.locations.index), or null at the
/// current location. Dates are at Cargo's precision, null when not recorded.
/// </summary>
public sealed record DatedName(
    string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateValue? Start,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateValue? End,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? LocationIndex);

public sealed record AttractionDates(DateValue? Opened, DateValue? Closed, DateValue? Opening, DateValue? Closing)
{
    [JsonIgnore]
    public bool IsEmpty => Opened == null && Closed == null && Opening == null && Closing == null;
}

/// <summary>Rider requirements, as recorded (openapi Restrictions). Heights in cm, weights in kg.</summary>
public sealed record Restrictions
{
    public Measurement? MinHeight { get; init; }
    public Measurement? MinHeightDriver { get; init; }
    public Measurement? MinHeightStanding { get; init; }
    public Measurement? MinHeightUpsideDown { get; init; }
    public Measurement? MinHeightUnaccompanied { get; init; }
    public Measurement? MaxHeightWithoutChild { get; init; }
    public Measurement? MaxHeight { get; init; }
    public int? MinAge { get; init; }
    public int? MinAgeDriver { get; init; }
    public int? MinAgeUnaccompanied { get; init; }
    public int? MaxAgeWithoutChild { get; init; }
    public int? MaxAge { get; init; }
    public bool? ChildrenOnly { get; init; }
    public bool? AdultOnlyWithChild { get; init; }
    public Measurement? MinWeight { get; init; }
    public Measurement? MaxWeight { get; init; }
    public Measurement? MaxWeightPerTube { get; init; }

    [JsonIgnore]
    public bool IsEmpty => this == new Restrictions();
}

/// <summary>What only a coaster records (openapi CoasterDetail). Train manufacturer waits on the one-or-many question.</summary>
public sealed record CoasterDetail
{
    public string? Propulsion { get; init; }
    public string? TrackMaterial { get; init; }
    public string? SupportMaterial { get; init; }
    public IReadOnlyList<Ref>? TrainManufacturers { get; init; }
    public IReadOnlyList<Ref>? Elements { get; init; }

    [JsonIgnore]
    public bool IsEmpty => Propulsion == null && TrackMaterial == null && SupportMaterial == null && TrainManufacturers == null
                           && Elements == null;
}

/// <summary>What only a ride records (openapi RideDetail). The flags are stored only when set, so false is rare and absent is usual.</summary>
public sealed record RideDetail
{
    public bool? Indoor { get; init; }
    public bool? Enclosed { get; init; }
    public bool? DarkRide { get; init; }
    public int? Cabins { get; init; }
    public int? Units { get; init; }
    public IReadOnlyList<Coordinates>? ExtraCoordinates { get; init; }

    [JsonIgnore]
    public bool IsEmpty => Indoor == null && Enclosed == null && DarkRide == null && Cabins == null
        && Units == null && ExtraCoordinates == null;
}

/// <summary>What only a water slide records (openapi WaterSlideDetail).</summary>
public sealed record WaterSlideDetail
{
    public IReadOnlyList<string>? Classes { get; init; }
    public string? Environment { get; init; }
    public string? Containment { get; init; }
    public string? Exit { get; init; }
    public int? Lanes { get; init; }
    public IReadOnlyList<AttractionProduct>? Components { get; init; }

    [JsonIgnore]
    public bool IsEmpty => Classes == null && Environment == null && Containment == null && Exit == null && Lanes == null
        && Components == null;
}

/// <summary>The figure as the editor typed it, in the unit the page uses.</summary>
public sealed record Entered(double Value, string Unit);

/// <summary>Metric, rounded to the precision of what was entered, plus what was entered.</summary>
public sealed record Measurement(double Value, string Unit, Entered? Entered = null);

public sealed record Inversions(int? Track, int? Rider);

public sealed record Footprint(Measurement? Length, Measurement? Width);

/// <summary>Only the fields the attraction's type records, and only when known.</summary>
public record AttractionStats
{
    public Measurement? Height { get; init; }
    public Measurement? Drop { get; init; }
    public Measurement? ElevationDifference { get; init; }
    public Measurement? Length { get; init; }
    public Measurement? Speed { get; init; }
    public Measurement? Diameter { get; init; }
    public Inversions? Inversions { get; init; }
    public double? MaxAngle { get; init; }
    public double? BankAngle { get; init; }
    public double? GForce { get; init; }
    public Measurement? Duration { get; init; }
    public int? RidersPerHour { get; init; }
    public int? RidersPerTrain { get; init; }
    public int? Tracks { get; init; }
    public Footprint? Footprint { get; init; }

    [JsonIgnore]
    public virtual bool IsEmpty => this == new AttractionStats();
}

/// <summary>A variant's specs (openapi ProductVariant.stats): the attraction stats, plus what only a variant records.</summary>
public sealed record VariantStats : AttractionStats
{
    public VariantStats()
    {
    }

    public VariantStats(AttractionStats stats) : base(stats)
    {
    }

    // Ordered after the attraction stats; the serialiser would otherwise put a derived type's own first.

    /// <summary>Degrees.</summary>
    [JsonPropertyOrder(1)] public double? Sweep { get; init; }
    [JsonPropertyOrder(1)] public int? Cabins { get; init; }
    [JsonPropertyOrder(1)] public int? NumberOfSlides { get; init; }
    [JsonPropertyOrder(1)] public Measurement? MaxWeight { get; init; }
    [JsonPropertyOrder(1)] public Measurement? MaxWeightPerTube { get; init; }

    [JsonIgnore]
    public override bool IsEmpty => this == new VariantStats();
}

/// <summary>
/// One product an attraction was built as (openapi Attraction.products).
/// Variant is written even when null: none recorded, or one the page doesn't
/// pair with a single product.
/// </summary>
public sealed record AttractionProduct(
    Ref Product,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Variant);

/// <summary>
/// Everything in an attraction record that comes from the wiki's content.
/// The hash is taken over this alone, so a prose edit (new revision, same
/// content) never counts as a change (DR-010 §6.3).
/// </summary>
public sealed record AttractionContent
{
    public required int Id { get; init; }
    public required string Type { get; init; }
    public required string Name { get; init; }

    /// <summary>The name is descriptive, written because the real one isn't known ({{Placeholder title}}). Always written.</summary>
    public required bool NameIsPlaceholder { get; init; }

    public required string Title { get; init; }
    public required string Url { get; init; }
    public string? Status { get; init; }
    public AttractionDates? Dates { get; init; }
    public required AttractionLocation Location { get; init; }
    public required IReadOnlyList<Ref> Manufacturers { get; init; }
    public required IReadOnlyList<AttractionProduct> Products { get; init; }

    /// <summary>Who designed it, by name only: people and firms mixed, and people have no records yet (DR-010 §12.18).</summary>
    public IReadOnlyList<Ref>? Designers { get; init; }

    public IReadOnlyList<Ref>? Builders { get; init; }

    /// <summary>The site's type tags as written ("Kiddie", "Log Flume"), from Category1–6.</summary>
    public IReadOnlyList<string>? Categories { get; init; }

    public IReadOnlyList<string>? Restraints { get; init; }

    /// <summary>What it replaced, and what replaced it: usually attractions, sometimes plain text.</summary>
    public IReadOnlyList<Ref>? Replaced { get; init; }

    public IReadOnlyList<Ref>? ReplacedBy { get; init; }

    /// <summary>YouTube video IDs, in the infobox's order (Video, Video2, Video3).</summary>
    public IReadOnlyList<string>? Videos { get; init; }

    public AttractionStats? Stats { get; init; }
    public Restrictions? Restrictions { get; init; }
    public CoasterDetail? Coaster { get; init; }
    public RideDetail? Ride { get; init; }
    public WaterSlideDetail? WaterSlide { get; init; }
    public AttractionHistory? History { get; init; }
}

/// <summary>
/// A date as the wiki knows it (openapi.yaml DateValue): EDTF, with the first
/// and last day it can be. A bound is null at an open end ("[..1989]" has no
/// earliest), so both are always written, never left out as unknown.
/// </summary>
public sealed record DateValue(
    string Edtf,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Earliest,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Latest);

public sealed record ParkDates(DateValue? Opened, DateValue? Closed, DateValue? Opening)
{
    [JsonIgnore]
    public bool IsEmpty => Opened == null && Closed == null && Opening == null;
}

/// <summary>
/// The resort a park belongs to. Id is the page ID of the resort's wrapper
/// template, which defines the resort and stores its members, so every resort
/// has one; most have no article of their own.
/// </summary>
public sealed record Resort(int Id, string Name);

/// <summary>
/// A park's place in its resort: the resort, and where the wrapper lists the
/// park. Ordering never enters the record; it rides on the wire as
/// filters.resort_order, so the edge can list a resort's parks in the
/// wrapper's order (DR-010 §12.20).
/// </summary>
public sealed record ResortMembership(Resort Resort, int? Ordering);

/// <summary>Everything in a park record that comes from the wiki's content; hashed like AttractionContent.</summary>
public sealed record ParkContent
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public required string Title { get; init; }
    public required string Url { get; init; }
    public string? Status { get; init; }
    public ParkDates? Dates { get; init; }
    public string? Place { get; init; }
    public string? State { get; init; }
    public Country? Country { get; init; }
    public Coordinates? Coordinates { get; init; }
    public Resort? Resort { get; init; }
    public string? Website { get; init; }

    /// <summary>The companies in the spell in force (history's current entries); absent at a park that has closed.</summary>
    public IReadOnlyList<Ref>? Owners { get; init; }

    public IReadOnlyList<Ref>? Operators { get; init; }

    /// <summary>Past names and owner and operator spells from History; null when the page records none of them.</summary>
    public ParkHistory? History { get; init; }
}

/// <summary>A park's names and owner and operator spells, each oldest first, as openapi.yaml Park.history.</summary>
public sealed record ParkHistory(IReadOnlyList<ParkName>? Names, IReadOnlyList<DatedRef>? Owners, IReadOnlyList<DatedRef>? Operators);

/// <summary>One name the park carried (openapi DatedNames). Dates are at Cargo's precision, null when not recorded.</summary>
public sealed record ParkName(
    string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateValue? Start,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateValue? End);

/// <summary>
/// One company in one spell (openapi.yaml DatedRefs). A spell naming two
/// companies is two of these with the same dates. Current: the spell in force
/// (Position 1, no end) at a park that hasn't closed or been cancelled.
/// </summary>
public sealed record DatedRef(
    Ref Company,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateValue? Start,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateValue? End,
    bool Current);

/// <summary>
/// Everything in a park company record that comes from the wiki's content;
/// hashed like AttractionContent.
/// </summary>
public sealed record ParkCompanyContent
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public required string Title { get; init; }
    public required string Url { get; init; }
    public string? Status { get; init; }
    public DateValue? Established { get; init; }
    public DateValue? Closed { get; init; }
    public string? Headquarters { get; init; }
    public Country? Country { get; init; }
    public string? Website { get; init; }

    public IReadOnlyList<Ref>? Predecessors { get; init; }

    public IReadOnlyList<Ref>? Successors { get; init; }
}

/// <summary>
/// Everything in a manufacturer record that comes from the wiki's content;
/// hashed like AttractionContent.
/// </summary>
public sealed record ManufacturerContent
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public required string Title { get; init; }
    public required string Url { get; init; }
    public string? Status { get; init; }
    public DateValue? Founded { get; init; }
    public string? Headquarters { get; init; }
    public Country? Country { get; init; }
    public string? Website { get; init; }

    /// <summary>Companies before and after it: a merger has two predecessors (DR-010 §12.18).</summary>
    public IReadOnlyList<Ref>? Predecessors { get; init; }

    public IReadOnlyList<Ref>? Successors { get; init; }
}

/// <summary>
/// What a product is offered as (openapi Product.offered): capability, never
/// what one installation realised. Only the closed sets, as slugs; each list
/// is absent when the page offers nothing in it.
/// </summary>
public sealed record ProductOffered
{
    public IReadOnlyList<string>? TrackMaterial { get; init; }
    public IReadOnlyList<string>? SupportMaterial { get; init; }
    public IReadOnlyList<string>? Restraints { get; init; }
    public IReadOnlyList<string>? Classes { get; init; }
    public IReadOnlyList<string>? Containment { get; init; }
    public IReadOnlyList<string>? SlideMaterial { get; init; }
    public IReadOnlyList<int>? RidersPerTube { get; init; }

    /// <summary>Open lists, as written: the site's type tags, and coasters' lift and launch methods.</summary>
    public IReadOnlyList<string>? Types { get; init; }

    public IReadOnlyList<string>? LiftLaunch { get; init; }

    [JsonIgnore]
    public bool IsEmpty => this == new ProductOffered();
}

/// <summary>
/// One catalogued configuration. Name is null on the product's own one (the
/// specs in its infobox, which the wiki leaves unnamed), and written as null
/// rather than left out: there it means "the product itself", not "unknown".
/// </summary>
public sealed record ProductVariant(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Name)
{
    public string? Group { get; init; }
    public DateValue? Introduced { get; init; }
    public DateValue? Discontinued { get; init; }
    public VariantStats? Stats { get; init; }
}

/// <summary>
/// Everything in a product record that comes from the wiki's content; hashed
/// like AttractionContent. Installations are counted at the edge from the
/// attractions' products (DR-010 §12.13), so an attraction edit moves them at
/// once.
/// </summary>
public sealed record ProductContent
{
    public required int Id { get; init; }
    public required string Type { get; init; }
    public required string Name { get; init; }
    public required string Title { get; init; }
    public required string Url { get; init; }
    public string? Status { get; init; }
    public required IReadOnlyList<Ref> Manufacturers { get; init; }

    /// <summary>By name only, as on attractions.</summary>
    public IReadOnlyList<Ref>? Designers { get; init; }

    public DateValue? Introduced { get; init; }
    public DateValue? Discontinued { get; init; }
    public ProductOffered? Offered { get; init; }
    public IReadOnlyList<ProductVariant>? Variants { get; init; }
}

/// <summary>A data problem found while projecting. Reported, never built around.</summary>
public sealed record DataIssue(int PageId, string Field, string Value, string Problem);
