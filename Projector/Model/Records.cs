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
}

/// <summary>A data problem found while projecting. Reported, never built around.</summary>
public sealed record DataIssue(int PageId, string Field, string Value, string Problem);
