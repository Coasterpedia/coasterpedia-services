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
    };

    private static AttractionRow Row(string type = "coaster", int id = 171, string page = "Gold Rusher (Six Flags Magic Mountain)",
        string? name = "Gold Rusher", string? status = "operating", string? country = "USA",
        string? manufacturer = "[[Arrow Dynamics|Arrow Development]]", double? lat = 34.4234906, double? lon = -118.598881) =>
        new(type, id, page, 123456, name, status, country, manufacturer, lat, lon);

    private static AttractionContent Map(AttractionRow row, List<DataIssue>? issues = null) =>
        AttractionMapper.Map(row, Vocab, Resolved, "https://coasterpedia.net", issues ?? []);

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

        Assert.Equal(["type", "status"], doc.Select(f => f.Key));
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
            country: "ship", manufacturer: "[[SkyRide Technology]]", lat: null, lon: null)), 42, at);

        var json = JsonSerializer.Serialize(item);
        Assert.Equal(
            """{"kind":"attraction","id":66486,"revision":42,"updated_at":"2026-10-11T12:00:00Z","settled":true,"settles_at":null,"sort_name":"skyride","sort_values":{"height":null,"speed":null,"length":null},"filters":{"type":"ride","status":"operating","country":null},"summary":{"id":66486,"type":"ride","name":"SkyRide","status":"operating","manufacturers":[{"id":9001,"name":"SkyRide Technology"}],"updated_at":"2026-10-11T12:00:00Z"},"record":{"id":66486,"type":"ride","name":"SkyRide","title":"SkyRide (Carnival Horizon)","url":"https://coasterpedia.net/wiki/SkyRide_(Carnival_Horizon)","status":"operating","location":{},"manufacturers":[{"id":9001,"name":"SkyRide Technology"}],"revision":42,"updated_at":"2026-10-11T12:00:00Z"}}""",
            json);
    }
}
