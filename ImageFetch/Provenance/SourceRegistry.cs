namespace CoasterpediaServices.ImageFetch.Provenance;

/// <summary>
/// Per-source presentation: the Cargo <c>Source</c> label, the source-attribution template, and
/// whether that template already carries its own licence card. <c>{{Geograph}}</c> and
/// <c>{{Wikimapia}}</c> bake in CC-BY-SA, so a fetched image from them needs no separate licence
/// card; <c>{{Flickr}}</c> and <c>{{Wikimedia Commons}}</c> do.
/// </summary>
public sealed record SourceInfo(string Label, string CardTemplate, bool SelfLicensing);

public static class SourceRegistry
{
    public const string Flickr = "flickr";
    public const string Geograph = "geograph";
    public const string Wikimapia = "wikimapia";
    public const string Commons = "commons";

    private static readonly Dictionary<string, SourceInfo> ByKey = new()
    {
        [Flickr]    = new SourceInfo("Flickr", "Flickr", SelfLicensing: false),
        [Geograph]  = new SourceInfo("Geograph", "Geograph", SelfLicensing: true),
        [Wikimapia] = new SourceInfo("Wikimapia", "Wikimapia", SelfLicensing: true),
        [Commons]   = new SourceInfo("Wikimedia Commons", "Wikimedia Commons", SelfLicensing: false),
    };

    public static SourceInfo Get(string key) => ByKey[key];
}
