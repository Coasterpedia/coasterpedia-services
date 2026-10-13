using CoasterpediaServices.Projector.Data;
using Xunit;

namespace CoasterpediaServices.Projector.Tests;

/// <summary>
/// The reader refuses a query whose columns and properties disagree, but a map
/// key naming no property just never gets selected: that reads as null, silently.
/// </summary>
public class CargoReaderTests
{
    private static IEnumerable<string> Properties(Type type) => CargoReader.ColumnProperties(type).Select(p => p.Name);

    [Fact]
    public void Every_stat_map_key_is_a_StatsRow_property()
    {
        foreach (var columns in CargoReader.StatColumns.Values)
        {
            Assert.Empty(columns.Keys.Except(Properties(typeof(StatsRow))));
        }
    }

    [Fact]
    public void Every_variant_map_key_is_a_stat_or_a_variant_spec()
    {
        Assert.Empty(CargoReader.VariantSpecs.Except(Properties(typeof(VariantRow))));
        foreach (var columns in CargoReader.VariantColumns.Values)
        {
            Assert.Empty(columns.Keys.Except(Properties(typeof(StatsRow))).Except(CargoReader.VariantSpecs));
        }
    }

    [Fact]
    public void Every_attraction_map_key_is_an_optional_AttractionRow_property()
    {
        Assert.Empty(CargoReader.AttractionOptional.Except(Properties(typeof(AttractionRow))));
        foreach (var columns in CargoReader.AttractionColumns.Values)
        {
            Assert.Empty(columns.Keys.Except(CargoReader.AttractionOptional));
        }
    }

    [Fact]
    public void Every_capability_list_is_a_ProductRow_property()
    {
        Assert.Empty(CargoReader.ListProperties.Except(Properties(typeof(ProductRow))));
        foreach (var (_, _, _, lists) in CargoReader.ProductTables)
        {
            Assert.Empty(lists.Keys.Except(CargoReader.ListProperties));
        }
    }
}
