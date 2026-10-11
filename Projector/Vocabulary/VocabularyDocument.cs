using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CoasterpediaServices.Projector.Mapping;

namespace CoasterpediaServices.Projector.Vocabulary;

/// <summary>
/// What /v1/vocabularies serves (DR-010 §6.2): per published API field, each
/// allowed value as its slug, the site's label, and the attraction types whose
/// infobox offers it. Built from the same Vocabularies the pass validated
/// against, so a value reaches a record no sooner than it reaches this list.
/// </summary>
public static class VocabularyDocument
{
    /// <summary>
    /// API field → infobox param, for the vocabulary fields a slice publishes.
    /// A slice that publishes another (restraints, track_material…) adds it here.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Fields = new Dictionary<string, string>
    {
        ["status"] = "status",
    };

    /// <summary>The type vocabulary is the site's own, not TemplateData's.</summary>
    private static readonly Dictionary<string, string> TypeLabels = new()
    {
        ["coaster"] = "Roller coaster",
        ["ride"] = "Ride",
        ["water_slide"] = "Water slide",
    };

    /// <param name="types">Attraction type → its templates (ProjectorConfig.AttractionTemplates), in the order to list them.</param>
    public static JsonObject Build(Vocabularies vocab, IReadOnlyDictionary<string, string[]> types)
    {
        var doc = new JsonObject
        {
            ["type"] = new JsonArray(types.Keys
                .Select(t => (JsonNode)new JsonObject { ["value"] = t, ["label"] = TypeLabels.GetValueOrDefault(t) ?? Label(t.Replace('_', ' ')) })
                .ToArray()),
        };

        foreach (var (field, param) in Fields)
        {
            // Slug → (label, types), in order of first appearance across the types.
            var values = new Dictionary<string, (string Label, List<string> AppliesTo)>();
            foreach (var type in types.Keys)
            {
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
