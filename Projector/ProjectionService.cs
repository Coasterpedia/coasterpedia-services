using System.Text.Json.Nodes;
using CoasterpediaServices.Projector.Data;
using CoasterpediaServices.Projector.Mapping;
using CoasterpediaServices.Projector.Model;
using CoasterpediaServices.Projector.Options;
using CoasterpediaServices.Projector.State;
using CoasterpediaServices.Projector.Vocabulary;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoasterpediaServices.Projector;

public sealed record ProjectionResult(int Read, int Upserted, int Deleted, int Issues);

/// <summary>
/// One projection pass: read rows, map, diff against what was last pushed,
/// queue the difference. Used by the debounced event flush (some page IDs)
/// and the nightly sweep (everything).
/// </summary>
public sealed class ProjectionService
{
    /// <summary>
    /// Passes that must find a page with no attraction row before its record
    /// is deleted. Cargo deletes a page's rows and then re-inserts them on
    /// every save, so one empty read can be a save in flight.
    /// </summary>
    private const int MissingPassesBeforeDelete = 2;

    private readonly CargoReader _reader;
    private readonly VocabularySource _vocabularies;
    private readonly ProjectionState _state;
    private readonly ProjectorConfig _config;
    private readonly ILogger<ProjectionService> _logger;

    public ProjectionService(CargoReader reader, VocabularySource vocabularies, ProjectionState state,
        IOptions<ProjectorConfig> config, ILogger<ProjectionService> logger)
    {
        _reader = reader;
        _vocabularies = vocabularies;
        _state = state;
        _config = config.Value;
        _logger = logger;
    }

    /// <param name="pageIds">Pages to project, or null for a full sweep.</param>
    public async Task<ProjectionResult> ProjectAsync(IReadOnlyCollection<int>? pageIds, CancellationToken ct = default)
    {
        var vocab = await _vocabularies.GetAsync(ct);
        await using var conn = await _reader.OpenAsync(ct);

        var rows = await _reader.ReadAttractionsAsync(conn, pageIds, ct);
        var resolved = await _reader.ResolveTitlesAsync(conn, AttractionMapper.LinkTargets(rows).ToList(), ct);
        var pushed = await _state.GetPushedAsync(pageIds);

        var issues = new List<DataIssue>();
        var now = DateTimeOffset.UtcNow;
        var upserts = new List<(JsonObject Item, int Id, PushedState State)>();
        foreach (var row in rows)
        {
            var content = AttractionMapper.Map(row, vocab, resolved, _config.PublicBaseUrl, issues);
            var hash = Wire.Hash(content);
            if (pushed.TryGetValue(row.PageId, out var last) && last.Hash == hash)
            {
                continue;
            }

            upserts.Add((Wire.Upsert(content, row.Revision, now), row.PageId, new PushedState(hash, row.Revision, now)));
        }

        var found = rows.Select(r => r.PageId).ToHashSet();
        await _state.ClearMissingAsync(found);

        // Records the edge has that this pass did not find. For an event flush,
        // only the pages asked about; a sweep covers everything ever pushed.
        var absent = (pageIds ?? pushed.Keys).Where(id => !found.Contains(id) && pushed.ContainsKey(id)).ToList();
        var deletes = new List<(JsonObject Item, int Id)>();
        if (absent.Count > 0)
        {
            var pages = await _reader.ReadPageStatesAsync(conn, absent, ct);
            foreach (var id in absent)
            {
                if (!pages.TryGetValue(id, out var page))
                {
                    deletes.Add((Wire.Delete(id, null), id)); // the page is gone
                }
                else if (page.IsRedirect)
                {
                    deletes.Add((Wire.Delete(id, page.RedirectTargetId), id)); // merged into another
                }
                else if (await _state.IncrementMissingAsync(id) >= MissingPassesBeforeDelete)
                {
                    deletes.Add((Wire.Delete(id, null), id)); // no longer an attraction
                }
            }
        }

        foreach (var chunk in upserts.Chunk(_config.BatchSize))
        {
            var batch = Wire.Batch(now, chunk.Select(u => u.Item), []);
            await _state.CommitAsync(batch.ToJsonString(), chunk.ToDictionary(u => u.Id, u => u.State), []);
        }

        foreach (var chunk in deletes.Chunk(_config.BatchSize))
        {
            var batch = Wire.Batch(now, [], chunk.Select(d => d.Item));
            await _state.CommitAsync(batch.ToJsonString(), new Dictionary<int, PushedState>(), chunk.Select(d => d.Id).ToList());
        }

        if (upserts.Count == 0 && deletes.Count == 0)
        {
            // Nothing changed, but the edge's data_as_of should still say it was checked.
            await _state.CommitAsync(Wire.Batch(now, [], []).ToJsonString(), new Dictionary<int, PushedState>(), []);
        }

        await _state.SetIssuesAsync(found, issues);

        _logger.LogInformation("Projected {Scope}: {Read} read, {Upserted} changed, {Deleted} deleted, {Issues} issues",
            pageIds == null ? "everything" : $"{pageIds.Count} pages", rows.Count, upserts.Count, deletes.Count, issues.Count);
        return new ProjectionResult(rows.Count, upserts.Count, deletes.Count, issues.Count);
    }
}
