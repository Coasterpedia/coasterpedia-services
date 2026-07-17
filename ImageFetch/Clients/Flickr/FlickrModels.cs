using System.Text.Json.Serialization;

namespace CoasterpediaServices.ImageFetch.Clients.Flickr;

public record FlickrPhotoInfoEnvelope(string Stat, string? Message, FlickrPhoto? Photo);

public record FlickrPhoto(
    string License,
    FlickrOwner Owner,
    FlickrContent? Title,
    FlickrContent? Description,
    FlickrDates Dates,
    FlickrUrls Urls,
    FlickrLocation? Location);

public record FlickrLocation(string Latitude, string Longitude);

public record FlickrOwner(string Nsid, string Username, string? Realname);

public record FlickrContent([property: JsonPropertyName("_content")] string Content);

public record FlickrDates(string? Taken);

public record FlickrUrls(List<FlickrUrl> Url);

public record FlickrUrl(string Type, [property: JsonPropertyName("_content")] string Content);

public record FlickrPhotoSizesEnvelope(string Stat, string? Message, FlickrSizes? Sizes);

public record FlickrSizes(List<FlickrSize> Size);

public record FlickrSize(string Label, string Source);

public record FlickrLicensesEnvelope(string Stat, string? Message, FlickrLicenseList? Licenses);

public record FlickrLicenseList(List<FlickrLicenseEntry> License);

public record FlickrLicenseEntry(int Id, string Name);

public record FlickrPhotosetPhotosEnvelope(string Stat, string? Message, FlickrPhotoset? Photoset);

public record FlickrPhotoset(List<FlickrAlbumPhoto> Photo);

public record FlickrAlbumPhoto(string Id, string Title, [property: JsonPropertyName("url_s")] string? UrlS, string License);
