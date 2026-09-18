namespace CoasterpediaServices.ImageFetch.Provenance;

/// <summary>The Cargo <c>Licence</c> value (the canonical slug) + the Coasterpedia licence-notice
/// card for one licence.</summary>
public sealed record LicenseInfo(string Slug, string Card);

/// <summary>
/// The single source of truth for licence identity on the URL-import path: a CC/PD slug -&gt; its
/// canonical slug (stored verbatim in the Cargo <c>Licence</c> field) + the Coasterpedia
/// licence-notice template. Slugs are the lowercase Commons ext-metadata form (e.g.
/// <c>cc-by-sa-3.0-de</c>), so a consumer can recover the exact licence - and its
/// Category:UploadWizard templates wrapper, by capitalising the first letter - from the field.
/// Cards use the canonical <c>{{CC-BY|type=2.0}}</c> form, NOT those wrappers. Every fetcher
/// normalises its source's licence to one of these slugs (Flickr via
/// <see cref="Clients.Flickr.FlickrLicenses"/>; Commons/Geograph/Wikimapia already produce slugs);
/// <see cref="ProvenanceBuilder"/> turns the slug into the stored value + card.
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

        void Add(string slug, string card) => map[slug] = new LicenseInfo(slug, card);

        // Creative Commons families, versioned; the version drives the template's `type` param.
        (string Slug, string Template)[] ccFamilies =
        [
            ("cc-by",       "CC-BY"),
            ("cc-by-sa",    "CC-BY-SA"),
            ("cc-by-nd",    "CC-BY-ND"),
            ("cc-by-nc",    "CC-BY-NC"),
            ("cc-by-nc-sa", "CC-BY-NC-SA"),
            ("cc-by-nc-nd", "CC-BY-NC-ND"),
        ];
        foreach (var (slug, template) in ccFamilies)
        {
            foreach (var version in new[] { "1.0", "2.0", "3.0", "4.0" })
            {
                Add($"{slug}-{version}", $"{{{{{template}|type={version}}}}}");
            }
        }

        // Commons-only licences (Flickr never offered these, and Commons only hosts the free
        // BY / BY-SA families): 2.5, and the German ports, which the templates render via
        // countrycode/countryname.
        foreach (var (slug, template) in new[] { ("cc-by", "CC-BY"), ("cc-by-sa", "CC-BY-SA") })
        {
            Add($"{slug}-2.5", $"{{{{{template}|type=2.5}}}}");
            foreach (var version in new[] { "2.0", "2.5", "3.0" })
            {
                Add($"{slug}-{version}-de", $"{{{{{template}|type={version}|countrycode=de|countryname=Germany}}}}");
            }
        }

        // Public domain / zero. These render a fixed card with no version param. `pd-usgov` keeps
        // its own stored value (it's a distinct basis) but shares the generic PD card. `cc-zero`
        // is Commons' alternate slug for CC0 - the same licence - so it aliases the canonical
        // entry and is stored as `cc0`.
        Add("cc0",         "{{CC0}}");
        Add("pd",          "{{PD}}");
        Add("pd-us",       "{{PD-US}}");
        Add("pd-usgov",    "{{PD}}");
        Add("flickr-nkcr", "{{Flickr-no known copyright restrictions}}");
        map["cc-zero"] = map["cc0"];

        return map;
    }
}
