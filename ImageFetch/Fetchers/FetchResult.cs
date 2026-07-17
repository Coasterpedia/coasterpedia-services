namespace CoasterpediaServices.ImageFetch.Fetchers;

public record FetchResult
{
    public required byte[] Bytes { get; init; }
    public required string ContentType { get; init; }
    public required string SuggestedFileName { get; init; }
    public required string Title { get; init; }
    public string? Author { get; init; }
    public required string SourceUrl { get; init; }
    public required string License { get; init; }
    public string? AdditionalLicenseWikitext { get; init; }
    public string? Date { get; init; }
    public string? Latitude { get; init; }
    public string? Longitude { get; init; }

    /// <summary>Set when a mirror hop was chased (e.g. a Commons file mirrored from Flickr) to the origin page.</summary>
    public string? OriginUrl { get; init; }
}
