using System.Text.Json;
using System.Text.Json.Nodes;
using CoasterpediaServices.Projector.Data;
using CoasterpediaServices.Projector.Mapping;
using CoasterpediaServices.Projector.Model;
using CoasterpediaServices.Projector.Options;
using CoasterpediaServices.Projector.Vocabulary;
using Xunit;

namespace CoasterpediaServices.Projector.Tests;

public class ProjectorTests
{
    // TemplateData as the wiki served it on 2026-10-11.
    private static readonly JsonNode TemplateData =
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "templatedata.json")))!;

    private static readonly Vocabularies Vocab = VocabularySource.Parse(TemplateData, new ProjectorConfig().AttractionTemplates);

    private static readonly Dictionary<string, int> Resolved = new()
    {
        ["Arrow Dynamics"] = 1829,
        ["Bolliger & Mabillard"] = 543,
        ["SkyRide Technology"] = 9001,
        ["Zierer"] = 77,
        ["Boomerang (Vekoma product)"] = 3213,
        ["SkyBOX (ProSlide product)"] = 58664,
        ["TWISTER (ProSlide product)"] = 58307,
        ["Wild mouse"] = 4100,
        ["Carnival Horizon"] = 67009,
    };

    private static readonly HashSet<int> ProductIds = [3213, 58664, 58307];

    private static AttractionRow Row(string type = "coaster", int id = 171, string page = "Gold Rusher (Six Flags Magic Mountain)",
        string? name = "Gold Rusher", string? status = "operating", string? country = "USA",
        string? manufacturer = "[[Arrow Dynamics|Arrow Development]]", double? lat = 34.4234906, double? lon = -118.598881,
        string? product = null, string? variant = null, string? kind = "park", string? park = "[[Six Flags Magic Mountain]]") =>
        new()
        {
            Type = type, PageId = id, PageName = page, Revision = 123456, Name = name, Status = status, Country = country,
            Manufacturer = manufacturer, Product = product, ProductVariant = variant, Lat = lat, Lon = lon,
            LocationKind = kind, OnLoan = kind == null ? null : false, Park = park,
        };

    private static AttractionContent Map(AttractionRow row, List<DataIssue>? issues = null) =>
        AttractionMapper.Map(row, Vocab, Resolved, ProductIds, "https://coasterpedia.net", issues ?? []);

    [Fact]
    public void Vocabularies_come_from_each_types_templates()
    {
        foreach (var type in new[] { "coaster", "ride", "water_slide" })
        {
            Assert.Equal("standing but not operating", Vocab.Match(type, "status", "Standing But Not Operating"));
            Assert.Null(Vocab.Match(type, "status", "closed"));
            Assert.Equal("ship", Vocab.Match(type, "country", "ship"));
        }
    }

    [Fact]
    public void A_template_missing_from_TemplateData_stops_projection()
    {
        var templates = new Dictionary<string, string[]> { ["coaster"] = ["Template:No such infobox"] };
        Assert.Throws<InvalidOperationException>(() => VocabularySource.Parse(TemplateData, templates));
    }

    [Fact]
    public void Every_country_the_wiki_allows_has_an_ISO_code_except_ship()
    {
        var allowed = Vocab.All["ride"]["country"];
        var missing = allowed.Where(c => c != "ship" && Countries.IsoCode(c) == null).ToList();
        Assert.Empty(missing);
        Assert.Equal(allowed.Count - 1, Countries.Count);
    }

    [Fact]
    public void The_published_vocabulary_keeps_the_infobox_order_with_slugs_and_labels()
    {
        var doc = VocabularyDocument.Build(Vocab, new ProjectorConfig().AttractionTemplates);

        // A field only products offer (slide_material) is left out when no product is listed.
        Assert.Equal(["type", "status", "restraints", "propulsion", "track_material", "support_material", "classes", "environment",
            "containment", "exit"], doc.Select(f => f.Key));
        Assert.Equal(["coaster", "ride", "water_slide"], doc["type"]!.AsArray().Select(t => t!["value"]!.GetValue<string>()));
        Assert.Equal("Roller coaster", doc["type"]![0]!["label"]!.GetValue<string>());

        var status = doc["status"]!.AsArray();
        Assert.Equal(
            ["operating", "defunct", "under_construction", "under_refurbishment", "standing_but_not_operating", "in_storage", "planned",
                "cancelled", "unknown"],
            status.Select(s => s!["value"]!.GetValue<string>()));
        Assert.Equal("Standing but not operating", status[4]!["label"]!.GetValue<string>());
        Assert.Equal(["coaster", "ride", "water_slide"], status[0]!["applies_to"]!.AsArray().Select(t => t!.GetValue<string>()));
    }

    [Fact]
    public void A_value_offered_by_one_infobox_applies_to_that_type_only()
    {
        var ordered = new Dictionary<string, Dictionary<string, List<string>>>
        {
            ["coaster"] = new() { ["status"] = ["operating"] },
            ["ride"] = new() { ["status"] = ["Operating", "in production"] },
        };
        var types = new Dictionary<string, string[]> { ["coaster"] = [], ["ride"] = [] };
        var status = VocabularyDocument.Build(new Vocabularies(ordered), types)["status"]!.AsArray();

        Assert.Equal(["operating", "in_production"], status.Select(s => s!["value"]!.GetValue<string>()));
        Assert.Equal(["coaster", "ride"], status[0]!["applies_to"]!.AsArray().Select(t => t!.GetValue<string>()));
        Assert.Equal(["ride"], status[1]!["applies_to"]!.AsArray().Select(t => t!.GetValue<string>()));
    }

    [Fact]
    public void The_vocabulary_hash_moves_only_when_the_lists_do()
    {
        var types = new ProjectorConfig().AttractionTemplates;
        var again = VocabularySource.Parse(TemplateData, types);
        Assert.Equal(VocabularyDocument.Hash(VocabularyDocument.Build(Vocab, types)),
            VocabularyDocument.Hash(VocabularyDocument.Build(again, types)));

        var grown = TemplateData.DeepClone();
        foreach (var (_, page) in grown["pages"]!.AsObject())
        {
            page!["params"]!["status"]!["suggestedvalues"]!.AsArray().Add("relocated");
        }

        Assert.NotEqual(VocabularyDocument.Hash(VocabularyDocument.Build(Vocab, types)),
            VocabularyDocument.Hash(VocabularyDocument.Build(VocabularySource.Parse(grown, types), types)));
    }

    [Fact]
    public void A_piped_link_is_named_by_its_display_text_and_resolved_by_its_target()
    {
        var c = Map(Row());
        var m = Assert.Single(c.Manufacturers);
        Assert.Equal(new Ref(1829, "Arrow Development"), m);
    }

    [Fact]
    public void Every_link_becomes_a_ref_and_the_annotations_go()
    {
        var c = Map(Row(manufacturer: "[[Zierer]] <br> [[BHS]] ([[Zierer|again]])"));
        Assert.Equal([new Ref(77, "Zierer"), new Ref(null, "BHS")], c.Manufacturers);
    }

    [Fact]
    public void Unlinked_text_is_a_ref_with_no_id_and_a_placeholder_is_nothing()
    {
        Assert.Equal([new Ref(null, "Self-built")], Map(Row(manufacturer: "Self-built")).Manufacturers);
        Assert.Empty(Map(Row(manufacturer: "Unknown")).Manufacturers);
        Assert.Empty(Map(Row(manufacturer: "")).Manufacturers);
        Assert.Empty(Map(Row(manufacturer: null)).Manufacturers);
    }

    [Fact]
    public void One_product_takes_the_variant_and_every_member_is_a_product()
    {
        var issues = new List<DataIssue>();
        var one = Map(Row(product: "[[Boomerang (Vekoma product)|Boomerang]]", variant: "Standard"), issues);
        Assert.Equal([new AttractionProduct(new Ref(3213, "Boomerang"), "Standard")], one.Products);

        // A complex: one member per link, as the infobox stores it.
        var two = Map(Row(type: "water_slide", product: "[[SkyBOX (ProSlide product)|SkyBOX]]#[[TWISTER (ProSlide product)|TWISTER]]"), issues);
        Assert.Equal([new AttractionProduct(new Ref(58664, "SkyBOX"), null), new AttractionProduct(new Ref(58307, "TWISTER"), null)],
            two.Products);
        Assert.Empty(issues);
    }

    [Fact]
    public void A_variant_beside_two_products_pairs_with_neither_and_is_reported()
    {
        // Fighter Jet (Glorious Orient Jining), 2026-10-12.
        var issues = new List<DataIssue>();
        var c = Map(Row(product: "[[Boomerang (Vekoma product)|Boomerang]]#[[TWISTER (ProSlide product)|TWISTER]]",
            variant: "Top Gun Launch Coaster"), issues);
        Assert.All(c.Products, p => Assert.Null(p.Variant));
        Assert.Equal("products.variant", Assert.Single(issues).Field);

        issues.Clear();
        Assert.Empty(Map(Row(variant: "Standard"), issues).Products);
        Assert.Equal("a variant with no product", Assert.Single(issues).Problem);
    }

    [Fact]
    public void A_product_link_to_another_kind_of_page_keeps_its_id_and_is_reported_and_a_red_link_is_not()
    {
        var issues = new List<DataIssue>();
        var c = Map(Row(product: "[[Wild mouse]]#[[No Such Product]]#Play Structure Type A02"), issues);
        Assert.Equal([new Ref(4100, "Wild mouse"), new Ref(null, "No Such Product"), new Ref(null, "Play Structure Type A02")],
            c.Products.Select(p => p.Product));
        // The red link is no issue: an article not written yet.
        Assert.Equal(new DataIssue(171, "products", "Wild mouse", "links a page that is not a product"), Assert.Single(issues));
    }

    [Fact]
    public void A_product_named_twice_is_one_product()
    {
        var c = Map(Row(product: "[[Boomerang (Vekoma product)|Boomerang]]#[[Boomerang (Vekoma product)|again]]"));
        Assert.Equal(new Ref(3213, "Boomerang"), Assert.Single(c.Products).Product);
    }

    // DR-006 §3's rows, by the kind the infobox stored: which column names the park, the owner and the place.
    [Theory]
    [InlineData("park", false, "[[Six Flags Magic Mountain]]", "Arrow Dynamics", "Valencia", "Six Flags Magic Mountain", null, "Valencia")]
    [InlineData("park", true, "[[Six Flags Magic Mountain]]", "[[Zierer]]", "Valencia", "Six Flags Magic Mountain", "Zierer", "Valencia")]
    [InlineData("travelling", false, "Travelling", "Billy Irvin", "", null, "Billy Irvin", null)]
    // A linked company beside a travel marker is the showman, not a park.
    [InlineData("travelling", false, "[[Zierer]]", "", "Travelling UK", null, "Zierer", null)]
    [InlineData("travelling", false, "Malcolm Dixon", "", "", null, "Malcolm Dixon", null)]
    [InlineData("storage", false, "[[Six Flags Magic Mountain]]", "", "In storage", "Six Flags Magic Mountain", null, null)]
    [InlineData("storage", false, "Billy Irvin", "", "In storage, Middle East", null, "Billy Irvin", "Middle East")]
    [InlineData("standalone", false, "Standalone", "Blackpool Council", "Blackpool", null, "Blackpool Council", "Blackpool")]
    // Placeholders name nobody: "Owner unknown", a bare country.
    [InlineData("travelling", false, "Owner unknown", "USA", "", null, null, null)]
    [InlineData("unknown", false, "", "", "", null, null, null)]
    public void Each_kind_names_its_park_owner_and_place_from_the_columns_classify_read(string kind, bool onLoan, string park,
        string owner, string location, string? expectPark, string? expectOwner, string? expectPlace)
    {
        var where = LocationMapper.Project(kind, onLoan, park, owner, location, "coaster", Vocab, Resolved);
        Assert.Equal(expectPark, where.Park?.Name);
        Assert.Equal(expectOwner, where.Owner?.Name);
        Assert.Equal(expectPlace, where.Place);
    }

    [Fact]
    public void A_park_link_resolves_to_its_page()
    {
        var c = Map(Row(park: "[[Carnival Horizon|the ship]]"));
        Assert.Equal(new Ref(67009, "the ship"), c.Location.Park);
        Assert.Equal("park", c.Location.Kind);
    }

    [Fact]
    public void Past_stints_list_oldest_first_keeping_the_pages_numbers()
    {
        var row = Row() with
        {
            PastLocations =
            [
                new PastLocationRow { PageId = 171, Ordering = 1, LocationKind = "travelling", Park = "Billy Irvin" },
                new PastLocationRow { PageId = 171, Ordering = 2, LocationKind = "park", Park = "[[Carnival Horizon]]" },
            ],
        };
        var history = Map(row).History!;
        Assert.Equal([2, 1], history.Locations.Select(l => l.Index));
        Assert.Equal(67009, history.Locations[0].Park!.Id);
        Assert.Equal("Billy Irvin", history.Locations[1].Owner!.Name);
    }

    [Fact]
    public void A_blank_kind_is_reported_and_names_nothing()
    {
        var issues = new List<DataIssue>();
        var c = Map(Row(kind: null), issues);
        Assert.Null(c.Location.Kind);
        Assert.Null(c.Location.Park);
        Assert.Equal(new DataIssue(171, "location.kind", "", "missing"), Assert.Single(issues));
    }

    [Fact]
    public void Elements_keep_layout_order_and_repeats()
    {
        var c = Map(Row() with { Elements = ["Vertical loop", "Vertical loop", "corkscrew"] });
        Assert.Equal(["Vertical loop", "Vertical loop", "Corkscrew"], c.Coaster!.Elements!.Select(e => e.Name));
    }

    [Fact]
    public void Rider_heights_convert_by_the_pages_units_and_text_counts_stay_text()
    {
        var imperial = Map(Row(type: "ride") with { Stats = new StatsRow { Units = "imperial" }, MinHeight = 48, Cabins = "36 horses, 2 chariots" });
        Assert.Equal(new Measurement(121.9, "cm", new Entered(48, "in")), imperial.Restrictions!.MinHeight);
        // A4 kept Cabins text: a note is not a count, and not an issue.
        Assert.Null(imperial.Ride);
        Assert.Equal(new Measurement(137, "cm", new Entered(137, "cm")), Map(Row(type: "ride") with { MinHeight = 137 }).Restrictions!.MinHeight);
    }

    [Fact]
    public void Link_targets_normalise_like_MediaWiki_titles()
    {
        Assert.Equal("Arrow Dynamics", Wikitext.NormaliseTitle(" arrow_Dynamics#History "));
        Assert.Equal("Éclair", Wikitext.NormaliseTitle("éclair"));
        Assert.Null(Wikitext.NormaliseTitle("#Section"));
    }

    [Fact]
    public void Names_lose_their_markup()
    {
        // 11 attractions on 2026-10-11, e.g. 51387 "Unknown (Taukbashçe)".
        Assert.Equal("unknown", Map(Row(name: "''unknown''")).Name);
        Assert.Equal("Gold Rusher (Six Flags Magic Mountain)", Map(Row(name: " ")).Name);
    }

    [Fact]
    public void Status_is_a_slug_of_the_wikis_wording()
    {
        Assert.Equal("standing_but_not_operating", Map(Row(status: "standing but not operating")).Status);
        Assert.Equal("operating", Map(Row(status: "Operating")).Status);
    }

    [Fact]
    public void A_status_outside_the_allowed_list_is_null_and_reported()
    {
        var issues = new List<DataIssue>();
        Assert.Null(Map(Row(status: "closed"), issues).Status);
        Assert.Equal(new DataIssue(171, "status", "closed", "not in the infobox's allowed list"), Assert.Single(issues));

        issues.Clear();
        Assert.Null(Map(Row(type: "ride", status: ""), issues).Status);
        Assert.Equal("missing", Assert.Single(issues).Problem);
    }

    [Fact]
    public void Country_maps_to_ISO_with_the_sites_name()
    {
        Assert.Equal(new Country("GB", "UK"), Map(Row(country: "UK")).Location.Country);
        Assert.Equal(new Country("XK", "Kosovo"), Map(Row(country: "Kosovo")).Location.Country);
        Assert.Equal(new Country("US", "USA"), Map(Row(country: "usa")).Location.Country);
    }

    [Fact]
    public void A_ship_has_no_country_and_is_not_an_issue()
    {
        var issues = new List<DataIssue>();
        var c = Map(Row(type: "ride", country: "ship", lat: null, lon: null), issues);
        Assert.Null(c.Location.Country);
        Assert.Null(c.Location.Coordinates);
        Assert.Empty(issues);
    }

    [Fact]
    public void Null_island_is_not_a_place()
    {
        var issues = new List<DataIssue>();
        Assert.Null(Map(Row(lat: 0, lon: 0), issues).Location.Coordinates);
        Assert.Single(issues);
    }

    [Fact]
    public void Urls_are_encoded_as_MediaWiki_encodes_them()
    {
        Assert.Equal("https://coasterpedia.net/wiki/Water_Chute_(Great_Yarmouth_Pleasure_Beach;_opened_1972)",
            Wikitext.ArticleUrl("https://coasterpedia.net", "Water Chute (Great Yarmouth Pleasure Beach; opened 1972)"));
        Assert.Equal("https://coasterpedia.net/wiki/Bolliger_%26_Mabillard",
            Wikitext.ArticleUrl("https://coasterpedia.net/", "Bolliger & Mabillard"));
        Assert.Equal("https://coasterpedia.net/wiki/Dr._Doom%27s_Fear_Fall",
            Wikitext.ArticleUrl("https://coasterpedia.net", "Dr. Doom's Fear Fall"));
    }

    [Fact]
    public void The_hash_follows_content_not_revision()
    {
        var a = Map(Row());
        Assert.Equal(Wire.Hash(a), Wire.Hash(Map(Row() with { Revision = 999999 })));
        Assert.NotEqual(Wire.Hash(a), Wire.Hash(Map(Row(status: "defunct"))));
        Assert.NotEqual(Wire.Hash(a), Wire.Hash(Map(Row(page: "Gold Rusher (moved)"))));
    }

    [Fact]
    public void Sort_keys_are_case_and_accent_blind()
    {
        Assert.Equal("el condor", Wikitext.SortKey("Él Cóndor"));
    }

    [Fact]
    public void The_upsert_carries_record_summary_and_the_edges_columns()
    {
        var at = DateTimeOffset.Parse("2026-10-11T12:00:00Z");
        var item = Wire.Upsert(Map(Row(type: "ride", id: 66486, page: "SkyRide (Carnival Horizon)", name: "SkyRide",
            country: "ship", manufacturer: "[[SkyRide Technology]]", lat: null, lon: null, park: "[[Carnival Horizon]]")), 42, at);

        var json = JsonSerializer.Serialize(item);
        Assert.Equal(
            """{"kind":"attraction","id":66486,"revision":42,"updated_at":"2026-10-11T12:00:00Z","settled":true,"settles_at":null,"sort_name":"skyride","sort_values":{"height":null,"speed":null,"length":null},"filters":{"type":"ride","status":"operating","country":null,"park":67009,"location_kind":"park","state":null,"opened_earliest":null,"opened_latest":null},"summary":{"id":66486,"type":"ride","name":"SkyRide","status":"operating","park":{"id":67009,"name":"Carnival Horizon"},"location_kind":"park","manufacturers":[{"id":9001,"name":"SkyRide Technology"}],"products":[],"updated_at":"2026-10-11T12:00:00Z"},"record":{"id":66486,"type":"ride","name":"SkyRide","title":"SkyRide (Carnival Horizon)","url":"https://coasterpedia.net/wiki/SkyRide_(Carnival_Horizon)","status":"operating","location":{"kind":"park","on_loan":false,"park":{"id":67009,"name":"Carnival Horizon"}},"manufacturers":[{"id":9001,"name":"SkyRide Technology"}],"products":[],"revision":42,"updated_at":"2026-10-11T12:00:00Z"}}""",
            json);
    }
}
