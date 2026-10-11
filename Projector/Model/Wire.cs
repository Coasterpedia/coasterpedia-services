using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CoasterpediaServices.Projector.Mapping;

namespace CoasterpediaServices.Projector.Model;

/// <summary>
/// Serialisation for the edge. The edge stores what it is sent and derives
/// nothing: the record, the list summary, and the columns it filters and
/// sorts on all come from here.
/// </summary>
public static class Wire
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>UTC to the second, "2026-10-11T12:00:00Z": what every timestamp in the API looks like.</summary>
    public static string Iso(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    /// <summary>SHA-256 over the content's serialisation. Property order is the type's, so it is stable.</summary>
    public static string Hash(AttractionContent content) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(content, Json)));

    public static JsonObject Upsert(AttractionContent c, long revision, DateTimeOffset updatedAt)
    {
        var record = JsonSerializer.SerializeToNode(c, Json)!.AsObject();
        record["revision"] = revision;
        record["updated_at"] = Iso(updatedAt);

        var summary = new JsonObject
        {
            ["id"] = c.Id,
            ["type"] = c.Type,
            ["name"] = c.Name,
        };
        if (c.Status != null) summary["status"] = c.Status;
        if (c.Location.Country != null) summary["country"] = JsonSerializer.SerializeToNode(c.Location.Country, Json);
        summary["manufacturers"] = JsonSerializer.SerializeToNode(c.Manufacturers, Json);
        summary["updated_at"] = Iso(updatedAt);

        return new JsonObject
        {
            ["kind"] = "attraction",
            ["id"] = c.Id,
            ["revision"] = revision,
            ["updated_at"] = Iso(updatedAt),
            ["sort_name"] = Wikitext.SortKey(c.Name),
            ["filters"] = new JsonObject
            {
                ["type"] = c.Type,
                ["status"] = c.Status,
                ["country"] = c.Location.Country?.Code,
            },
            ["summary"] = summary,
            ["record"] = record,
        };
    }

    public static JsonObject Delete(int id, int? mergedInto) => new()
    {
        ["kind"] = "attraction",
        ["id"] = id,
        ["merged_into"] = mergedInto,
    };

    public static JsonObject Batch(DateTimeOffset asOf, IEnumerable<JsonObject> upserts, IEnumerable<JsonObject> deletes) => new()
    {
        ["as_of"] = Iso(asOf),
        ["upsert"] = new JsonArray(upserts.Cast<JsonNode>().ToArray()),
        ["delete"] = new JsonArray(deletes.Cast<JsonNode>().ToArray()),
    };
}
