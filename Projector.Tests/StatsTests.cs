using System.Text.Json;
using CoasterpediaServices.Projector.Data;
using CoasterpediaServices.Projector.Mapping;
using CoasterpediaServices.Projector.Model;
using Xunit;

namespace CoasterpediaServices.Projector.Tests;

public class StatsTests
{
    private static AttractionStats? Map(StatsRow row, List<DataIssue>? issues = null) =>
        StatsMapper.Map(1, row, issues ?? []);

    // Stratosphere Tower Fishhook Coaster (22784), as stored 2026-10-11.
    private static readonly StatsRow Stratosphere = new()
    {
        Units = "imperial", Height = 740, HeightMetric = 225.55199278234, Drop = 700, DropMetric = 213.35999317248,
        Speed = 122, SpeedMetric = 196.34002874289, TrackInversions = 0, Tracks = 1,
    };

    [Fact]
    public void Imperial_figures_publish_metric_one_step_finer_than_typed_and_keep_what_was_typed()
    {
        var s = Map(Stratosphere)!;
        Assert.Equal(new Measurement(225.6, "m", new Entered(740, "ft")), s.Height);
        Assert.Equal(new Measurement(213.4, "m", new Entered(700, "ft")), s.Drop);
        Assert.Equal(new Measurement(196.3, "km/h", new Entered(122, "mph")), s.Speed);
    }

    [Fact]
    public void A_decimal_typed_in_feet_keeps_two_decimals_of_metres()
    {
        // Swiss Bob (29045): 3.8 ft.
        var s = Map(new StatsRow { Units = "imperial", Height = 3.8, HeightMetric = 1.1582399629363 })!;
        Assert.Equal(new Measurement(1.16, "m", new Entered(3.8, "ft")), s.Height);
    }

    [Fact]
    public void Metric_figures_publish_as_typed()
    {
        // Falcon's Flight (41256).
        var s = Map(new StatsRow { Units = "metric", Height = 162, HeightMetric = 162, Speed = 250, SpeedMetric = 250, Length = 4300, LengthMetric = 4300 })!;
        Assert.Equal(new Measurement(162, "m", new Entered(162, "m")), s.Height);
        Assert.Equal(new Measurement(250, "km/h", new Entered(250, "km/h")), s.Speed);
        Assert.Equal(new Measurement(4300, "m", new Entered(4300, "m")), s.Length);
    }

    [Fact]
    public void Blank_units_are_metric_as_the_infobox_reads_them()
    {
        Assert.Equal("m", Map(new StatsRow { Height = 10, HeightMetric = 10 })!.Height!.Entered!.Unit);
    }

    [Fact]
    public void Columns_without_a_metric_twin_are_converted_by_units()
    {
        // SkyScreamer (20166): diameter 98 ft, no Diameter_Metric until the rebuild.
        var s = Map(new StatsRow { Units = "imperial", Diameter = 98, Dimension1 = 120, Dimension2 = 80 })!;
        Assert.Equal(new Measurement(29.9, "m", new Entered(98, "ft")), s.Diameter);
        Assert.Equal(new Footprint(new Measurement(36.6, "m", new Entered(120, "ft")), new Measurement(24.4, "m", new Entered(80, "ft"))),
            s.Footprint);
    }

    [Fact]
    public void Unknown_units_publish_no_measurement_and_are_reported()
    {
        var issues = new List<DataIssue>();
        var s = Map(new StatsRow { Units = "furlongs", Height = 10, HeightMetric = 0, GForce = 3.5 }, issues)!;
        Assert.Null(s.Height);
        Assert.Equal(3.5, s.GForce);
        Assert.Equal("units", Assert.Single(issues).Field);
    }

    [Fact]
    public void Zero_inversions_is_a_fact_and_published()
    {
        Assert.Equal(new Inversions(0, null), Map(Stratosphere)!.Inversions);
        Assert.Equal(new Inversions(5, 14), Map(new StatsRow { TrackInversions = 5, RiderInversions = 14 })!.Inversions);
    }

    [Theory]
    [InlineData("2:30", 150)]
    [InlineData("0:45", 45)]
    [InlineData("12:05", 725)]
    public void Durations_in_m_ss_become_seconds(string text, int seconds)
    {
        Assert.Equal(new Measurement(seconds, "s"), Map(new StatsRow { Duration = text })!.Duration);
    }

    [Theory]
    [InlineData("3 minutes")]
    [InlineData("140s")]
    [InlineData("1:28 (5 repeat rides) <br> 0:20 (1 ride)")]
    [InlineData("2:75")]
    public void Any_other_duration_is_left_out_and_reported(string text)
    {
        var issues = new List<DataIssue>();
        Assert.Null(Map(new StatsRow { Duration = text }, issues));
        Assert.Equal(new DataIssue(1, "duration", text, "not m:ss"), Assert.Single(issues));
    }

    [Theory]
    [InlineData("1400", 1400)]
    [InlineData("1,200", 1200)]
    [InlineData("12,000", 12000)]
    public void Riders_per_hour_takes_plain_numbers_with_or_without_commas(string text, int n)
    {
        Assert.Equal(n, Map(new StatsRow { RidersHour = text })!.RidersPerHour);
    }

    [Theory]
    [InlineData("600 pph")]
    [InlineData("600-800")]
    [InlineData("12 per cycle")]
    [InlineData("1,20")]
    public void Riders_per_hour_notes_and_ranges_stay_on_the_wiki(string text)
    {
        var issues = new List<DataIssue>();
        Assert.Null(Map(new StatsRow { RidersHour = text }, issues));
        Assert.Equal("riders_per_hour", Assert.Single(issues).Field);
    }

    [Fact]
    public void A_record_with_no_stats_has_no_stats_block()
    {
        Assert.Null(Map(StatsRow.Empty));
        Assert.Null(Map(new StatsRow { Units = "imperial" }));
    }

    [Fact]
    public void Floats_lose_their_binary_noise()
    {
        Assert.Equal(4.5, Map(new StatsRow { GForce = 4.4999999999 })!.GForce);
    }

    [Fact]
    public void Stats_serialise_in_the_contracts_shape()
    {
        var json = JsonSerializer.Serialize(Map(new StatsRow
        {
            Units = "imperial", Height = 105, HeightMetric = 32.003998975872, TrackInversions = 5, GForce = 4,
            Duration = "2:00", RidersHour = "1400", RidersTrain = 32, Tracks = 1,
        }), Wire.Json);
        Assert.Equal(
            """{"height":{"value":32,"unit":"m","entered":{"value":105,"unit":"ft"}},"inversions":{"track":5},"g_force":4,"duration":{"value":120,"unit":"s"},"riders_per_hour":1400,"riders_per_train":32,"tracks":1}""",
            json);
    }

    [Theory]
    [InlineData(740, 0)]
    [InlineData(456.5, 1)]
    [InlineData(3.75, 2)]
    [InlineData(84.5, 1)]
    public void Decimals_read_back_from_the_float(double v, int d)
    {
        Assert.Equal(d, StatsMapper.Decimals(v));
    }
}
