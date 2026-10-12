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

public sealed record AttractionLocation(Country? Country, Coordinates? Coordinates);

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
/// Everything in an attraction record that comes from the wiki's content.
/// The hash is taken over this alone, so a prose edit (new revision, same
/// content) never counts as a change (DR-010 §6.3).
/// </summary>
public sealed record AttractionContent
{
    public required int Id { get; init; }
    public required string Type { get; init; }
    public required string Name { get; init; }
    public required string Title { get; init; }
    public required string Url { get; init; }
    public string? Status { get; init; }
    public required AttractionLocation Location { get; init; }
    public required IReadOnlyList<Ref> Manufacturers { get; init; }
    public AttractionStats? Stats { get; init; }
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
}

/// <summary>
/// Everything in a manufacturer record that comes from the wiki's content;
/// hashed like AttractionContent. Predecessor and successor wait for their own
/// decision: the columns hold lists and notes, not one link (DR-010 §12.8).
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
/// like AttractionContent. Installations wait for the attractions' product
/// field (rebuild A3); designer and the open type lists are held back (DR-010 §12.10).
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
    public DateValue? Introduced { get; init; }
    public DateValue? Discontinued { get; init; }
    public ProductOffered? Offered { get; init; }
    public IReadOnlyList<ProductVariant>? Variants { get; init; }
}

/// <summary>A data problem found while projecting. Reported, never built around.</summary>
public sealed record DataIssue(int PageId, string Field, string Value, string Problem);
