using System.Text.Json;
using System.Text.Json.Nodes;
using CoasterpediaServices.Projector.Data;
using CoasterpediaServices.Projector.Mapping;
using CoasterpediaServices.Projector.Model;
using CoasterpediaServices.Projector.Options;
using CoasterpediaServices.Projector.Vocabulary;
using Xunit;

namespace CoasterpediaServices.Projector.Tests;

public class ManufacturerTests
{
    // TemplateData as the wiki served it; the manufacturer infobox on 2026-10-12.
    private static readonly Vocabularies Vocab = VocabularySource.Parse(
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "templatedata.json")))!,
        new ProjectorConfig().VocabularyTemplates());

    // Bolliger & Mabillard as stored on 2026-10-12.
    private static ManufacturerRow Row(int id = 543, string page = "Bolliger & Mabillard", string? name = "Bolliger & Mabillard",
        string? status = "active", string? country = "Switzerland", string? headquarters = "Monthey, Valais, Switzerland",
        string? website = "https://bolliger-mabillard.com/", string? founded = "1988-01-01", int? precision = 3) =>
        new()
        {
            PageId = id, PageName = page, Revision = 123456, Name = name, Status = status, Country = country,
            Headquarters = headquarters, Website = website, Founded = founded, FoundedPrecision = precision,
        };

    private static ManufacturerContent Map(ManufacturerRow row, List<DataIssue>? issues = null) =>
        ManufacturerMapper.Map(row, Vocab, "https://coasterpedia.net", issues ?? []);

    [Fact]
    public void The_manufacturer_infobox_has_its_own_allowed_values()
    {
        Assert.Equal("active", Vocab.Match("manufacturer", "status", "Active"));
        // A company is active or defunct; it is never operating.
        Assert.Null(Vocab.Match("manufacturer", "status", "operating"));
        Assert.Equal("Liechtenstein", Vocab.Match("manufacturer", "country", "liechtenstein"));
    }

    [Fact]
    public void Every_country_a_manufacturer_may_name_has_an_ISO_code_except_ship()
    {
        Assert.DoesNotContain(Vocab.All["manufacturer"]["country"], c => c != "ship" && Countries.IsoCode(c) == null);
    }

    [Fact]
    public void A_clean_row_maps_with_no_issues()
    {
        var issues = new List<DataIssue>();
        var c = Map(Row(), issues);

        Assert.Empty(issues);
        Assert.Equal("Bolliger & Mabillard", c.Name);
        Assert.Equal("https://coasterpedia.net/wiki/Bolliger_%26_Mabillard", c.Url);
        Assert.Equal("active", c.Status);
        Assert.Equal(new DateValue("1988", "1988-01-01", "1988-12-31"), c.Founded);
        Assert.Equal("Monthey, Valais, Switzerland", c.Headquarters);
        Assert.Equal(new Country("CH", "Switzerland"), c.Country);
        Assert.Equal("https://bolliger-mabillard.com/", c.Website);
    }

    [Theory]
    // ProSlide, Adesko, Mack Rides.
    [InlineData("1986-12-19", 1, "1986-12-19", "1986-12-19", "1986-12-19")]
    [InlineData("1991-10-01", 2, "1991-10", "1991-10-01", "1991-10-31")]
    [InlineData("1780-01-01", 3, "1780", "1780-01-01", "1780-12-31")]
    public void Founded_takes_its_EDTF_from_Cargos_precision(string stored, int precision, string edtf, string earliest, string latest)
    {
        Assert.Equal(new DateValue(edtf, earliest, latest), Map(Row(founded: stored, precision: precision)).Founded);
    }

    [Fact]
    public void A_status_is_matched_case_blind_and_published_as_the_slug()
    {
        // ProSlide stores "Active"; 98 others store "defunct".
        Assert.Equal("active", Map(Row(status: "Active")).Status);
        Assert.Equal("defunct", Map(Row(status: "Defunct")).Status);
    }

    [Fact]
    public void A_status_from_another_infobox_is_null_and_reported()
    {
        var issues = new List<DataIssue>();
        Assert.Null(Map(Row(status: "operating"), issues).Status);
        Assert.Equal(new DataIssue(543, "status", "operating", "not in the infobox's allowed list"), Assert.Single(issues));
    }

    [Fact]
    public void Website_follows_the_park_rule()
    {
        // The Gravity Group: the external link the infobox asks for.
        Assert.Equal("http://www.thegravitygroup.com/",
            Map(Row(website: "[http://www.thegravitygroup.com/ www.thegravitygroup.com]")).Website);

        // Reverse Bungy NZ: no scheme, and a note.
        var issues = new List<DataIssue>();
        Assert.Null(Map(Row(website: "www.reversebungy.com (dead)"), issues).Website);
        Assert.Equal("website", Assert.Single(issues).Field);
    }

    [Fact]
    public void Blank_fields_are_absent_and_not_issues()
    {
        // Vekoma: no country or headquarters; Arrow Dynamics: no founding date or website.
        var issues = new List<DataIssue>();
        var c = Map(Row(country: "", headquarters: "", website: "", founded: null, precision: null), issues);

        Assert.Empty(issues);
        Assert.Null(c.Country);
        Assert.Null(c.Headquarters);
        Assert.Null(c.Website);
        Assert.Null(c.Founded);
    }

    [Fact]
    public void The_title_stands_in_for_a_blank_name_but_a_name_that_differs_is_kept()
    {
        Assert.Equal("Bolliger & Mabillard", Map(Row(name: "")).Name);
        // ProSlide's page names it by its short name.
        var proslide = Map(Row(55205, "ProSlide", "ProSlide Technology"));
        Assert.Equal("ProSlide Technology", proslide.Name);
        Assert.Equal("ProSlide", proslide.Title);
    }

    [Fact]
    public void The_manufacturer_upsert_lists_the_record_and_filters_by_status_and_country()
    {
        var at = DateTimeOffset.Parse("2026-10-12T12:00:00Z");
        var item = Wire.UpsertManufacturer(Map(Row()), 42, at);

        Assert.Equal(
            """{"kind":"manufacturer","id":543,"revision":42,"updated_at":"2026-10-12T12:00:00Z","settled":true,"settles_at":null,"sort_name":"bolliger \u0026 mabillard","filters":{"status":"active","country":"CH"},"summary":{"id":543,"name":"Bolliger \u0026 Mabillard","title":"Bolliger \u0026 Mabillard","url":"https://coasterpedia.net/wiki/Bolliger_%26_Mabillard","status":"active","founded":{"edtf":"1988","earliest":"1988-01-01","latest":"1988-12-31"},"headquarters":"Monthey, Valais, Switzerland","country":{"code":"CH","name":"Switzerland"},"website":"https://bolliger-mabillard.com/","revision":42,"updated_at":"2026-10-12T12:00:00Z"},"record":{"id":543,"name":"Bolliger \u0026 Mabillard","title":"Bolliger \u0026 Mabillard","url":"https://coasterpedia.net/wiki/Bolliger_%26_Mabillard","status":"active","founded":{"edtf":"1988","earliest":"1988-01-01","latest":"1988-12-31"},"headquarters":"Monthey, Valais, Switzerland","country":{"code":"CH","name":"Switzerland"},"website":"https://bolliger-mabillard.com/","revision":42,"updated_at":"2026-10-12T12:00:00Z"}}""",
            JsonSerializer.Serialize(item));
    }

    [Fact]
    public void Status_values_manufacturers_offer_apply_to_manufacturer()
    {
        var doc = VocabularyDocument.Build(Vocab, new ProjectorConfig().AttractionTemplates, [Wire.Park, Wire.Manufacturer]);

        // manufacturer is in applies_to, never a type.
        Assert.Equal(["coaster", "ride", "water_slide"], doc["type"]!.AsArray().Select(t => t!["value"]!.GetValue<string>()));
        var status = doc["status"]!.AsArray().ToDictionary(s => s!["value"]!.GetValue<string>(),
            s => s!["applies_to"]!.AsArray().Select(t => t!.GetValue<string>()).ToList());
        Assert.Equal(["manufacturer"], status["active"]);
        Assert.Contains("manufacturer", status["defunct"]);
        Assert.DoesNotContain("manufacturer", status["operating"]);
    }
}
