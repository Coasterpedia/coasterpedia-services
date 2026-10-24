using CoasterpediaServices.Projector.Gaps;
using Xunit;

namespace CoasterpediaServices.Projector.Tests;

public class CargoGapTests
{
    // The two found on 2026-10-24: both lost their table row and their _pageData row.
    private static readonly Dictionary<string, Dictionary<int, string>> Transcluding = new()
    {
        ["Roller_Coasters"] = new() { [171] = "Gold Rusher (Six Flags Magic Mountain)", [12428] = "Wild Mouse (Primetime Carnival)" },
        ["Amusement_Parks"] = new() { [15642] = "Playland Park (USA-New York-Rye)", [2877] = "Six Flags Magic Mountain" },
    };

    [Fact]
    public void FindsMissingTableAndPageDataRows()
    {
        var stored = new Dictionary<string, HashSet<int>>
        {
            ["Roller_Coasters"] = [171],
            ["Amusement_Parks"] = [2877],
        };

        var gaps = CargoGapRepair.FindGaps(Transcluding, stored, new HashSet<int> { 171, 2877 });

        Assert.Equal([12428, 15642], gaps.Select(g => g.PageId));
        Assert.Equal("Roller_Coasters, _pageData", gaps[0].Missing);
        Assert.Equal("Amusement_Parks, _pageData", gaps[1].Missing);
    }

    [Fact]
    public void FindsAPageMissingOnlyItsPageDataRow()
    {
        var stored = new Dictionary<string, HashSet<int>>
        {
            ["Roller_Coasters"] = [171, 12428],
            ["Amusement_Parks"] = [2877, 15642],
        };

        var gap = Assert.Single(CargoGapRepair.FindGaps(Transcluding, stored, new HashSet<int> { 171, 2877, 15642 }));

        Assert.Equal(12428, gap.PageId);
        Assert.Empty(gap.MissingTables);
        Assert.Equal("_pageData", gap.Missing);
    }

    [Fact]
    public void ListsEachTableAPageIsMissingFrom()
    {
        var transcluding = new Dictionary<string, Dictionary<int, string>>
        {
            ["Rides"] = new() { [5] = "Both" },
            ["Water_Slides"] = new() { [5] = "Both" },
        };
        var stored = new Dictionary<string, HashSet<int>> { ["Rides"] = [], ["Water_Slides"] = [] };

        var gap = Assert.Single(CargoGapRepair.FindGaps(transcluding, stored, new HashSet<int> { 5 }));

        Assert.Equal(["Rides", "Water_Slides"], gap.MissingTables);
        Assert.False(gap.MissingPageData);
    }

    [Fact]
    public void NoGapsWhenEveryRowIsThere()
    {
        var stored = new Dictionary<string, HashSet<int>>
        {
            ["Roller_Coasters"] = [171, 12428],
            ["Amusement_Parks"] = [2877, 15642],
        };

        Assert.Empty(CargoGapRepair.FindGaps(Transcluding, stored, new HashSet<int> { 171, 12428, 2877, 15642 }));
    }
}
