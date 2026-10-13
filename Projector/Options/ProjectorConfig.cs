namespace CoasterpediaServices.Projector.Options;

public record ProjectorConfig
{
    /// <summary>Off by default: nothing is read or pushed until the edge exists.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// A read-only MariaDB user with SELECT on the Cargo database and on
    /// page_id / page_latest / page_is_redirect of the wiki's page table.
    /// One connection reads both, so a sweep is one consistent snapshot.
    /// </summary>
    public string ConnectionString { get; init; } = "";

    public string CargoDatabase { get; init; } = "cargo_db";
    public string WikiDatabase { get; init; } = "coasterpedia";
    public string WikiTablePrefix { get; init; } = "mw_";

    /// <summary>Public base for record URLs; the wiki's internal address is CoasterpediaConfig.BaseUrl.</summary>
    public string PublicBaseUrl { get; init; } = "https://coasterpedia.net";

    /// <summary>POST target for ingest batches, e.g. https://api.coasterpedia.net/internal/ingest.</summary>
    public string IngestUrl { get; init; } = "";

    /// <summary>Cloudflare Access service token for /internal/*.</summary>
    public string AccessClientId { get; init; } = "";
    public string AccessClientSecret { get; init; } = "";

    /// <summary>Local development only: matches the Worker's DEV_INGEST_TOKEN in .dev.vars.</summary>
    public string DevIngestToken { get; init; } = "";

    /// <summary>Items per ingest request. The edge takes at most 300 (three D1 queries each).</summary>
    public int BatchSize { get; init; } = 200;

    /// <summary>Wait after an edit before projecting, so a burst of edits exports once.</summary>
    public TimeSpan Debounce { get; init; } = TimeSpan.FromSeconds(60);

    public string SweepCron { get; init; } = "17 3 * * *";

    /// <summary>How long an untrusted change stands unchanged before it settles (DR-010 §6.3). Published as settle_after.</summary>
    public TimeSpan SettleAfter { get; init; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Groups whose changes settle at once: the ones whose edits already skip
    /// the patrol queue. Add patroller once it has autopatrol.
    /// </summary>
    public string[] TrustedGroups { get; init; } = ["sysop", "bot", "autopatrolled"];

    /// <summary>
    /// Attraction type → the infoboxes that store it. TemplateData of these
    /// supplies the allowed values. Multitrack stores to Roller_Coasters.
    /// </summary>
    public Dictionary<string, string[]> AttractionTemplates { get; init; } = new()
    {
        ["coaster"] = ["Template:Infobox roller coaster", "Template:Infobox coaster multitrack"],
        ["ride"] = ["Template:Infobox ride"],
        ["water_slide"] = ["Template:Infobox water slide"],
    };

    /// <summary>The infobox that stores Amusement_Parks; its TemplateData supplies the parks' allowed values.</summary>
    public string[] ParkTemplates { get; init; } = ["Template:Infobox amusement park"];

    /// <summary>The infobox that stores Manufacturers; its TemplateData supplies the manufacturers' allowed values.</summary>
    public string[] ManufacturerTemplates { get; init; } = ["Template:Infobox manufacturer"];

    /// <summary>The infobox that stores Park_Companies; its TemplateData supplies the park companies' allowed values.</summary>
    public string[] ParkCompanyTemplates { get; init; } = ["Template:Infobox park company"];

    /// <summary>
    /// The three product infoboxes, read as one list per param: every product
    /// is a "product" whatever its type, and they share status and restraints.
    /// </summary>
    public string[] ProductTemplates { get; init; } =
        ["Template:Infobox coaster product", "Template:Infobox ride product", "Template:Infobox water slide product"];

    /// <summary>
    /// Every list of allowed values the Projector validates against: each
    /// attraction type's, then the parks' under "park", the manufacturers'
    /// under "manufacturer", the products' under "product" and the park
    /// companies' under "park_company".
    /// </summary>
    public Dictionary<string, string[]> VocabularyTemplates() =>
        new(AttractionTemplates)
        {
            ["park"] = ParkTemplates, ["manufacturer"] = ManufacturerTemplates, ["product"] = ProductTemplates,
            ["park_company"] = ParkCompanyTemplates,
        };
}
