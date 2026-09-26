namespace CoasterpediaServices.ImageFetch.Clients.Wikimapia;

public record WikimapiaPlaceResponse(
    string? Title,
    List<WikimapiaPhoto>? Photos,
    WikimapiaLocation? Location,
    WikimapiaDebug? Debug
);

// Wikimapia reports errors (bad key, rate limit, missing object) as HTTP 200 with only a "debug" block.
public record WikimapiaDebug(
    int Code,
    string? Message
);

public record WikimapiaPhoto(
    long Id,
    long ObjectId,
    string? UserName,
    long Time,
    string? FullUrl,
    string? BigUrl,
    string? ThumbnailUrl
);

public record WikimapiaLocation(
    double Lat,
    double Lon
);
