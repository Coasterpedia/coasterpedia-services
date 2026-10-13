using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CoasterpediaServices.Projector.Mapping;

namespace CoasterpediaServices.Projector.Vocabulary;

/// <summary>
/// What /v1/vocabularies serves (DR-010 §6.2): per published API field, each
/// allowed value as its slug, the site's label, and the attraction types (and
/// "park", "manufacturer") whose infobox offers it. Built from the same
/// Vocabularies the pass validated against, so a value reaches a record no
/// sooner than it reaches this list.
/// </summary>
public static class VocabularyDocument
{
    /// <summary>
    /// Where one vocabulary field's values come from: the infobox param on each
    /// kind that publishes the field. A kind left out doesn't publish it, so
    /// applies_to never says it does.
    /// </summary>
    public sealed record Source(IReadOnlyDictionary<string, string>? ByKind, string? Param = null)
    {
        /// <summary>The same param on every kind.</summary>
        public static Source Everywhere(string param) => new(null, param);

        public string? ParamFor(string kind) => ByKind == null ? Param : ByKind.GetValueOrDefault(kind);
    }

    /// <summary>
    /// API field → its source, for the vocabulary fields a slice publishes.
    /// The attraction and product infoboxes name some params differently
    /// (track_material, track_material1).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, Source> Fields = new Dictionary<string, Source>
    {
        ["status"] = Source.Everywhere("status"),
        ["restraints"] = new(new Dictionary<string, string>
        {
            ["coaster"] = "restraints1", ["ride"] = "restraints1", [Product] = ProductMapper.OfferedParams["restraints"],
        }),
        ["propulsion"] = new(new Dictionary<string, string> { ["coaster"] = "class" }),
        ["track_material"] = new(new Dictionary<string, string>
        {
            ["coaster"] = "track_material", [Product] = ProductMapper.OfferedParams["track_material"],
        }),
        ["support_material"] = new(new Dictionary<string, string>
        {
            ["coaster"] = "support_material", [Product] = ProductMapper.OfferedParams["support_material"],
        }),
        ["classes"] = new(new Dictionary<string, string>
        {
            ["water_slide"] = "class1", [Product] = ProductMapper.OfferedParams["classes"],
        }),
        ["environment"] = new(new Dictionary<string, string> { ["water_slide"] = "environment" }),
        ["containment"] = new(new Dictionary<string, string>
        {
            ["water_slide"] = "containment", [Product] = ProductMapper.OfferedParams["containment"],
        }),
        ["exit"] = new(new Dictionary<string, string> { ["water_slide"] = "exit" }),
        ["slide_material"] = new(new Dictionary<string, string> { [Product] = ProductMapper.OfferedParams["slide_material"] }),
    };

    private const string Product = ProductMapper.VocabType;

    /// <summary>The type vocabulary is the site's own, not TemplateData's.</summary>
    private static readonly Dictionary<string, string> TypeLabels = new()
    {
        ["coaster"] = "Roller coaster",
        ["ride"] = "Ride",
        ["water_slide"] = "Water slide",
    };

    /// <param name="types">Attraction type → its templates (ProjectorConfig.AttractionTemplates), in the order to list them.</param>
    /// <param name="others">Other kinds whose infobox shares a field ("park", "manufacturer", "product"): listed in applies_to after the types, never as a type.</param>
    public static JsonObject Build(Vocabularies vocab, IReadOnlyDictionary<string, string[]> types, IEnumerable<string>? others = null)
    {
        var appliesTo = types.Keys.Concat(others ?? []).ToList();
        var doc = new JsonObject
        {
            ["type"] = new JsonArray(types.Keys
                .Select(t => (JsonNode)new JsonObject { ["value"] = t, ["label"] = TypeLabels.GetValueOrDefault(t) ?? Label(t.Replace('_', ' ')) })
                .ToArray()),
        };

        foreach (var (field, source) in Fields)
        {
            // Slug → (label, types), in order of first appearance across the types.
            var values = new Dictionary<string, (string Label, List<string> AppliesTo)>();
            foreach (var type in appliesTo)
            {
                if (source.ParamFor(type) is not { } param)
                {
                    continue;
                }

                foreach (var wiki in vocab.Ordered(type, param))
                {
                    var slug = Wikitext.Slug(wiki);
                    if (!values.TryGetValue(slug, out var entry))
                    {
                        values[slug] = entry = (Label(wiki), []);
                    }

                    entry.AppliesTo.Add(type);
                }
            }

            if (values.Count == 0)
            {
                // No kind passed offers it: not a vocabulary of this document.
                continue;
            }

            doc[field] = new JsonArray(values
                .Select(v => (JsonNode)new JsonObject
                {
                    ["value"] = v.Key,
                    ["label"] = v.Value.Label,
                    ["applies_to"] = new JsonArray(v.Value.AppliesTo.Select(t => (JsonNode)t).ToArray()),
                })
                .ToArray());
        }

        return doc;
    }

    public static string Hash(JsonObject doc) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(doc.ToJsonString())));

    /// <summary>Sentence case, as Module:Status shows a status: "standing but not operating" → "Standing but not operating".</summary>
    private static string Label(string wiki) => wiki.Length == 0 ? wiki : char.ToUpperInvariant(wiki[0]) + wiki[1..];
}
