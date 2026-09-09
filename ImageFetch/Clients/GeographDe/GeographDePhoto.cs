namespace CoasterpediaServices.ImageFetch.Clients.GeographDe;

/// <summary>
/// One photo from the Geograph Deutschland API, already flattened out of its XML. Deliberately
/// not <see cref="Geograph.GeographResponse"/>: the German site is a fork of the same codebase,
/// and its API answers the same questions in a different shape — a full <c>img src</c> instead of
/// <c>imgserver</c> + <c>image</c>, <c>location</c> attributes instead of <c>wgs84_lat</c>, and a
/// <c>status</c> element instead of an <c>error</c> field.
/// </summary>
public sealed record GeographDePhoto
{
    public string? Title { get; init; }
    public string? Author { get; init; }
    public string? ImageUrl { get; init; }
    public string? Taken { get; init; }
    public string? Latitude { get; init; }
    public string? Longitude { get; init; }

    /// <summary>The API's own failure message, or null on success.</summary>
    public string? Error { get; init; }
}
