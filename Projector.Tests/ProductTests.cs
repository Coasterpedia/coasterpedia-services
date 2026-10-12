using System.Text.Json;
using System.Text.Json.Nodes;
using CoasterpediaServices.Projector.Data;
using CoasterpediaServices.Projector.Mapping;
using CoasterpediaServices.Projector.Model;
using CoasterpediaServices.Projector.Options;
using CoasterpediaServices.Projector.Vocabulary;
using Xunit;

namespace CoasterpediaServices.Projector.Tests;

public class ProductTests
{
    // TemplateData as the wiki served it; the three product infoboxes on 2026-10-12.
    private static readonly Vocabularies Vocab = VocabularySource.Parse(
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "templatedata.json")))!,
        new ProjectorConfig().VocabularyTemplates());

    private static readonly Dictionary<string, int> Resolved = new()
    {
        ["Vekoma"] = 548, ["Giovanola"] = 2416, ["Intamin"] = 546, ["Zamperla"] = 2399, ["A.R.M. (USA)"] = 46253,
        ["WhiteWater West"] = 55374,
    };

    // Boomerang (Vekoma product) as stored on 2026-10-12: specs in the infobox, so on the unnamed row.
    private static ProductRow Boomerang(IReadOnlyList<VariantRow>? variants = null) =>
        new()
        {
            Type = "coaster", PageId = 3213, PageName = "Boomerang (Vekoma product)", Revision = 123456, Name = "Boomerang",
            Status = "Discontinued", Manufacturer = "[[Vekoma]]", Units = "metric",
            Introduced = "1984-01-01", IntroducedPrecision = 3,
            TrackMaterial = "Steel", SupportMaterial = "Steel",
            Variants = variants ??
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
        };

    private static ProductContent Map(ProductRow row, List<DataIssue>? issues = null) =>
        ProductMapper.Map(row, Vocab, Resolved, "https://coasterpedia.net", issues ?? []);

    [Fact]
    public void The_product_infoboxes_share_one_list_of_allowed_values()
    {
        Assert.Equal("in production", Vocab.Match("product", "status", "In production"));
        // A product is offered or not; it is never operating, and proposed is not a product status.
        Assert.Null(Vocab.Match("product", "status", "operating"));
        Assert.Null(Vocab.Match("product", "status", "Proposed"));
        Assert.Equal("Shared lap-bars", Vocab.Match("product", "restraints1", "shared lap-bars"));
        Assert.Equal("Tube", Vocab.Match("product", "class1", "tube"));
    }

    [Fact]
    public void A_clean_row_maps_with_no_issues()
    {
        var issues = new List<DataIssue>();
        var c = Map(Boomerang(), issues);

        Assert.Empty(issues);
        Assert.Equal("coaster", c.Type);
        Assert.Equal("Boomerang", c.Name);
        Assert.Equal("Boomerang (Vekoma product)", c.Title);
        Assert.Equal("https://coasterpedia.net/wiki/Boomerang_(Vekoma_product)", c.Url);
        Assert.Equal("discontinued", c.Status);
        Assert.Equal([new Ref(548, "Vekoma")], c.Manufacturers);
        Assert.Equal(new DateValue("1984", "1984-01-01", "1984-12-31"), c.Introduced);
        Assert.Null(c.Discontinued);
        Assert.Equal(["steel"], c.Offered!.TrackMaterial);
        Assert.Equal(["steel"], c.Offered.SupportMaterial);
        Assert.Null(c.Offered.Restraints);
    }

    [Fact]
    public void The_infoboxs_own_specs_are_one_unnamed_variant()
    {
        var v = Assert.Single(Map(Boomerang()).Variants!);

        Assert.Null(v.Name);
        Assert.Equal(new Measurement(35.5, "m", new Entered(35.5, "m")), v.Stats!.Height);
        Assert.Equal(new Measurement(285, "m", new Entered(285, "m")), v.Stats.Length);
        Assert.Equal(new Inversions(3, 6), v.Stats.Inversions);
        Assert.Equal(new Measurement(108, "s"), v.Stats.Duration);
        Assert.Equal(760, v.Stats.RidersPerHour);
        Assert.Equal(new Footprint(new Measurement(88, "m", new Entered(88, "m")), new Measurement(30, "m", new Entered(30, "m"))),
            v.Stats.Footprint);
    }

    [Fact]
    public void The_unnamed_variant_is_written_as_null_not_left_out()
    {
        var json = JsonSerializer.Serialize(Map(Boomerang()), Wire.Json);
        Assert.Contains("\"variants\":[{\"name\":null,", json);
    }

    [Fact]
    public void An_empty_unnamed_row_is_no_variant_and_with_none_left_the_list_is_absent()
    {
        // Polercoaster: a product page with no specs at all, as 875 of 1,092 are.
        var c = Map(Boomerang([new VariantRow { PageId = 3213, RowId = 430 }]));
        Assert.Null(c.Variants);
        Assert.DoesNotContain("variants", JsonSerializer.Serialize(c, Wire.Json));
    }

    [Fact]
    public void Variant_blocks_follow_the_empty_unnamed_row_in_page_order()
    {
        // Sit Down Thrill Coaster: one variant name is a link.
        var c = Map(Boomerang(
        [
            new VariantRow
            {
                PageId = 68541, RowId = 582, Name = "Wildcat",
                Stats = new StatsRow { Units = "metric", Height = 40, HeightMetric = 40 },
            },
            new VariantRow { PageId = 68541, RowId = 574, Name = "", Group = "" },
            new VariantRow
            {
                PageId = 68541, RowId = 581, Name = "[[Top Gun Launch Coaster]]",
                Stats = new StatsRow { Units = "metric", Height = 31, HeightMetric = 31, Speed = 97, SpeedMetric = 97 },
            },
        ]));

        Assert.Equal(["Top Gun Launch Coaster", "Wildcat"], c.Variants!.Select(v => v.Name));
        Assert.Equal(97, c.Variants[0].Stats!.Speed!.Value);
    }

    [Fact]
    public void Specs_in_both_places_are_published_and_reported()
    {
        var issues = new List<DataIssue>();
        var c = Map(Boomerang(
        [
            new VariantRow { PageId = 3213, RowId = 430, Stats = new StatsRow { Height = 35.5, HeightMetric = 35.5 } },
            new VariantRow
            {
                PageId = 3213, RowId = 431, Name = "Junior",
                Stats = new StatsRow { Units = "metric", Height = 20, HeightMetric = 20 },
            },
        ]), issues);

        Assert.Equal(2, c.Variants!.Count);
        Assert.Equal("variants", Assert.Single(issues).Field);
    }

    [Fact]
    public void The_synthesised_row_reads_its_figures_in_the_pages_units()
    {
        // An imperial product's unnamed row stores no Units of its own; its Height_Metric is the converted figure.
        var row = Boomerang([new VariantRow
        {
            PageId = 3213, RowId = 430,
            Stats = new StatsRow { Height = 94.4, HeightMetric = 28.77311907926 },
        }])
            with { Units = "imperial" };

        Assert.Equal(new Measurement(28.77, "m", new Entered(94.4, "ft")), Map(row).Variants![0].Stats!.Height);
    }

    [Fact]
    public void Ride_variants_carry_diameter_from_its_metric_column_and_their_own_fields()
    {
        // Vertigo 100 (A.R.M. USA product): imperial, with Diameter_Metric stored.
        var row = new ProductRow
        {
            Type = "ride", PageId = 52106, PageName = "Vertigo (A.R.M. USA product)", Revision = 1, Name = "Vertigo",
            Status = "in production", Manufacturer = "[[A.R.M. (USA)|A.R.M.]]", Units = "imperial",
            Variants =
            [
                new VariantRow
                {
                    PageId = 52106, RowId = 1557, Name = "Vertigo 100",
                    Stats = new StatsRow
                    {
                        Units = "imperial", Height = 94.4, HeightMetric = 28.77311907926, Diameter = 76, DiameterMetric = 23.164799258726,
                    },
                    Sweep = 12, Cabins = 2,
                },
            ],
        };

        var c = Map(row);
        Assert.Equal([new Ref(46253, "A.R.M.")], c.Manufacturers);
        var stats = Assert.Single(c.Variants!).Stats!;
        // Whole feet convert to one decimal of a metre, as attraction heights do.
        Assert.Equal(new Measurement(23.2, "m", new Entered(76, "ft")), stats.Diameter);
        Assert.Equal(12, stats.Sweep);
        Assert.Equal(2, stats.Cabins);
    }

    [Fact]
    public void A_variant_duration_that_is_a_note_is_left_out_and_reported()
    {
        // Every Vertigo variant: "2:45 Maximum".
        var issues = new List<DataIssue>();
        var c = Map(Boomerang([new VariantRow
        {
            PageId = 52106, RowId = 1557, Name = "Vertigo 100",
            Stats = new StatsRow { Units = "imperial", Duration = "2:45 Maximum" },
        }]), issues);

        Assert.Null(Assert.Single(c.Variants!).Stats);
        Assert.Equal(new DataIssue(3213, "duration", "2:45 Maximum", "not m:ss"), Assert.Single(issues));
    }

    [Fact]
    public void Weights_convert_by_the_variants_units()
    {
        var row = Boomerang([new VariantRow
        {
            PageId = 1, RowId = 1, Name = "Big",
            Stats = new StatsRow { Units = "imperial" }, MaxWeight = 300, MaxWeightPerTube = 550,
        }]);

        var stats = Map(row).Variants![0].Stats!;
        Assert.Equal(new Measurement(136.1, "kg", new Entered(300, "lb")), stats.MaxWeight);
        Assert.Equal(new Measurement(249.5, "kg", new Entered(550, "lb")), stats.MaxWeightPerTube);
    }

    [Fact]
    public void Every_linked_manufacturer_is_listed_and_a_red_link_has_no_id()
    {
        // Swiss Bob: a co-build. Sombrero: a range that changed hands, the first firm without a page.
        Assert.Equal([new Ref(2416, "Giovanola"), new Ref(546, "Intamin")],
            Map(Boomerang() with { Manufacturer = "[[Giovanola]] <br> [[Intamin]]" }).Manufacturers);
        Assert.Equal([new Ref(null, "Soriani & Moser"), new Ref(2399, "Zamperla")],
            Map(Boomerang() with { Manufacturer = "[[Soriani & Moser]] (originally) <br> [[Zamperla]] (currently)" }).Manufacturers);
        Assert.Empty(Map(Boomerang() with { Manufacturer = "" }).Manufacturers);
    }

    [Fact]
    public void A_status_outside_the_product_list_is_null_and_reported()
    {
        // Polercoaster stores "Proposed".
        var issues = new List<DataIssue>();
        Assert.Null(Map(Boomerang() with { Status = "Proposed" }, issues).Status);
        Assert.Equal(new DataIssue(3213, "status", "Proposed", "not in the infobox's allowed list"), Assert.Single(issues));
    }

    [Fact]
    public void Capability_lists_are_split_checked_and_slugged_in_the_pages_order()
    {
        // AquaPlay and a Wiegand-style slide offered for body and tube, 1 to 3 riders.
        var row = new ProductRow
        {
            Type = "water_slide", PageId = 82994, PageName = "AquaPlay (WhiteWater West product)", Revision = 1,
            Name = "AquaPlay", Status = "in production", Manufacturer = "[[WhiteWater West]]", Units = "metric",
            Classes = "Tube#Body", Containment = "Partially enclosed", SlideMaterial = "Plastic", RidersTube = "1#2#3",
        };

        var o = Map(row).Offered!;
        Assert.Equal(["tube", "body"], o.Classes);
        Assert.Equal(["partially_enclosed"], o.Containment);
        Assert.Equal(["plastic"], o.SlideMaterial);
        Assert.Equal([1, 2, 3], o.RidersPerTube);
    }

    [Fact]
    public void A_hyphenated_restraint_slugs_to_underscores_as_the_contract_has_it()
    {
        // Mini Mouse (Zamperla product) and G-Force (A.R.M. USA product).
        Assert.Equal(["shared_lap_bars", "over_the_shoulder"],
            Map(Boomerang() with { Restraints = "Shared lap-bars # Over-the-shoulder" }).Offered!.Restraints);
    }

    [Fact]
    public void A_capability_outside_the_list_is_dropped_and_reported()
    {
        var issues = new List<DataIssue>();
        var o = Map(Boomerang() with { TrackMaterial = "Steel#Hybrid", RidersTube = "2#x" }, issues).Offered!;

        Assert.Equal(["steel"], o.TrackMaterial);
        Assert.Equal([2], o.RidersPerTube);
        Assert.Equal(
            [
                new DataIssue(3213, "offered.track_material", "Hybrid", "not in the infobox's allowed list"),
                new DataIssue(3213, "offered.riders_per_tube", "x", "not a positive whole number"),
            ],
            issues);
    }

    [Fact]
    public void Nothing_offered_leaves_offered_out()
    {
        Assert.Null(Map(Boomerang() with { TrackMaterial = null, SupportMaterial = "" }).Offered);
    }

    [Fact]
    public void The_product_upsert_summarises_and_filters_by_type_and_status()
    {
        var at = DateTimeOffset.Parse("2026-10-12T12:00:00Z");
        var item = Wire.UpsertProduct(Map(Boomerang([new VariantRow { PageId = 3213, RowId = 430 }])), 42, at);

        Assert.Equal(
            """{"kind":"product","id":3213,"revision":42,"updated_at":"2026-10-12T12:00:00Z","settled":true,"settles_at":null,"sort_name":"boomerang","filters":{"type":"coaster","status":"discontinued"},"summary":{"id":3213,"type":"coaster","name":"Boomerang","status":"discontinued","manufacturers":[{"id":548,"name":"Vekoma"}],"introduced":{"edtf":"1984","earliest":"1984-01-01","latest":"1984-12-31"},"updated_at":"2026-10-12T12:00:00Z"},"record":{"id":3213,"type":"coaster","name":"Boomerang","title":"Boomerang (Vekoma product)","url":"https://coasterpedia.net/wiki/Boomerang_(Vekoma_product)","status":"discontinued","manufacturers":[{"id":548,"name":"Vekoma"}],"introduced":{"edtf":"1984","earliest":"1984-01-01","latest":"1984-12-31"},"offered":{"track_material":["steel"],"support_material":["steel"]},"revision":42,"updated_at":"2026-10-12T12:00:00Z"}}""",
            JsonSerializer.Serialize(item));
    }

    [Fact]
    public void The_capability_vocabularies_apply_to_products_only()
    {
        var doc = VocabularyDocument.Build(Vocab, new ProjectorConfig().AttractionTemplates, [Wire.Park, Wire.Manufacturer, Wire.Product]);

        AppliesTo AppliesToOf(string field) => new(doc[field]!.AsArray().ToDictionary(s => s!["value"]!.GetValue<string>(),
            s => s!["applies_to"]!.AsArray().Select(t => t!.GetValue<string>()).ToList()));

        var status = AppliesToOf("status");
        Assert.Equal(["product"], status.Of("in_production"));
        Assert.Contains("product", status.Of("cancelled"));
        Assert.DoesNotContain("product", status.Of("operating"));

        // The attraction infoboxes offer restraints too, but no attraction publishes them yet.
        var restraints = AppliesToOf("restraints");
        Assert.All(restraints.Values.Values, a => Assert.Equal(["product"], a));
        Assert.Equal("Lap-bars", doc["restraints"]![0]!["label"]!.GetValue<string>());
        Assert.Equal("lap_bars", doc["restraints"]![0]!["value"]!.GetValue<string>());
        Assert.Equal(["steel", "wooden"], AppliesToOf("track_material").Values.Keys);
        Assert.Equal(["body", "mat", "tube", "sled"], AppliesToOf("classes").Values.Keys);
        Assert.Equal(["enclosed", "open", "partially_enclosed"], AppliesToOf("containment").Values.Keys);
    }

    [Fact]
    public void No_value_published_before_products_changes_slug()
    {
        // The slug rule gained hyphens with products; nothing already published held one.
        foreach (var kind in new[] { "coaster", "ride", "water_slide", "park", "manufacturer" })
        {
            Assert.DoesNotContain(Vocab.Ordered(kind, "status"), v => v.Contains('-'));
        }
    }

    private sealed record AppliesTo(Dictionary<string, List<string>> Values)
    {
        public List<string> Of(string value) => Values[value];
    }
}
