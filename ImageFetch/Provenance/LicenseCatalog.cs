namespace CoasterpediaServices.ImageFetch.Provenance;

/// <summary>The Cargo <c>Licence</c> short string + the Coasterpedia licence-notice card for one slug.</summary>
public sealed record LicenseInfo(string Short, string Card);

/// <summary>
/// The single source of truth for licence identity on the URL-import path: a canonical CC/PD
/// slug -&gt; its Cargo <c>Licence</c> short string + the Coasterpedia licence-notice template.
/// Cards use the canonical <c>{{CC-BY|type=2.0}}</c> form, NOT the legacy UploadWizard
/// <c>{{cc-by-2.0}}</c> wrappers. Every fetcher normalises its source's licence to one of these
/// slugs (Flickr via <see cref="Clients.Flickr.FlickrLicenses"/>; Commons/Geograph/Wikimapia
/// already produce slugs); <see cref="ProvenanceBuilder"/> turns the slug into the short + card.
/// </summary>
public static class LicenseCatalog
{
    private static readonly Dictionary<string, LicenseInfo> BySlug = Build();

    /// <summary>The catalogue entry for a slug, or null when the licence isn't one we import.</summary>
    public static LicenseInfo? Get(string? slug) =>
        slug != null && BySlug.TryGetValue(slug, out var info) ? info : null;

    private static Dictionary<string, LicenseInfo> Build()
    {
        var map = new Dictionary<string, LicenseInfo>(StringComparer.OrdinalIgnoreCase);

        // Creative Commons families, versioned. `short` is the licensetpl_short the matching
        // Coasterpedia template renders; the version drives the template's `type` param.
        (string Slug, string Short, string Template)[] ccFamilies =
        [
            ("cc-by",       "CC-BY",       "CC-BY"),
            ("cc-by-sa",    "CC-BY-SA",    "CC-BY-SA"),
            ("cc-by-nd",    "CC-BY-ND",    "CC-BY-ND"),
            ("cc-by-nc",    "CC-BY-NC",    "CC-BY-NC"),
            ("cc-by-nc-sa", "CC-BY-NC-SA", "CC-BY-NC-SA"),
            ("cc-by-nc-nd", "CC-BY-NC-ND", "CC-BY-NC-ND"),
        ];
        foreach (var (slug, shortName, template) in ccFamilies)
        {
            foreach (var version in new[] { "2.0", "3.0", "4.0" })
            {
                map[$"{slug}-{version}"] = new LicenseInfo(shortName, $"{{{{{template}|type={version}}}}}");
            }
        }

        // Public domain / zero. These render a fixed card with no version param. `cc-zero` and
        // bare `pd` are the alternate slugs Wikimedia Commons' ext-metadata reports.
        map["cc0"]         = new LicenseInfo("CC0", "{{CC0}}");
        map["cc-zero"]     = map["cc0"];
        map["pd"]          = new LicenseInfo("PD", "{{PD}}");
        map["pd-us"]       = new LicenseInfo("PD", "{{PD-US}}");
        map["pd-usgov"]    = map["pd"];
        map["flickr-nkcr"] = new LicenseInfo("PD", "{{Flickr-no known copyright restrictions}}");

        return map;
    }
}
