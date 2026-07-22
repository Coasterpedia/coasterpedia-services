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
    FlickrLocation? Location,
    // Degrees CLOCKWISE the original bytes must be turned to read the right way up -
    // Flickr's own rotate button, stored beside the photo rather than written into the
    // file. Flickr bakes it into every derivative size but leaves the original alone,
    // and writes no EXIF Orientation tag, so a downloaded original arrives on its side
    // with nothing in the file to say so. See FlickrRotation.
    [property: JsonConverter(typeof(FlickrLooseIntConverter))] int Rotation = 0);

public record FlickrLocation(string Latitude, string Longitude);

public record FlickrOwner(string Nsid, string Username, string? Realname);

public record FlickrContent([property: JsonPropertyName("_content")] string Content);

public record FlickrDates(string? Taken);

public record FlickrUrls(List<FlickrUrl> Url);

public record FlickrUrl(string Type, [property: JsonPropertyName("_content")] string Content);

public record FlickrPhotoSizesEnvelope(string Stat, string? Message, FlickrSizes? Sizes);

public record FlickrSizes(List<FlickrSize> Size);

// Width/Height are as DISPLAYED: every size except "Original" has the rotation flag
// already applied, which is what makes a derivative's aspect a usable cross-check on
// whether the original still needs turning.
public record FlickrSize(
    string Label,
    string Source,
    [property: JsonConverter(typeof(FlickrLooseIntConverter))] int Width = 0,
    [property: JsonConverter(typeof(FlickrLooseIntConverter))] int Height = 0);

public record FlickrLicensesEnvelope(string Stat, string? Message, FlickrLicenseList? Licenses);

public record FlickrLicenseList(List<FlickrLicenseEntry> License);

public record FlickrLicenseEntry(int Id, string Name);

public record FlickrPhotosetPhotosEnvelope(string Stat, string? Message, FlickrPhotoset? Photoset);

public record FlickrPhotoset(string Owner, List<FlickrAlbumPhoto> Photo);

public record FlickrAlbumPhoto(string Id, string Title, [property: JsonPropertyName("url_s")] string? UrlS, string License);
