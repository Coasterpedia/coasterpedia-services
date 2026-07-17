namespace CoasterpediaServices.ImageFetch.Fetchers;

public record FetchResult
{
    public required byte[] Bytes { get; init; }
    public required string ContentType { get; init; }
    public required string SuggestedFileName { get; init; }
    public required string Title { get; init; }
    public string? Author { get; init; }
    public required string SourceUrl { get; init; }

    /// <summary>The Cargo <c>Source</c> label (e.g. "Flickr", "Wikimedia Commons").</summary>
    public required string Source { get; init; }

    /// <summary>The Cargo <c>Licence</c> short string (e.g. "CC-BY-SA", "CC0", "PD").</summary>
    public required string License { get; init; }

    /// <summary>The file-page notice cards, in order — source-attribution card, licence card, any
    /// extras. The gadget writes these verbatim; nothing is re-derived client-side.</summary>
    public required IReadOnlyList<string> Cards { get; init; }

    public string? Date { get; init; }
    public string? Latitude { get; init; }
    public string? Longitude { get; init; }

    /// <summary>Set when a mirror hop was chased (e.g. a Commons file mirrored from Flickr) to the origin page.</summary>
    public string? OriginUrl { get; init; }
}
