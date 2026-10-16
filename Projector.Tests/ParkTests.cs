using System.Text.Json;
using System.Text.Json.Nodes;
using CoasterpediaServices.Projector.Data;
using CoasterpediaServices.Projector.Mapping;
using CoasterpediaServices.Projector.Model;
using CoasterpediaServices.Projector.Options;
using CoasterpediaServices.Projector.Vocabulary;
using Xunit;

namespace CoasterpediaServices.Projector.Tests;

public class ParkTests
{
    // TemplateData as the wiki served it: the attraction infoboxes on 2026-10-11, the park one on 2026-10-12.
    private static readonly Vocabularies Vocab = VocabularySource.Parse(
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "templatedata.json")))!,
        new ProjectorConfig().VocabularyTemplates());

    // Cedar Point as stored on 2026-10-12.
    private static ParkRow Row(int id = 182, string page = "Cedar Point", string? name = "Cedar Point", string? status = "operating",
        string? country = "USA", string? state = "Ohio", string? location = "Sandusky", string? website = "http://cedarpoint.com/",
        double? lat = 41.48, double? lon = -82.6825) =>
        new()
        {
            PageId = id, PageName = page, Revision = 123456, Name = name, Status = status, Country = country, State = state,
            Location = location, Website = website, Lat = lat, Lon = lon,
            OpenedEdtf = "1870", OpenedEarliest = "1870-01-01", OpenedLatest = "1870-12-31",
        };

    private static ParkContent Map(ParkRow row, List<DataIssue>? issues = null) =>
        ParkMapper.Map(row, Vocab, "https://coasterpedia.net", issues ?? []);

    [Fact]
    public void The_park_infobox_has_its_own_allowed_values()
    {
        Assert.Equal("cancelled", Vocab.Match("park", "status", "Cancelled"));
        // A park is never in storage; that is an attraction's status.
        Assert.Null(Vocab.Match("park", "status", "in storage"));
        Assert.Equal("Ohio", Vocab.Match("park", "state", "ohio"));
        Assert.Equal("ship", Vocab.Match("park", "country", "ship"));
    }

    [Fact]
    public void Every_country_a_park_may_name_has_an_ISO_code_except_ship()
    {
        Assert.DoesNotContain(Vocab.All["park"]["country"], c => c != "ship" && Countries.IsoCode(c) == null);
    }

    [Fact]
    public void A_clean_row_maps_with_no_issues()
    {
        var issues = new List<DataIssue>();
        var c = Map(Row(), issues);

        Assert.Empty(issues);
        Assert.Equal("Cedar Point", c.Name);
        Assert.Equal("https://coasterpedia.net/wiki/Cedar_Point", c.Url);
        Assert.Equal("operating", c.Status);
        Assert.Equal(new Country("US", "USA"), c.Country);
        Assert.Equal("Ohio", c.State);
        Assert.Equal("Sandusky", c.Place);
        Assert.Equal(new Coordinates(41.48, -82.6825), c.Coordinates);
        Assert.Equal("http://cedarpoint.com/", c.Website);
        Assert.Equal(new DateValue("1870", "1870-01-01", "1870-12-31"), c.Dates!.Opened);
        Assert.Null(c.Dates.Closed);
    }

    [Fact]
    public void An_open_ended_date_keeps_its_null_bound_on_the_wire()
    {
        // Anna's Hoeve: opened 1961 or earlier, closed 1979 or later.
        var row = Row(26438, "Anna's Hoeve", "Anna's Hoeve", "defunct", "Netherlands", null, "Hilversum, Noord Holland", null, null, null) with
        {
            OpenedEdtf = "[..1961]", OpenedEarliest = null, OpenedLatest = "1961-12-31",
            ClosedEdtf = "[1979..]", ClosedEarliest = "1979-01-01", ClosedLatest = null,
        };
        var c = Map(row);

        Assert.Equal(new DateValue("[..1961]", null, "1961-12-31"), c.Dates!.Opened);
        var json = JsonSerializer.Serialize(c.Dates, Wire.Json);
        Assert.Equal(
            """{"opened":{"edtf":"[..1961]","earliest":null,"latest":"1961-12-31"},"closed":{"edtf":"[1979..]","earliest":"1979-01-01","latest":null}}""",
            json);
    }

    [Fact]
    public void A_park_with_no_dates_carries_no_dates_block()
    {
        var c = Map(Row() with { OpenedEdtf = null, OpenedEarliest = null, OpenedLatest = null });
        Assert.Null(c.Dates);
    }

    [Theory]
    [InlineData("2027-07-01", 1, "2027-07-01", "2027-07-01", "2027-07-01")] // Bibi & Tina Freizeitpark
    [InlineData("2027-12-01", 2, "2027-12", "2027-12-01", "2027-12-31")] // Cacau Park
    [InlineData("2028-02-01", 2, "2028-02", "2028-02-01", "2028-02-29")]
    [InlineData("2028-01-01", 3, "2028", "2028-01-01", "2028-12-31")]
    public void Opening_takes_its_EDTF_from_Cargos_precision(string stored, int precision, string edtf, string earliest, string latest)
    {
        var c = Map(Row() with { Opening = stored, OpeningPrecision = precision });
        Assert.Equal(new DateValue(edtf, earliest, latest), c.Dates!.Opening);
    }

    [Fact]
    public void An_opening_with_no_precision_is_null_and_reported()
    {
        var issues = new List<DataIssue>();
        Assert.Null(Map(Row() with { Opening = "2027-07-01", OpeningPrecision = null }, issues).Dates!.Opening);
        Assert.Equal("unknown date precision", Assert.Single(issues).Problem);
    }

    [Fact]
    public void Bad_bounds_are_null_and_reported()
    {
        var issues = new List<DataIssue>();
        var c = Map(Row() with { ClosedEdtf = "1990", ClosedEarliest = "1990-12-31", ClosedLatest = "1990-01-01" }, issues);
        Assert.Null(c.Dates!.Closed);
        Assert.Equal("earliest is after latest", Assert.Single(issues).Problem);

        issues.Clear();
        Assert.Null(Map(Row() with { ClosedEdtf = "[..]" }, issues).Dates!.Closed);
        Assert.Equal("EDTF stored with no bounds", Assert.Single(issues).Problem);

        issues.Clear();
        Assert.Null(Map(Row() with { ClosedEdtf = "1990", ClosedEarliest = "0000-00-00", ClosedLatest = null }, issues).Dates!.Closed);
        Assert.Contains(issues, i => i.Problem == "not a date");
    }

    [Theory]
    [InlineData("http://dollywood.com", "http://dollywood.com")]
    [InlineData("[https://www.carnival.com/cruise-ships/carnival-horizon https://www.carnival.com]",
        "https://www.carnival.com/cruise-ships/carnival-horizon")]
    [InlineData("[https://hiltonhead.disney.go.com/]", "https://hiltonhead.disney.go.com/")]
    [InlineData(" https://karls.de/erlebnisse/bibi-tina-freizeitpark/ ", "https://karls.de/erlebnisse/bibi-tina-freizeitpark/")]
    public void A_website_is_one_bare_URL(string stored, string published)
    {
        var issues = new List<DataIssue>();
        Assert.Equal(published, Map(Row(website: stored), issues).Website);
        Assert.Empty(issues);
    }

    [Theory]
    // Begemot Aquapark: a note after the link.
    [InlineData("[https://web.archive.org/web/20190206204446/http://begemotaqua.su/#now http://begemotaqua.su] (archive link)")]
    // Jambo! Park: two links.
    [InlineData("[http://a.example/ a], [http://b.example/ b]")]
    // Discovery Parks - Alice Springs: no scheme.
    [InlineData("discoveryholidayparks.com.au/caravan-parks/northern-territory/alice-springs/alice-springs")]
    [InlineData("[[Some page]]")]
    public void Anything_else_in_website_is_null_and_reported(string stored)
    {
        var issues = new List<DataIssue>();
        Assert.Null(Map(Row(website: stored), issues).Website);
        Assert.Equal(new DataIssue(182, "website", stored.Trim(), "not a single http(s) URL"), Assert.Single(issues));
    }

    [Fact]
    public void A_cruise_ship_has_no_country_and_is_not_an_issue()
    {
        var issues = new List<DataIssue>();
        var c = Map(Row(67009, "Carnival Horizon", "Carnival Horizon", "operating", "ship", null, "", null, null, null), issues);
        Assert.Null(c.Country);
        Assert.Null(c.Place);
        Assert.Empty(issues);
    }

    [Fact]
    public void A_state_outside_the_list_is_null_and_reported()
    {
        var issues = new List<DataIssue>();
        Assert.Null(Map(Row(state: "Ontario"), issues).State);
        Assert.Equal(new DataIssue(182, "state", "Ontario", "not in the infobox's allowed list"), Assert.Single(issues));
    }

    [Fact]
    public void A_park_status_uses_the_park_list()
    {
        var issues = new List<DataIssue>();
        Assert.Null(Map(Row(status: "in storage"), issues).Status);
        Assert.Single(issues);
    }

    [Fact]
    public void The_park_upsert_carries_its_own_filters_and_summary()
    {
        var at = DateTimeOffset.Parse("2026-10-12T12:00:00Z");
        var item = Wire.UpsertPark(Map(Row()), 42, at);

        Assert.Equal(
            """{"kind":"park","id":182,"revision":42,"updated_at":"2026-10-12T12:00:00Z","settled":true,"settles_at":null,"sort_name":"cedar point","filters":{"status":"operating","country":"US","state":"Ohio","resort":null,"resort_order":null},"summary":{"id":182,"name":"Cedar Point","status":"operating","country":{"code":"US","name":"USA"},"state":"Ohio","coordinates":{"lat":41.48,"lon":-82.6825},"opened":{"edtf":"1870","earliest":"1870-01-01","latest":"1870-12-31"},"updated_at":"2026-10-12T12:00:00Z"},"record":{"id":182,"name":"Cedar Point","title":"Cedar Point","url":"https://coasterpedia.net/wiki/Cedar_Point","status":"operating","dates":{"opened":{"edtf":"1870","earliest":"1870-01-01","latest":"1870-12-31"}},"place":"Sandusky","state":"Ohio","country":{"code":"US","name":"USA"},"coordinates":{"lat":41.48,"lon":-82.6825},"website":"http://cedarpoint.com/","revision":42,"updated_at":"2026-10-12T12:00:00Z"}}""",
            JsonSerializer.Serialize(item));
    }

    private static readonly HashSet<int> Parks = [182, 2156, 61001];

    [Fact]
    public void A_resort_is_named_by_its_wrapper_and_identified_by_the_wrapper_page()
    {
        // Template:Walt Disney World Resort links its resort name.
        ResortMemberRow[] members = [new() { ResortId = 56391, Resort = "[[Walt Disney World]]", Park = "Magic Kingdom", Ordering = 5 }];
        var resorts = ParkMapper.Resorts(members, new Dictionary<string, int> { ["Magic Kingdom"] = 182 }, Parks, []);
        Assert.Equal(new ResortMembership(new Resort(56391, "Walt Disney World"), 5), resorts[182]);

        Assert.Null(Map(Row()).Resort);
        Assert.Equal(new Resort(56391, "Walt Disney World"),
            ParkMapper.Map(Row(), Vocab, "https://coasterpedia.net", [], resort: resorts[182].Resort).Resort);
    }

    [Fact]
    public void A_member_matches_through_redirects_and_only_a_park_attaches()
    {
        var members = new[]
        {
            new ResortMemberRow { ResortId = 58420, Resort = "Cedar Point Resort", Park = "Cedar point", Ordering = 1 }, // normalised like a link target
            // Moved since.
            new ResortMemberRow { ResortId = 58420, Resort = "Cedar Point Resort", Park = "Cedar Point Shores (old title)", Ordering = 2 },
            new ResortMemberRow { ResortId = 1, Resort = "Adventure Landing", Park = "Shipwreck Island Waterpark", Ordering = 2 }, // no such page
            new ResortMemberRow { ResortId = 2, Resort = "Somewhere", Park = "Some article", Ordering = 1 }, // a page, not a park
            new ResortMemberRow { ResortId = 3, Resort = "", Park = "Dollywood", Ordering = 1 }, // no resort name
        };
        var resolved = new Dictionary<string, int>
        {
            ["Cedar point"] = 182, ["Cedar Point Shores (old title)"] = 61001, ["Some article"] = 9, ["Dollywood"] = 2156,
        };
        var resorts = ParkMapper.Resorts(members, resolved, Parks, []);

        Assert.Equal([182, 61001], resorts.Keys.Order());
        Assert.Equal(58420, resorts[61001].Resort.Id);
    }

    [Fact]
    public void A_park_two_wrappers_claim_keeps_one_and_is_reported()
    {
        var members = new[]
        {
            new ResortMemberRow { ResortId = 20, Resort = "Zeta Resort", Park = "Cedar Point", Ordering = 1 },
            new ResortMemberRow { ResortId = 10, Resort = "Alpha Resort", Park = "Cedar Point", Ordering = 3 },
        };
        var issues = new List<DataIssue>();
        var resorts = ParkMapper.Resorts(members, new Dictionary<string, int> { ["Cedar Point"] = 182 }, Parks, issues);

        Assert.Equal(new ResortMembership(new Resort(10, "Alpha Resort"), 3), resorts[182]);
        Assert.Equal(new DataIssue(182, "resort", "Zeta Resort", "also a member of Alpha Resort; only that one is published"),
            Assert.Single(issues));
    }

    [Fact]
    public void The_park_upsert_filters_and_lists_by_resort()
    {
        var item = Wire.UpsertPark(ParkMapper.Map(Row(), Vocab, "https://coasterpedia.net", [], resort: new Resort(58420, "Cedar Point Resort")),
            42, DateTimeOffset.Parse("2026-10-12T12:00:00Z"), resortOrder: 1);
        Assert.Equal(58420, item["filters"]!["resort"]!.GetValue<int>());
        // The wrapper's order sorts the resort's parks at the edge; it is never in the record.
        Assert.Equal(1, item["filters"]!["resort_order"]!.GetValue<int>());
        Assert.Null(item["record"]!["resort_order"]);
        Assert.Equal("""{"id":58420,"name":"Cedar Point Resort"}""", item["summary"]!["resort"]!.ToJsonString());
        Assert.Equal("""{"id":58420,"name":"Cedar Point Resort"}""", item["record"]!["resort"]!.ToJsonString());
    }

    [Fact]
    public void Deletes_promotes_and_discards_name_their_kind()
    {
        Assert.Equal("park", Wire.Delete(1, 2, Wire.Park)["kind"]!.GetValue<string>());
        Assert.Equal("park", Wire.Promote(1, Wire.Park)["kind"]!.GetValue<string>());
        Assert.Equal("park", Wire.Discard(1, Wire.Park)["kind"]!.GetValue<string>());
        Assert.Equal("attraction", Wire.Delete(1, null)["kind"]!.GetValue<string>());
    }

    // ---- Names ---------------------------------------------------------------

    private static HistoryRow Named(string name, int position, string? start = null, string? end = null) =>
        new()
        {
            PageId = 182, Type = "name", Name = name, Position = position,
            Start = start, StartPrecision = start == null ? null : 3, End = end, EndPrecision = end == null ? null : 3,
        };

    [Fact]
    public void Names_come_oldest_first_with_their_dates()
    {
        var issues = new List<DataIssue>();
        var p = Map(Row(name: "Dollywood") with
        {
            History = [Named("Dollywood", 1, "1986-01-01"), Named("Silver Dollar City", 2, "1976-01-01", "1986-01-01"), Named("Rebel Railroad", 3, "1961-01-01", "1969-01-01")],
        }, issues);

        Assert.Empty(issues);
        Assert.Equal(["Rebel Railroad", "Silver Dollar City", "Dollywood"], p.History!.Names!.Select(n => n.Name));
        Assert.Equal(new DateValue("1961", "1961-01-01", "1961-12-31"), p.History.Names![0].Start);
        Assert.Null(p.History.Names![2].End);
        Assert.Null(p.History.Owners);
    }

    [Fact]
    public void A_park_never_renamed_has_no_history()
    {
        Assert.Null(Map(Row() with { History = [Named("Cedar Point", 1)] }).History);
    }

    [Fact]
    public void A_placeholder_name_is_left_out_and_markup_is_stripped()
    {
        var p = Map(Row(name: "Kamataen") with { History = [Named("Kamataen", 1), Named("''unknown''", 2), Named("''Old Kamata''", 3)] });
        Assert.Equal(["Old Kamata", "Kamataen"], p.History!.Names!.Select(n => n.Name));
    }

    [Fact]
    public void A_park_name_carries_no_location_index_on_the_wire()
    {
        var p = Map(Row() with { History = [Named("Cedar Point", 1), Named("Cedar Point Pleasure Resort", 2)] });
        var json = JsonSerializer.SerializeToNode(p.History, Wire.Json)!.AsObject();
        var first = json["names"]![0]!.AsObject();
        Assert.Equal(["name", "start", "end"], first.Select(kv => kv.Key));
        Assert.Null(json["owners"]);
    }

    [Fact]
    public void Status_values_parks_offer_apply_to_park_too()
    {
        var doc = VocabularyDocument.Build(Vocab, new ProjectorConfig().AttractionTemplates, [Wire.Park]);

        // park is in applies_to, never a type.
        Assert.Equal(["coaster", "ride", "water_slide"], doc["type"]!.AsArray().Select(t => t!["value"]!.GetValue<string>()));
        var status = doc["status"]!.AsArray().ToDictionary(s => s!["value"]!.GetValue<string>(),
            s => s!["applies_to"]!.AsArray().Select(t => t!.GetValue<string>()).ToList());
        Assert.Equal(["coaster", "ride", "water_slide", "park"], status["operating"]);
        Assert.Equal(["coaster", "ride", "water_slide"], status["in_storage"]);
    }
}
