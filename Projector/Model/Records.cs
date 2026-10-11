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
public sealed record AttractionStats
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
    public bool IsEmpty => this == new AttractionStats();
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

/// <summary>A data problem found while projecting. Reported, never built around.</summary>
public sealed record DataIssue(int PageId, string Field, string Value, string Problem);
