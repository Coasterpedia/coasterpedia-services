using System.Text.Json.Nodes;
using CoasterpediaServices.Projector.Data;
using CoasterpediaServices.Projector.Mapping;
using CoasterpediaServices.Projector.Model;
using CoasterpediaServices.Projector.Options;
using CoasterpediaServices.Projector.Vocabulary;
using Xunit;

namespace CoasterpediaServices.Projector.Tests;

/// <summary>Park companies (DR-010 §12.16), and the owner and operator history on parks that links them.</summary>
public class ParkCompanyTests
{
    // TemplateData as the wiki served it; the park company infobox on 2026-10-13.
    private static readonly Vocabularies Vocab = VocabularySource.Parse(
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "templatedata.json")))!,
        new ProjectorConfig().VocabularyTemplates());

    // Six Flags as stored on 2026-10-13.
    private static ParkCompanyRow Row(string? status = "active", string? website = "http://sixflags.com",
        string? headquarters = "Charlotte, North Carolina", string? established = "1959-01-01", int? precision = 3) =>
        new()
        {
            PageId = 821, PageName = "Six Flags", Revision = 123456, Name = "Six Flags", Status = status, Country = "USA",
            Headquarters = headquarters, Website = website, Established = established, EstablishedPrecision = precision,
        };

    private static ParkCompanyContent Map(ParkCompanyRow row, List<DataIssue>? issues = null) =>
        ParkCompanyMapper.Map(row, Vocab, "https://coasterpedia.net", issues ?? []);

    [Fact]
    public void The_park_company_infobox_has_its_own_allowed_values()
    {
        Assert.Equal("active", Vocab.Match("park_company", "status", "Active"));
        Assert.Equal("unknown", Vocab.Match("park_company", "status", "unknown"));
        Assert.Null(Vocab.Match("park_company", "status", "operating"));
        Assert.DoesNotContain(Vocab.All["park_company"]["country"], c => c != "ship" && Countries.IsoCode(c) == null);
    }

    [Fact]
    public void A_clean_row_maps_with_no_issues()
    {
        var issues = new List<DataIssue>();
        var c = Map(Row(), issues);

        Assert.Empty(issues);
        Assert.Equal("Six Flags", c.Name);
        Assert.Equal("https://coasterpedia.net/wiki/Six_Flags", c.Url);
        Assert.Equal("active", c.Status);
        Assert.Equal(new DateValue("1959", "1959-01-01", "1959-12-31"), c.Established);
        Assert.Null(c.Closed);
        Assert.Equal(new Country("US", "USA"), c.Country);
        Assert.Equal("http://sixflags.com", c.Website);
    }

    [Fact]
    public void A_capitalised_status_matches_and_a_blank_one_is_left_out()
    {
        // Village Roadshow Theme Parks stores "Active"; PARC Management stores nothing.
        Assert.Equal("active", Map(Row(status: "Active")).Status);
        Assert.Null(Map(Row(status: "")).Status);
    }

    [Fact]
    public void An_external_link_website_publishes_its_url()
    {
        // Ocala Recreation and Parks, as stored.
        var c = Map(Row(website: "[https://www.ocalafl.gov/government/city-departments-i-z/recreation-parks https://www.ocalafl.gov]"));
        Assert.Equal("https://www.ocalafl.gov/government/city-departments-i-z/recreation-parks", c.Website);
    }

    [Fact]
    public void Headquarters_lose_their_markup_and_blank_is_null()
    {
        Assert.Equal("London, England", Map(Row(headquarters: "[[London]], England")).Headquarters);
        Assert.Null(Map(Row(headquarters: "")).Headquarters);
    }

    // ---- History on parks -------------------------------------------------

    private static readonly Dictionary<string, int> Resolved = new()
    {
        ["Six Flags"] = 821, ["Cedar Fair"] = 823, ["Paramount Parks"] = 8476, ["Looping Group"] = 64002,
    };

    private static HistoryRow Spell(string type, string name, int position, string? start = null, string? end = null) =>
        new()
        {
            PageId = 1, Type = type, Name = name, Position = position,
            Start = start, StartPrecision = start == null ? null : 3, End = end, EndPrecision = end == null ? null : 3,
        };

    private static ParkContent Park(string status, params HistoryRow[] history) =>
        ParkMapper.Map(new ParkRow { PageId = 1, PageName = "Example Park", Revision = 1, Name = "Example Park", Status = status, History = history },
            Vocab, "https://coasterpedia.net", [], Resolved);

    [Fact]
    public void Spells_come_oldest_first_and_only_the_one_in_force_is_current()
    {
        var p = Park("operating",
            Spell("operator", "[[Six Flags]]", 1, start: "2024-01-01"),
            Spell("operator", "[[Cedar Fair]]", 2, start: "2006-01-01", end: "2024-01-01"),
            Spell("operator", "[[Paramount Parks]]", 3, start: "1992-01-01", end: "2006-01-01"));

        Assert.Null(p.History!.Owners);
        Assert.Equal([8476, 823, 821], p.History.Operators!.Select(o => o.Company.Id));
        Assert.Equal([false, false, true], p.History.Operators!.Select(o => o.Current));
    }

    [Fact]
    public void Nothing_is_current_at_a_closed_park_or_after_an_end_date()
    {
        // A defunct park whose last owner never got an end date: 70 owner spells on 2026-10-13.
        Assert.False(Park("defunct", Spell("owner", "[[Six Flags]]", 1)).History!.Owners![0].Current);
        Assert.False(Park("cancelled", Spell("owner", "[[Six Flags]]", 1)).History!.Owners![0].Current);
        // A defunct park's last owner with an end date: 58 spells.
        Assert.False(Park("operating", Spell("owner", "[[Six Flags]]", 1, end: "2020-01-01")).History!.Owners![0].Current);
        Assert.True(Park("under construction", Spell("owner", "[[Six Flags]]", 1)).History!.Owners![0].Current);
    }

    [Fact]
    public void A_piped_link_keeps_its_target_and_its_name_and_a_redirect_resolves()
    {
        var owners = Park("operating",
            Spell("owner", "[[Six Flags|Premier Parks/Six Flags]]", 1),
            Spell("owner", "[[Looping Group]]", 2)).History!.Owners!;

        Assert.Equal(new Ref(64002, "Looping Group"), owners[0].Company);
        Assert.Equal(new Ref(821, "Premier Parks/Six Flags"), owners[1].Company);
    }

    [Fact]
    public void Plain_text_has_no_id_a_red_link_has_none_and_a_placeholder_names_nobody()
    {
        var issues = new List<DataIssue>();
        var p = ParkMapper.Map(new ParkRow
        {
            PageId = 1, PageName = "Example Park", Revision = 1, Status = "operating",
            History =
            [
                Spell("owner", "Independent", 1),
                Spell("owner", "[[Taft Broadcasting]]", 2),
                Spell("owner", "Unknown", 3),
            ],
        }, Vocab, "https://coasterpedia.net", issues, Resolved);

        Assert.Equal([new Ref(null, "Taft Broadcasting"), new Ref(null, "Independent")], p.History!.Owners!.Select(o => o.Company));
        // An article not written yet is normal on a wiki (DR-010 §12.13): not reported.
        Assert.Empty(issues);
    }

    [Fact]
    public void A_spell_naming_two_companies_is_two_entries_with_the_same_dates()
    {
        var owners = Park("operating", Spell("owner", "[[Parques Reunidos]] (95%)<br>[[Six Flags]] (5%)", 1, start: "2019-01-01"))
            .History!.Owners!;

        Assert.Equal(["Parques Reunidos", "Six Flags"], owners.Select(o => o.Company.Name));
        Assert.All(owners, o => Assert.Equal("2019", o.Start!.Edtf));
    }

    [Fact]
    public void A_park_with_no_spells_has_no_history()
    {
        Assert.Null(Park("operating").History);
        Assert.Null(Park("operating", Spell("owner", "Unknown", 1)).History);
    }
}
