using System.Text.Json;
using System.Text.Json.Nodes;
using CoasterpediaServices.Projector.Data;
using CoasterpediaServices.Projector.Mapping;
using CoasterpediaServices.Projector.Model;
using CoasterpediaServices.Projector.Options;
using CoasterpediaServices.Projector.Vocabulary;
using Xunit;

namespace CoasterpediaServices.Projector.Tests;

/// <summary>
/// The ingest batches the Projector emits for real rows, pinned in files that
/// the Worker's tests also ingest (EdgeApi/test/fixtures/projector-batch.json,
/// projector-park-batch.json, projector-manufacturer-batch.json and projector-product-batch.json). A change on either side of the wire fails
/// one of the two suites.
/// Regenerate after a deliberate change: UPDATE_GOLDEN=1 dotnet test.
/// </summary>
public class GoldenBatchTests
{
    // Live Cargo rows, 2026-10-11 (revisions invented).
    private static readonly AttractionRow[] Rows =
    [
        new()
        {
            Type = "coaster", PageId = 171, PageName = "Gold Rusher (Six Flags Magic Mountain)", Revision = 401,
            Name = "Gold Rusher", Status = "operating", Country = "USA",
            Manufacturer = "[[Arrow Dynamics|Arrow Development]]",
            Product = "[[Runaway Train (Arrow Dynamics product)|Runaway Train]]", Lat = 34.4234906, Lon = -118.598881,
            Stats = new StatsRow
            {
                Units = "imperial", Height = 70, HeightMetric = 21.335999317248, Length = 2590, LengthMetric = 789.43197473818,
                Speed = 35, SpeedMetric = 56.32705742624, TrackInversions = 0, GForce = 3.4, Duration = "2:30",
                RidersHour = "1750", RidersTrain = 30, Tracks = 1,
            },
        },
        new()
        {
            Type = "coaster", PageId = 165, PageName = "Batman The Ride (Six Flags Magic Mountain)", Revision = 402,
            Name = "Batman The Ride", Status = "operating", Country = "USA", Manufacturer = "[[Bolliger & Mabillard]]",
            Product = "[[Inverted Coaster (Bolliger & Mabillard product)|Inverted Coaster]]", ProductVariant = "Batman",
            Lat = 34.425775, Lon = -118.600895,
            Stats = new StatsRow
            {
                Units = "imperial", Height = 105, HeightMetric = 32.003998975872, Drop = 84.5, DropMetric = 25.755599175821,
                Length = 2700, LengthMetric = 822.95997366528, Speed = 50, SpeedMetric = 80.467224894628,
                TrackInversions = 5, Angle = 59, GForce = 4, Duration = "2:00", RidersHour = "1400", RidersTrain = 32, Tracks = 1,
            },
        },
        new()
        {
            Type = "coaster", PageId = 169, PageName = "Colossus (Six Flags Magic Mountain)", Revision = 403,
            Name = "Colossus", Status = "defunct", Country = "USA", Manufacturer = "[[International Amusement Devices]]",
            Lat = 34.427553, Lon = -118.59786,
            // A racing coaster: two tracks.
            Stats = new StatsRow
            {
                Units = "imperial", Height = 125, HeightMetric = 38.0999987808, Drop = 115, DropMetric = 35.051998878336,
                Length = 4325, LengthMetric = 1318.2599578157, Speed = 62, SpeedMetric = 99.779358869339,
                TrackInversions = 0, GForce = 3.2, Duration = "2:30", RidersHour = "2600", RidersTrain = 24, Tracks = 2,
            },
        },
        new()
        {
            Type = "coaster", PageId = 9101, PageName = "Example Twin (Example Park)", Revision = 404,
            Name = "Example Twin", Status = "standing but not operating", Country = "Germany",
            Manufacturer = "[[Zierer]] <br> [[BHS]]",
            // A product in projector-product-batch.json, so the edge counts an installation pending.
            Product = "[[Boomerang (Vekoma product)|Boomerang]]",
        },
        new()
        {
            Type = "ride", PageId = 19643, PageName = "Over the Hill 2: Spooksville", Revision = 405,
            Name = "Over the Hill 2: Spooksville", Status = "operating", Country = "UK", Manufacturer = "Self-built",
            Product = "[[Star Flyer (Funtime product)|Star Flyer]]", Lat = 51.532395, Lon = 0.717849,
            // Values from SkyScreamer (20166).
            Stats = new StatsRow
            {
                Units = "imperial", Height = 242, HeightMetric = 73.761597639629, Diameter = 98, DiameterMetric = 29.870399044147, Speed = 35,
                SpeedMetric = 56.32705742624, RidersHour = "1,200",
            },
        },
        new()
        {
            Type = "ride", PageId = 66486, PageName = "SkyRide (Carnival Horizon)", Revision = 406, Name = "SkyRide",
            Status = "operating", Country = "ship", Manufacturer = "[[SkyRide Technology]]",
            // A red link, as on the wiki.
            Product = "[[SkyRide Human Powered (SkyRide Technology product)|SkyRide Human Powered]]",
        },
        new()
        {
            Type = "water_slide", PageId = 9102, PageName = "Élan (Example Waterpark)", Revision = 407, Name = "Élan",
            Status = "Operating", Country = "Kosovo", Manufacturer = "", Lat = 42.6, Lon = 21.1,
            // A complex of two products, one of them in projector-product-batch.json.
            Product = "[[AquaPlay (WhiteWater West product)|AquaPlay]]#[[TWISTER (ProSlide product)|TWISTER]]",
            // Values from Summit Plummet (55725).
            Stats = new StatsRow { Units = "imperial", Height = 120, HeightMetric = 36.575998829568, Length = 360, LengthMetric = 109.7279964887 },
        },
    ];

    private static readonly Dictionary<string, int> Resolved = new()
    {
        ["Arrow Dynamics"] = 1829,
        ["Bolliger & Mabillard"] = 543,
        ["International Amusement Devices"] = 2210,
        ["Zierer"] = 77,
        ["SkyRide Technology"] = 66480,
        ["Runaway Train (Arrow Dynamics product)"] = 53318,
        ["Inverted Coaster (Bolliger & Mabillard product)"] = 1498,
        ["Star Flyer (Funtime product)"] = 44361,
        ["AquaPlay (WhiteWater West product)"] = 82994,
        ["TWISTER (ProSlide product)"] = 58307,
        ["Boomerang (Vekoma product)"] = 3213,
    };

    private static readonly HashSet<int> ProductIds = [53318, 1498, 44361, 82994, 58307, 3213];

    // Live Amusement_Parks rows, 2026-10-12 (revisions invented).
    private static readonly ParkRow[] ParkRows =
    [
        new()
        {
            PageId = 182, PageName = "Cedar Point", Revision = 501, Name = "Cedar Point", Status = "operating",
            Country = "USA", State = "Ohio", Location = "Sandusky", Website = "http://cedarpoint.com/", Lat = 41.48,
            Lon = -82.6825,
            OpenedEdtf = "1870", OpenedEarliest = "1870-01-01", OpenedLatest = "1870-12-31",
        },
        new()
        {
            PageId = 2156, PageName = "Dollywood", Revision = 502, Name = "Dollywood", Status = "operating",
            Country = "USA", State = "Tennessee", Location = "Pigeon Forge", Website = "http://dollywood.com",
            Lat = 35.796504, Lon = -83.533699,
            OpenedEdtf = "1961", OpenedEarliest = "1961-01-01", OpenedLatest = "1961-12-31",
        },
        new()
        {
            PageId = 26438, PageName = "Anna's Hoeve", Revision = 503, Name = "Anna's Hoeve", Status = "defunct",
            Country = "Netherlands", Location = "Hilversum, Noord Holland",
            OpenedEdtf = "[..1961]", OpenedLatest = "1961-12-31",
            ClosedEdtf = "[1979..]", ClosedEarliest = "1979-01-01",
        },
        new()
        {
            PageId = 65834, PageName = "Acushnet Park", Revision = 504, Name = "Acushnet Park", Status = "defunct",
            Country = "USA", State = "Massachusetts", Location = "New Bedford",
            OpenedEdtf = "1916", OpenedEarliest = "1916-01-01", OpenedLatest = "1916-12-31",
            ClosedEdtf = "1952~", ClosedEarliest = "1952-01-01", ClosedLatest = "1952-12-31",
        },
        new()
        {
            PageId = 66765, PageName = "Cacau Park", Revision = 505, Name = "Cacau Park", Status = "under construction",
            Country = "Brazil", Location = "Sorocaba, São Paulo", Website = "https://www.cacaushow.com.br/cacau-park.html",
            Lat = -23.37989020563, Lon = -47.38641610419,
            Opening = "2027-12-01", OpeningPrecision = 2,
        },
        new()
        {
            PageId = 67009, PageName = "Carnival Horizon", Revision = 506, Name = "Carnival Horizon", Status = "operating",
            Country = "ship", Location = "",
            Website = "[https://www.carnival.com/cruise-ships/carnival-horizon https://www.carnival.com]",
            OpenedEdtf = "2018-04-02", OpenedEarliest = "2018-04-02", OpenedLatest = "2018-04-02",
        },
        new()
        {
            PageId = 76098, PageName = "Bibi & Tina Freizeitpark", Revision = 507, Name = "Bibi & Tina Freizeitpark",
            Status = "under construction", Country = "Germany", Location = "Wustermark, Brandenburg",
            Website = "https://karls.de/erlebnisse/bibi-tina-freizeitpark/",
            Opening = "2027-07-01", OpeningPrecision = 1,
        },
    ];

    // Live Resorts rows, 2026-10-12: two members each, the second a park this batch does not carry.
    private static readonly ResortMemberRow[] ResortMembers =
    [
        new() { ResortId = 58420, Resort = "Cedar Point Resort", Park = "Cedar Point", Ordering = 1 },
        new() { ResortId = 58420, Resort = "Cedar Point Resort", Park = "Cedar Point Shores", Ordering = 2 },
        new() { ResortId = 57454, Resort = "Dollywood Resort", Park = "Dollywood", Ordering = 1 },
        new() { ResortId = 57454, Resort = "Dollywood Resort", Park = "Dollywood's Splash Country", Ordering = 2 },
    ];

    // Live Manufacturers rows, 2026-10-12 (revisions invented).
    private static readonly ManufacturerRow[] ManufacturerRows =
    [
        new()
        {
            PageId = 543, PageName = "Bolliger & Mabillard", Revision = 601, Name = "Bolliger & Mabillard",
            Status = "active", Country = "Switzerland", Headquarters = "Monthey, Valais, Switzerland",
            Website = "https://bolliger-mabillard.com/", Founded = "1988-01-01", FoundedPrecision = 3,
        },
        // No founding date or website.
        new()
        {
            PageId = 1829, PageName = "Arrow Dynamics", Revision = 602, Name = "Arrow Dynamics", Status = "defunct",
            Country = "USA", Headquarters = "Clearfield, Utah, USA", Website = "",
        },
        new()
        {
            PageId = 2393, PageName = "Mack Rides", Revision = 603, Name = "Mack Rides", Status = "active",
            Country = "Germany", Headquarters = "Waldkrich, Germany", Website = "http://www.mack-rides.com/",
            Founded = "1780-01-01", FoundedPrecision = 3,
        },
        // Founded to the month.
        new()
        {
            PageId = 17469, PageName = "Adesko", Revision = 604, Name = "Adesko", Status = "active", Country = "Belgium",
            Headquarters = "Dessel, Antwerp, Belgium", Website = "http://www.adesko.com/index.htm", Founded = "1991-10-01",
            FoundedPrecision = 2,
        },
        // Named apart from its title, status capitalised, founded to the day.
        new()
        {
            PageId = 55205, PageName = "ProSlide", Revision = 605, Name = "ProSlide Technology", Status = "Active",
            Country = "Canada", Headquarters = "Ottawa, Ontario, Canada", Website = "https://www.proslide.com/",
            Founded = "1986-12-19", FoundedPrecision = 1,
        },
        // The infobox's external link.
        new()
        {
            PageId = 3551, PageName = "The Gravity Group, LLC", Revision = 606, Name = "The Gravity Group, LLC",
            Status = "active", Country = "USA", Headquarters = "Cincinnati, Ohio, USA",
            Website = "[http://www.thegravitygroup.com/ www.thegravitygroup.com]", Founded = "2002-07-01",
            FoundedPrecision = 2,
        },
        // No country or headquarters.
        new()
        {
            PageId = 548, PageName = "Vekoma", Revision = 607, Name = "Vekoma", Status = "active", Country = "",
            Headquarters = "", Website = "https://www.vekoma.com/", Founded = "1926-01-01", FoundedPrecision = 3,
        },
    ];

    // Live product rows, 2026-10-12 (revisions invented; variants trimmed to a few).
    private static readonly ProductRow[] ProductRows =
    [
        // Specs in the infobox: one unnamed variant.
        new()
        {
            Type = "coaster", PageId = 3213, PageName = "Boomerang (Vekoma product)", Revision = 701, Name = "Boomerang",
            Status = "Discontinued", Manufacturer = "[[Vekoma]]", Units = "metric",
            Introduced = "1984-01-01", IntroducedPrecision = 3, TrackMaterial = "Steel", SupportMaterial = "Steel",
            Variants =
            [
                new VariantRow
                {
                    PageId = 3213, RowId = 430,
                    Stats = new StatsRow
                    {
                        Height = 35.5, HeightMetric = 35.5, Length = 285, LengthMetric = 285, Speed = 75.6, SpeedMetric = 75.6,
                        TrackInversions = 3, RiderInversions = 6, Angle = 45, GForce = 5.2, Duration = "1:48",
                        RidersHour = "760", RidersTrain = 28, Dimension1 = 88, Dimension2 = 30,
                    },
                },
            ],
        },
        // Variant blocks after an empty unnamed row; one name is a link.
        new()
        {
            Type = "coaster", PageId = 68541, PageName = "Sit Down Thrill Coaster (Vekoma product)", Revision = 702,
            Name = "Sit Down Thrill Coaster", Status = "in production", Manufacturer = "[[Vekoma]]", Units = "metric",
            Introduced = "2016-01-01", IntroducedPrecision = 3,
            Variants =
            [
                new VariantRow { PageId = 68541, RowId = 574 },
                new VariantRow
                {
                    PageId = 68541, RowId = 575, Name = "Lech Coaster",
                    Stats = new StatsRow { Units = "metric", Height = 40, HeightMetric = 40, Length = 910, LengthMetric = 910, Speed = 95, SpeedMetric = 95 },
                },
                new VariantRow
                {
                    PageId = 68541, RowId = 581, Name = "[[Top Gun Launch Coaster]]",
                    Stats = new StatsRow { Units = "metric", Height = 31, HeightMetric = 31, Length = 912, LengthMetric = 912, Speed = 97, SpeedMetric = 97 },
                },
            ],
        },
        // Two manufacturers; no specs, so no variants.
        new()
        {
            Type = "coaster", PageId = 54013, PageName = "Swiss Bob", Revision = 703, Name = "Swiss Bob",
            Status = "discontinued", Manufacturer = "[[Giovanola]] <br> [[Intamin]]", Units = "metric",
            TrackMaterial = "Steel", SupportMaterial = "Steel", Variants = [new VariantRow { PageId = 54013, RowId = 500 }],
        },
        // Discontinued to the year; a hyphenated restraint.
        new()
        {
            Type = "coaster", PageId = 86315, PageName = "Mini Mouse (Zamperla product)", Revision = 704,
            Name = "Mini Mouse", Status = "discontinued", Manufacturer = "[[Zamperla]]", Units = "metric",
            Discontinued = "2015-01-01", DiscontinuedPrecision = 3, TrackMaterial = "Steel", Restraints = "Shared lap-bars",
        },
        // Grouped ride variants.
        new()
        {
            Type = "ride", PageId = 18364, PageName = "Disk'O (Zamperla product)", Revision = 705, Name = "Disk'O",
            Status = "in production", Manufacturer = "[[Zamperla]]", Units = "metric",
            Introduced = "2003-01-01", IntroducedPrecision = 3,
            Variants =
            [
                new VariantRow { PageId = 18364, RowId = 1038 },
                new VariantRow
                {
                    PageId = 18364, RowId = 1041, Name = "Disk'O 16", Group = "Simple",
                    Stats = new StatsRow
                    {
                        Units = "metric", Height = 10.4, HeightMetric = 10.4, RidersHour = "400", RidersTrain = 16,
                        Dimension1 = 23.5, Dimension2 = 8.6,
                    },
                },
                new VariantRow
                {
                    PageId = 18364, RowId = 1044, Name = "Disk'O Coaster (40)", Group = "Coaster",
                    Stats = new StatsRow
                    {
                        Units = "metric", Height = 15.5, HeightMetric = 15.5, RidersHour = "600", RidersTrain = 40,
                        Dimension1 = 81.5, Dimension2 = 11.3,
                    },
                },
            ],
        },
        // Imperial, with Diameter_Metric and a sweep; a piped manufacturer.
        new()
        {
            Type = "ride", PageId = 52106, PageName = "Vertigo (A.R.M. USA product)", Revision = 706, Name = "Vertigo",
            Status = "in production", Manufacturer = "[[A.R.M. (USA)|A.R.M.]]", Units = "imperial",
            Introduced = "2009-01-01", IntroducedPrecision = 3,
            Variants =
            [
                new VariantRow
                {
                    PageId = 52106, RowId = 1561, Name = "Vertigo Park",
                    Stats = new StatsRow { Units = "imperial", Height = 130, HeightMetric = 39.623998732032 },
                    Sweep = 12,
                },
                new VariantRow
                {
                    PageId = 52106, RowId = 1557, Name = "Vertigo 100",
                    Stats = new StatsRow
                    {
                        Units = "imperial", Height = 94.4, HeightMetric = 28.77311907926, Diameter = 76, DiameterMetric = 23.164799258726,
                    },
                    Sweep = 12,
                },
            ],
        },
        // A water slide product with variants carrying a slide count.
        new()
        {
            Type = "water_slide", PageId = 82994, PageName = "AquaPlay (WhiteWater West product)", Revision = 707,
            Name = "AquaPlay", Status = "in production", Manufacturer = "[[WhiteWater West]]", Units = "metric",
            Classes = "Body", SlideMaterial = "Plastic",
            Variants =
            [
                new VariantRow { PageId = 82994, RowId = 258 },
                new VariantRow
                {
                    PageId = 82994, RowId = 259, Name = "AP 50",
                    Stats = new StatsRow { Units = "metric", Height = 3.8, HeightMetric = 3.8, Dimension1 = 8.8, Dimension2 = 7.9 },
                    NumberOfSlides = 2,
                },
            ],
        },
        // A capability list with integers; a manufacturer with no page.
        new()
        {
            Type = "water_slide", PageId = 84000, PageName = "Family Twister (Example product)", Revision = 708,
            Name = "Family Twister", Status = "in production", Manufacturer = "Example Slides Ltd", Units = "metric",
            Classes = "Body#Tube", Containment = "Open", RidersTube = "1#2#3",
        },
    ];

    private static readonly Dictionary<string, int> ProductResolved = new()
    {
        ["Vekoma"] = 548, ["Giovanola"] = 2416, ["Intamin"] = 546, ["Zamperla"] = 2399, ["A.R.M. (USA)"] = 46253,
        ["WhiteWater West"] = 55374,
    };

    private static Vocabularies Vocab() => VocabularySource.Parse(
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "templatedata.json")))!,
        new ProjectorConfig().VocabularyTemplates());

    [Fact]
    public void The_batch_matches_the_shared_fixture()
    {
        var templates = new ProjectorConfig().AttractionTemplates;
        var vocab = Vocab();
        var at = DateTimeOffset.Parse("2026-10-11T12:00:00Z");
        var issues = new List<DataIssue>();
        // 9101 is an untrusted change waiting to settle; the rest are settled. The
        // promote and discard name pages the edge has never seen, so they must be
        // accepted and do nothing.
        var upserts = Rows.Select(r => Wire.Upsert(
            AttractionMapper.Map(r, vocab, Resolved, ProductIds, "https://coasterpedia.net", issues), r.Revision, at,
            r.PageId == 9101 ? at + TimeSpan.FromDays(7) : null));
        var batch = Wire.Batch(at, TimeSpan.FromDays(7), upserts, [Wire.Delete(170, 171)],
            [Wire.Promote(9998)], [Wire.Discard(9999)], VocabularyDocument.Build(vocab, templates, [Wire.Park, Wire.Manufacturer, Wire.Product]));

        Assert.Empty(issues);
        AssertGolden("projector-batch.json", batch);
    }

    [Fact]
    public void The_park_batch_matches_the_shared_fixture()
    {
        var at = DateTimeOffset.Parse("2026-10-12T12:00:00Z");
        var issues = new List<DataIssue>();
        var vocab = Vocab();
        var resorts = ParkMapper.Resorts(ResortMembers,
            new Dictionary<string, int> { ["Cedar Point"] = 182, ["Cedar Point Shores"] = 61001, ["Dollywood"] = 2156 },
            ParkRows.Select(r => r.PageId).ToHashSet(), issues);
        // Acushnet Park waits to settle; 99001 was merged into Cedar Point. The
        // promote and discard name parks the edge has never seen.
        var upserts = ParkRows.Select(r => Wire.UpsertPark(ParkMapper.Map(r, vocab, "https://coasterpedia.net", issues, resorts.GetValueOrDefault(r.PageId)),
            r.Revision, at, r.PageId == 65834 ? at + TimeSpan.FromDays(7) : null));
        var batch = Wire.Batch(at, TimeSpan.FromDays(7), upserts, [Wire.Delete(99001, 182, Wire.Park)],
            [Wire.Promote(99998, Wire.Park)], [Wire.Discard(99999, Wire.Park)]);

        Assert.Empty(issues);
        AssertGolden("projector-park-batch.json", batch);
    }

    [Fact]
    public void The_manufacturer_batch_matches_the_shared_fixture()
    {
        var at = DateTimeOffset.Parse("2026-10-12T12:00:00Z");
        var issues = new List<DataIssue>();
        var vocab = Vocab();
        // Adesko waits to settle; 99101 was merged into Bolliger & Mabillard. The
        // promote and discard name manufacturers the edge has never seen.
        var upserts = ManufacturerRows.Select(r => Wire.UpsertManufacturer(ManufacturerMapper.Map(r, vocab, "https://coasterpedia.net", issues),
            r.Revision, at, r.PageId == 17469 ? at + TimeSpan.FromDays(7) : null));
        var batch = Wire.Batch(at, TimeSpan.FromDays(7), upserts, [Wire.Delete(99101, 543, Wire.Manufacturer)],
            [Wire.Promote(99198, Wire.Manufacturer)], [Wire.Discard(99199, Wire.Manufacturer)]);

        Assert.Empty(issues);
        AssertGolden("projector-manufacturer-batch.json", batch);
    }

    [Fact]
    public void The_product_batch_matches_the_shared_fixture()
    {
        var at = DateTimeOffset.Parse("2026-10-12T12:00:00Z");
        var issues = new List<DataIssue>();
        var vocab = Vocab();
        // Mini Mouse waits to settle; 99201 was merged into Boomerang. The
        // promote and discard name products the edge has never seen.
        var upserts = ProductRows.Select(r => Wire.UpsertProduct(ProductMapper.Map(r, vocab, ProductResolved, "https://coasterpedia.net", issues),
            r.Revision, at, r.PageId == 86315 ? at + TimeSpan.FromDays(7) : null));
        var batch = Wire.Batch(at, TimeSpan.FromDays(7), upserts, [Wire.Delete(99201, 3213, Wire.Product)],
            [Wire.Promote(99298, Wire.Product)], [Wire.Discard(99299, Wire.Product)]);

        Assert.Empty(issues);
        AssertGolden("projector-product-batch.json", batch);
    }

    private static void AssertGolden(string name, JsonObject batch)
    {
        var actual = batch.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        var path = Path.Combine(RepoRoot(), "EdgeApi", "test", "fixtures", name);
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
        }

        Assert.Equal(File.ReadAllText(path), actual);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CoasterpediaServices.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found");
    }
}
