namespace CoasterpediaServices.ImageFetch.Clients.Geograph;

public record GeographResponse(
    string? Title,
    string? GridReference,
    string? ProfileLink,
    string? Realname,
    string? Imgserver,
    string? Thumbnail,
    string? Image,
    string[]? Sizeinfo,
    string? Taken,
    long? Submitted,
    string? Category,
    string? Comment,
    string? Wgs84Lat,
    string? Wgs84Long,
    string? Error
);
