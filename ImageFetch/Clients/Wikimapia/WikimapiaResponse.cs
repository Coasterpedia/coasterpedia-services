namespace CoasterpediaServices.ImageFetch.Clients.Wikimapia;

public record WikimapiaPlaceResponse(
    string? Title,
    List<WikimapiaPhoto>? Photos,
    WikimapiaLocation? Location
);

public record WikimapiaPhoto(
    long Id,
    long ObjectId,
    string? UserName,
    long Time,
    string FullUrl,
    string? ThumbnailUrl
);

public record WikimapiaLocation(
    double Lat,
    double Lon
);
