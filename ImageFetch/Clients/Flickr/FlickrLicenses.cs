namespace CoasterpediaServices.ImageFetch.Clients.Flickr;

/// <summary>
/// Flickr licence name -&gt; our canonical licence slug (see
/// <see cref="Provenance.LicenseCatalog"/>), ported from Coasterpedia's mw.FlickrChecker.js
/// licenseMaps override, which accepts every Flickr licence except "All Rights Reserved" (unlike
/// stock UploadWizard, which also rejects NC/ND licences). A null value means the import must be
/// rejected. The slug is turned into the Cargo short + notice card by the catalogue, so this map
/// only has to establish licence *identity*, not wikitext.
/// </summary>
public static class FlickrLicenses
{
    private static readonly Dictionary<string, string?> SlugsByName = new()
    {
        ["All Rights Reserved"] = null,

        ["CC BY 2.0"] = "cc-by-2.0",
        ["CC BY-ND 2.0"] = "cc-by-nd-2.0",
        ["CC BY-NC-ND 2.0"] = "cc-by-nc-nd-2.0",
        ["CC BY-NC 2.0"] = "cc-by-nc-2.0",
        ["CC BY-NC-SA 2.0"] = "cc-by-nc-sa-2.0",
        ["CC BY-SA 2.0"] = "cc-by-sa-2.0",

        ["No known copyright restrictions"] = "flickr-nkcr",
        ["United States Government Work"] = "pd-usgov",
        ["Public Domain Dedication (CC0)"] = "cc0",
        ["Public Domain Mark"] = "pd-us",

        ["CC BY 4.0"] = "cc-by-4.0",
        ["CC BY-ND 4.0"] = "cc-by-nd-4.0",
        ["CC BY-NC-ND 4.0"] = "cc-by-nc-nd-4.0",
        ["CC BY-NC 4.0"] = "cc-by-nc-4.0",
        ["CC BY-NC-SA 4.0"] = "cc-by-nc-sa-4.0",
        ["CC BY-SA 4.0"] = "cc-by-sa-4.0",

        // Old Flickr license names from 2011, preserved just in case (matches mw.FlickrChecker.js).
        ["Attribution License"] = "cc-by-2.0",
        ["Attribution-NoDerivs License"] = "cc-by-nd-2.0",
        ["Attribution-NonCommercial-NoDerivs License"] = "cc-by-nc-nd-2.0",
        ["Attribution-NonCommercial License"] = "cc-by-nc-2.0",
        ["Attribution-NonCommercial-ShareAlike License"] = "cc-by-nc-sa-2.0",
        ["Attribution-ShareAlike License"] = "cc-by-sa-2.0",
    };

    /// <summary>The canonical slug for a Flickr licence name, or null when it can't be imported.</summary>
    public static string? ToSlug(string licenseName) =>
        SlugsByName.GetValueOrDefault(licenseName);
}
