using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Xml;
using CoasterpediaServices.Projector.Mapping;

namespace CoasterpediaServices.Projector.Model;

/// <summary>
/// Serialisation for the edge. The edge stores what it is sent and derives
/// nothing: the record, the list summary, and the columns it filters and
/// sorts on all come from here. The one thing it assembles is a record's
/// settlement block, from the revisions of the rows it holds for each view.
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

    /// <summary>The wire's record kinds, which are also /v1/changes' kinds and the Projector's state keys.</summary>
    public const string Attraction = "attraction";
    public const string Park = "park";
    public const string Manufacturer = "manufacturer";
    public const string Product = "product";

    /// <summary>SHA-256 over the content's serialisation. Property order is the type's, so it is stable.</summary>
    public static string Hash<T>(T content) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(content, Json)));

    /// <param name="settlesAt">Null when the content is settled as well as latest; otherwise when its pending run settles.</param>
    public static JsonObject Upsert(AttractionContent c, long revision, DateTimeOffset updatedAt, DateTimeOffset? settlesAt = null)
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
        // The edge links products[].product.id for /v1/products/{id}/installations and the product filter.
        summary["products"] = JsonSerializer.SerializeToNode(c.Products, Json);
        // The figures a list is ranked by, so a sorted page shows what it is sorted on.
        var headline = new AttractionStats { Height = c.Stats?.Height, Speed = c.Stats?.Speed, Length = c.Stats?.Length };
        if (!headline.IsEmpty) summary["stats"] = JsonSerializer.SerializeToNode(headline, Json);
        summary["updated_at"] = Iso(updatedAt);

        return new JsonObject
        {
            ["kind"] = Attraction,
            ["id"] = c.Id,
            ["revision"] = revision,
            ["updated_at"] = Iso(updatedAt),
            ["settled"] = settlesAt == null,
            ["settles_at"] = settlesAt is { } at ? Iso(at) : null,
            ["sort_name"] = Wikitext.SortKey(c.Name),
            // Metric values; null sorts nowhere, as an unknown height cannot be ranked.
            ["sort_values"] = new JsonObject
            {
                ["height"] = c.Stats?.Height?.Value,
                ["speed"] = c.Stats?.Speed?.Value,
                ["length"] = c.Stats?.Length?.Value,
            },
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

    /// <param name="settlesAt">As for an attraction.</param>
    public static JsonObject UpsertPark(ParkContent c, long revision, DateTimeOffset updatedAt, DateTimeOffset? settlesAt = null)
    {
        var record = JsonSerializer.SerializeToNode(c, Json)!.AsObject();
        record["revision"] = revision;
        record["updated_at"] = Iso(updatedAt);

        // openapi.yaml ParkSummary: what a list row needs to place and date the park.
        var summary = new JsonObject
        {
            ["id"] = c.Id,
            ["name"] = c.Name,
        };
        if (c.Status != null) summary["status"] = c.Status;
        if (c.Country != null) summary["country"] = JsonSerializer.SerializeToNode(c.Country, Json);
        if (c.State != null) summary["state"] = c.State;
        if (c.Coordinates != null) summary["coordinates"] = JsonSerializer.SerializeToNode(c.Coordinates, Json);
        if (c.Dates?.Opened != null) summary["opened"] = JsonSerializer.SerializeToNode(c.Dates.Opened, Json);
        if (c.Dates?.Closed != null) summary["closed"] = JsonSerializer.SerializeToNode(c.Dates.Closed, Json);
        if (c.Resort != null) summary["resort"] = JsonSerializer.SerializeToNode(c.Resort, Json);
        summary["updated_at"] = Iso(updatedAt);

        return new JsonObject
        {
            ["kind"] = Park,
            ["id"] = c.Id,
            ["revision"] = revision,
            ["updated_at"] = Iso(updatedAt),
            ["settled"] = settlesAt == null,
            ["settles_at"] = settlesAt is { } at ? Iso(at) : null,
            ["sort_name"] = Wikitext.SortKey(c.Name),
            ["filters"] = new JsonObject
            {
                ["status"] = c.Status,
                ["country"] = c.Country?.Code,
                ["state"] = c.State,
                ["resort"] = c.Resort?.Id,
            },
            ["summary"] = summary,
            ["record"] = record,
        };
    }

    /// <summary>
    /// The contract lists full manufacturer records (ManufacturerList), so the
    /// summary is the record as it stands in the list: everything but the
    /// settlement block, which only a single record carries.
    /// </summary>
    /// <param name="settlesAt">As for an attraction.</param>
    public static JsonObject UpsertManufacturer(ManufacturerContent c, long revision, DateTimeOffset updatedAt, DateTimeOffset? settlesAt = null)
    {
        var record = JsonSerializer.SerializeToNode(c, Json)!.AsObject();
        record["revision"] = revision;
        record["updated_at"] = Iso(updatedAt);

        return new JsonObject
        {
            ["kind"] = Manufacturer,
            ["id"] = c.Id,
            ["revision"] = revision,
            ["updated_at"] = Iso(updatedAt),
            ["settled"] = settlesAt == null,
            ["settles_at"] = settlesAt is { } at ? Iso(at) : null,
            ["sort_name"] = Wikitext.SortKey(c.Name),
            ["filters"] = new JsonObject
            {
                ["status"] = c.Status,
                ["country"] = c.Country?.Code,
            },
            ["summary"] = record.DeepClone(),
            ["record"] = record,
        };
    }

    /// <param name="settlesAt">As for an attraction.</param>
    public static JsonObject UpsertProduct(ProductContent c, long revision, DateTimeOffset updatedAt, DateTimeOffset? settlesAt = null)
    {
        var record = JsonSerializer.SerializeToNode(c, Json)!.AsObject();
        record["revision"] = revision;
        record["updated_at"] = Iso(updatedAt);

        // openapi.yaml ProductSummary. The edge links manufacturers[].id for the manufacturer filter, as for attractions.
        var summary = new JsonObject
        {
            ["id"] = c.Id,
            ["type"] = c.Type,
            ["name"] = c.Name,
        };
        if (c.Status != null) summary["status"] = c.Status;
        summary["manufacturers"] = JsonSerializer.SerializeToNode(c.Manufacturers, Json);
        if (c.Introduced != null) summary["introduced"] = JsonSerializer.SerializeToNode(c.Introduced, Json);
        if (c.Discontinued != null) summary["discontinued"] = JsonSerializer.SerializeToNode(c.Discontinued, Json);
        summary["updated_at"] = Iso(updatedAt);

        return new JsonObject
        {
            ["kind"] = Product,
            ["id"] = c.Id,
            ["revision"] = revision,
            ["updated_at"] = Iso(updatedAt),
            ["settled"] = settlesAt == null,
            ["settles_at"] = settlesAt is { } at ? Iso(at) : null,
            ["sort_name"] = Wikitext.SortKey(c.Name),
            ["filters"] = new JsonObject
            {
                ["type"] = c.Type,
                ["status"] = c.Status,
            },
            ["summary"] = summary,
            ["record"] = record,
        };
    }

    public static JsonObject Delete(int id, int? mergedInto, string kind = Attraction) => new()
    {
        ["kind"] = kind,
        ["id"] = id,
        ["merged_into"] = mergedInto,
    };

    /// <summary>The latest version has settled: it joins the settled view, replacing what was there.</summary>
    public static JsonObject Promote(int id, string kind = Attraction) => new()
    {
        ["kind"] = kind,
        ["id"] = id,
    };

    /// <summary>The content went back to the settled version: the latest view returns to it and the pending run goes.</summary>
    public static JsonObject Discard(int id, string kind = Attraction) => new()
    {
        ["kind"] = kind,
        ["id"] = id,
    };

    /// <summary>
    /// One ingest request. The edge applies upserts, discards, promotes and
    /// deletes in that order; a pass emits at most one item per record.
    /// </summary>
    /// <param name="vocabularies">/v1/vocabularies' document (VocabularyDocument), when it has changed.</param>
    public static JsonObject Batch(DateTimeOffset asOf, TimeSpan settleAfter, IEnumerable<JsonObject>? upserts = null,
        IEnumerable<JsonObject>? deletes = null, IEnumerable<JsonObject>? promotes = null, IEnumerable<JsonObject>? discards = null,
        JsonObject? vocabularies = null)
    {
        var batch = new JsonObject
        {
            ["as_of"] = Iso(asOf),
            ["settle_after"] = XmlConvert.ToString(settleAfter),
            ["upsert"] = Array(upserts),
            ["delete"] = Array(deletes),
            ["promote"] = Array(promotes),
            ["discard"] = Array(discards),
        };
        if (vocabularies != null)
        {
            batch["vocabularies"] = vocabularies.DeepClone();
        }

        return batch;
    }

    private static JsonArray Array(IEnumerable<JsonObject>? items) => new((items ?? []).Cast<JsonNode>().ToArray());
}
