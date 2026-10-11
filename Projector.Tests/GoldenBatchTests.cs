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
/// The ingest batch the Projector emits for real rows, pinned in one file that
/// the Worker's tests also ingest (EdgeApi/test/fixtures/projector-batch.json).
/// A change on either side of the wire fails one of the two suites.
/// Regenerate after a deliberate change: UPDATE_GOLDEN=1 dotnet test.
/// </summary>
public class GoldenBatchTests
{
    // Live Cargo rows, 2026-10-11 (revisions invented).
    private static readonly AttractionRow[] Rows =
    [
        new("coaster", 171, "Gold Rusher (Six Flags Magic Mountain)", 401, "Gold Rusher", "operating", "USA",
            "[[Arrow Dynamics|Arrow Development]]", 34.4234906, -118.598881)
        {
            Stats = new StatsRow
            {
                Units = "imperial", Height = 70, HeightMetric = 21.335999317248, Length = 2590, LengthMetric = 789.43197473818,
                Speed = 35, SpeedMetric = 56.32705742624, TrackInversions = 0, GForce = 3.4, Duration = "2:30",
                RidersHour = "1750", RidersTrain = 30, Tracks = 1,
            },
        },
        new("coaster", 165, "Batman The Ride (Six Flags Magic Mountain)", 402, "Batman The Ride", "operating", "USA",
            "[[Bolliger & Mabillard]]", 34.425775, -118.600895)
        {
            Stats = new StatsRow
            {
                Units = "imperial", Height = 105, HeightMetric = 32.003998975872, Drop = 84.5, DropMetric = 25.755599175821,
                Length = 2700, LengthMetric = 822.95997366528, Speed = 50, SpeedMetric = 80.467224894628,
                TrackInversions = 5, Angle = 59, GForce = 4, Duration = "2:00", RidersHour = "1400", RidersTrain = 32, Tracks = 1,
            },
        },
        new("coaster", 169, "Colossus (Six Flags Magic Mountain)", 403, "Colossus", "defunct", "USA",
            "[[International Amusement Devices]]", 34.427553, -118.59786)
        {
            // A racing coaster: two tracks.
            Stats = new StatsRow
            {
                Units = "imperial", Height = 125, HeightMetric = 38.0999987808, Drop = 115, DropMetric = 35.051998878336,
                Length = 4325, LengthMetric = 1318.2599578157, Speed = 62, SpeedMetric = 99.779358869339,
                TrackInversions = 0, GForce = 3.2, Duration = "2:30", RidersHour = "2600", RidersTrain = 24, Tracks = 2,
            },
        },
        new("coaster", 9101, "Example Twin (Example Park)", 404, "Example Twin", "standing but not operating", "Germany",
            "[[Zierer]] <br> [[BHS]]", null, null),
        new("ride", 19643, "Over the Hill 2: Spooksville", 405, "Over the Hill 2: Spooksville", "operating", "UK",
            "Self-built", 51.532395, 0.717849)
        {
            // Diameter has no _Metric column yet (rebuild A8): converted by Units. Values from SkyScreamer (20166).
            Stats = new StatsRow
            {
                Units = "imperial", Height = 242, HeightMetric = 73.761597639629, Diameter = 98, Speed = 35,
                SpeedMetric = 56.32705742624, RidersHour = "1,200",
            },
        },
        new("ride", 66486, "SkyRide (Carnival Horizon)", 406, "SkyRide", "operating", "ship",
            "[[SkyRide Technology]]", null, null),
        new("water_slide", 9102, "Élan (Example Waterpark)", 407, "Élan", "Operating", "Kosovo", "", 42.6, 21.1)
        {
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
    };

    [Fact]
    public void The_batch_matches_the_shared_fixture()
    {
        var vocab = VocabularySource.Parse(
            JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "templatedata.json")))!,
            new ProjectorConfig().AttractionTemplates);
        var at = DateTimeOffset.Parse("2026-10-11T12:00:00Z");
        var issues = new List<DataIssue>();
        // 9101 is an untrusted change waiting to settle; the rest are settled. The
        // promote and discard name pages the edge has never seen, so they must be
        // accepted and do nothing.
        var upserts = Rows.Select(r => Wire.Upsert(
            AttractionMapper.Map(r, vocab, Resolved, "https://coasterpedia.net", issues), r.Revision, at,
            r.PageId == 9101 ? at + TimeSpan.FromDays(7) : null));
        var batch = Wire.Batch(at, TimeSpan.FromDays(7), upserts, [Wire.Delete(170, 171)],
            [Wire.Promote(9998)], [Wire.Discard(9999)]);
        var actual = batch.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";

        Assert.Empty(issues);

        var path = Path.Combine(RepoRoot(), "EdgeApi", "test", "fixtures", "projector-batch.json");
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
