using System.Text.Json.Nodes;
using CoasterpediaServices.Projector.Data;
using CoasterpediaServices.Projector.Mapping;
using CoasterpediaServices.Projector.Model;
using CoasterpediaServices.Projector.Options;
using CoasterpediaServices.Projector.Vocabulary;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace CoasterpediaServices.Projector;

/// <summary>One record a pass read: what the diff needs, and how to send it once the settle rule has decided.</summary>
/// <param name="Upsert">(updatedAt, settlesAt) → the ingest item.</param>
public sealed record Projected(int PageId, long Revision, string Hash, Func<DateTimeOffset, DateTimeOffset?, JsonObject> Upsert);

/// <summary>
/// One kind of record the Projector publishes. It reads and maps; the diff,
/// settling, deletes and batching after that are ProjectionService's, the same
/// for every kind.
/// </summary>
public interface IKindProjection
{
    /// <summary>The wire's kind (Wire.Attraction, Wire.Park), which also names its Redis state.</summary>
    string Kind { get; }

    Task<List<Projected>> ReadAsync(MySqlConnection conn, IReadOnlyCollection<int>? pageIds, Vocabularies vocab,
        List<DataIssue> issues, CancellationToken ct);
}

public sealed class AttractionProjection : IKindProjection
{
    private readonly CargoReader _reader;
    private readonly ProjectorConfig _config;

    public AttractionProjection(CargoReader reader, IOptions<ProjectorConfig> config)
    {
        _reader = reader;
        _config = config.Value;
    }

    public string Kind => Wire.Attraction;

    public async Task<List<Projected>> ReadAsync(MySqlConnection conn, IReadOnlyCollection<int>? pageIds, Vocabularies vocab,
        List<DataIssue> issues, CancellationToken ct)
    {
        var rows = await _reader.ReadAttractionsAsync(conn, pageIds, ct);
        var resolved = await _reader.ResolveTitlesAsync(conn, AttractionMapper.LinkTargets(rows).ToList(), ct);
        var productIds = await _reader.ReadProductIdsAsync(conn, ct);
        return rows.Select(r =>
        {
            var content = AttractionMapper.Map(r, vocab, resolved, productIds, _config.PublicBaseUrl, issues);
            return new Projected(r.PageId, r.Revision, Wire.Hash(content),
                (at, settlesAt) => Wire.Upsert(content, r.Revision, at, settlesAt));
        }).ToList();
    }
}

public sealed class ParkProjection : IKindProjection
{
    private readonly CargoReader _reader;
    private readonly ProjectorConfig _config;

    public ParkProjection(CargoReader reader, IOptions<ProjectorConfig> config)
    {
        _reader = reader;
        _config = config.Value;
    }

    public string Kind => Wire.Park;

    public async Task<List<Projected>> ReadAsync(MySqlConnection conn, IReadOnlyCollection<int>? pageIds, Vocabularies vocab,
        List<DataIssue> issues, CancellationToken ct)
    {
        var rows = await _reader.ReadParksAsync(conn, pageIds, ct);
        // Resorts are read whole: a wrapper template stores them, so a park's own
        // edit never re-stores its membership. A wrapper edit reaches the park at
        // the nightly sweep, as a re-store (DR-010 §12.7).
        var members = await _reader.ReadResortMembersAsync(conn, ct);
        var resolved = await _reader.ResolveTitlesAsync(conn,
            ParkMapper.MemberTitles(members).Concat(ParkMapper.LinkTargets(rows)).Distinct().ToList(), ct);
        var resorts = ParkMapper.Resorts(members, resolved, rows.Select(r => r.PageId).ToHashSet(), issues);
        var result = new List<Projected>(rows.Count);
        var seen = new HashSet<int>();
        foreach (var r in rows)
        {
            // One row per page on 2026-10-12; a second park infobox on a page would be two records with one ID.
            if (!seen.Add(r.PageId))
            {
                issues.Add(new DataIssue(r.PageId, "page", r.PageName, "more than one park infobox; only the first is published"));
                continue;
            }

            var content = ParkMapper.Map(r, vocab, _config.PublicBaseUrl, issues, resolved, resorts.GetValueOrDefault(r.PageId));
            result.Add(new Projected(r.PageId, r.Revision, Wire.Hash(content),
                (at, settlesAt) => Wire.UpsertPark(content, r.Revision, at, settlesAt)));
        }

        return result;
    }
}

public sealed class ProductProjection : IKindProjection
{
    private readonly CargoReader _reader;
    private readonly ProjectorConfig _config;

    public ProductProjection(CargoReader reader, IOptions<ProjectorConfig> config)
    {
        _reader = reader;
        _config = config.Value;
    }

    public string Kind => Wire.Product;

    public async Task<List<Projected>> ReadAsync(MySqlConnection conn, IReadOnlyCollection<int>? pageIds, Vocabularies vocab,
        List<DataIssue> issues, CancellationToken ct)
    {
        var rows = await _reader.ReadProductsAsync(conn, pageIds, ct);
        var resolved = await _reader.ResolveTitlesAsync(conn, ProductMapper.LinkTargets(rows).ToList(), ct);
        var result = new List<Projected>(rows.Count);
        var seen = new HashSet<int>();
        foreach (var r in rows)
        {
            // One row per page and no page in two tables on 2026-10-12; a second infobox would be two records with one ID.
            if (!seen.Add(r.PageId))
            {
                issues.Add(new DataIssue(r.PageId, "page", r.PageName, "more than one product infobox; only the first is published"));
                continue;
            }

            var content = ProductMapper.Map(r, vocab, resolved, _config.PublicBaseUrl, issues);
            result.Add(new Projected(r.PageId, r.Revision, Wire.Hash(content),
                (at, settlesAt) => Wire.UpsertProduct(content, r.Revision, at, settlesAt)));
        }

        return result;
    }
}

public sealed class ManufacturerProjection : IKindProjection
{
    private readonly CargoReader _reader;
    private readonly ProjectorConfig _config;

    public ManufacturerProjection(CargoReader reader, IOptions<ProjectorConfig> config)
    {
        _reader = reader;
        _config = config.Value;
    }

    public string Kind => Wire.Manufacturer;

    public async Task<List<Projected>> ReadAsync(MySqlConnection conn, IReadOnlyCollection<int>? pageIds, Vocabularies vocab,
        List<DataIssue> issues, CancellationToken ct)
    {
        var rows = await _reader.ReadManufacturersAsync(conn, pageIds, ct);
        var resolved = await _reader.ResolveTitlesAsync(conn, ManufacturerMapper.LinkTargets(rows).Distinct().ToList(), ct);
        var result = new List<Projected>(rows.Count);
        var seen = new HashSet<int>();
        foreach (var r in rows)
        {
            // Waterfun Products held two infoboxes on 2026-10-12; the first, in page order, is the record.
            if (!seen.Add(r.PageId))
            {
                issues.Add(new DataIssue(r.PageId, "page", r.PageName, "more than one manufacturer infobox; only the first is published"));
                continue;
            }

            var content = ManufacturerMapper.Map(r, vocab, _config.PublicBaseUrl, issues, resolved);
            result.Add(new Projected(r.PageId, r.Revision, Wire.Hash(content),
                (at, settlesAt) => Wire.UpsertManufacturer(content, r.Revision, at, settlesAt)));
        }

        return result;
    }
}

public sealed class ParkCompanyProjection : IKindProjection
{
    private readonly CargoReader _reader;
    private readonly ProjectorConfig _config;

    public ParkCompanyProjection(CargoReader reader, IOptions<ProjectorConfig> config)
    {
        _reader = reader;
        _config = config.Value;
    }

    public string Kind => Wire.ParkCompany;

    public async Task<List<Projected>> ReadAsync(MySqlConnection conn, IReadOnlyCollection<int>? pageIds, Vocabularies vocab,
        List<DataIssue> issues, CancellationToken ct)
    {
        var rows = await _reader.ReadParkCompaniesAsync(conn, pageIds, ct);
        var resolved = await _reader.ResolveTitlesAsync(conn, ParkCompanyMapper.LinkTargets(rows).Distinct().ToList(), ct);
        var result = new List<Projected>(rows.Count);
        var seen = new HashSet<int>();
        foreach (var r in rows)
        {
            // One row per page on 2026-10-13 (68); a second infobox would be two records with one ID.
            if (!seen.Add(r.PageId))
            {
                issues.Add(new DataIssue(r.PageId, "page", r.PageName, "more than one park company infobox; only the first is published"));
                continue;
            }

            var content = ParkCompanyMapper.Map(r, vocab, _config.PublicBaseUrl, issues, resolved);
            result.Add(new Projected(r.PageId, r.Revision, Wire.Hash(content),
                (at, settlesAt) => Wire.UpsertParkCompany(content, r.Revision, at, settlesAt)));
        }

        return result;
    }
}
