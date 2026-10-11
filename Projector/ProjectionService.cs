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
        var sweep = pageIds == null;
        var vocab = await _vocabularies.GetAsync(ct);
        await using var conn = await _reader.OpenAsync(ct);

        var rows = await _reader.ReadAttractionsAsync(conn, pageIds, ct);
        var resolved = await _reader.ResolveTitlesAsync(conn, AttractionMapper.LinkTargets(rows).ToList(), ct);
        var states = await _state.GetStatesAsync(pageIds);

        var issues = new List<DataIssue>();
        var now = DateTimeOffset.UtcNow;
        var mapped = rows.Select(r => (Row: r, Content: AttractionMapper.Map(r, vocab, resolved, _config.PublicBaseUrl, issues)))
            .Select(m => (m.Row, m.Content, Hash: Wire.Hash(m.Content)))
            .ToList();

        // Trust only matters where something moved: new content, or a newer revision.
        var trust = await _state.GetTrustAsync(mapped
            .Where(m => !states.TryGetValue(m.Row.PageId, out var last) || last.Hash != m.Hash || last.Revision != m.Row.Revision)
            .Select(m => m.Row.PageId)
            .ToList());

        var upserts = new List<(JsonObject Item, int Id, RecordState State)>();
        var discards = new List<(JsonObject Item, int Id, RecordState State)>();
        var absorbed = new Dictionary<int, RecordState>();
        foreach (var (row, content, hash) in mapped)
        {
            var pageTrust = trust.GetValueOrDefault(row.PageId, PageTrust.None);
            states.TryGetValue(row.PageId, out var last);
            if (last != null && last.Hash == hash)
            {
                if (Settling.CanAbsorb(last, row.Revision, pageTrust, sweep))
                {
                    absorbed[row.PageId] = last with { Revision = row.Revision };
                }

                continue;
            }

            var trusted = Settling.IsTrusted(last, row.Revision, pageTrust);
            var (next, op) = Settling.Decide(last, hash, row.Revision, trusted, now);
            if (op == SettleOp.Discard)
            {
                discards.Add((Wire.Discard(row.PageId), row.PageId, next));
            }
            else
            {
                var settlesAt = op == SettleOp.Pending ? next.ClockFrom + _config.SettleAfter : null;
                upserts.Add((Wire.Upsert(content, row.Revision, now, settlesAt), row.PageId, next));
            }
        }

        var found = rows.Select(r => r.PageId).ToHashSet();
        await _state.ClearMissingAsync(found);

        // Records the edge has that this pass did not find. For an event flush,
        // only the pages asked about; a sweep covers everything ever pushed.
        var absent = (pageIds ?? states.Keys).Where(id => !found.Contains(id) && states.ContainsKey(id)).ToList();
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
                else if (page.Namespace != 0)
                {
                    // Moved out (draftified in patrol). A save never changes the
                    // namespace, so this needs no second pass.
                    deletes.Add((Wire.Delete(id, null), id));
                }
                else if (await _state.IncrementMissingAsync(id) >= MissingPassesBeforeDelete)
                {
                    deletes.Add((Wire.Delete(id, null), id)); // no longer an attraction
                }
            }
        }

        // The vocabulary this pass validated against rides on its first batch, so a
        // value never reaches a record before /v1/vocabularies lists it. Every pass
        // queues at least one batch, so a change always goes out.
        var vocabulary = VocabularyDocument.Build(vocab, _config.AttractionTemplates);
        var vocabularyHash = VocabularyDocument.Hash(vocabulary);
        var unsent = await _state.GetPushedVocabularyAsync() == vocabularyHash ? null : vocabulary;
        JsonObject NewBatch(IEnumerable<JsonObject>? upsert = null, IEnumerable<JsonObject>? delete = null,
            IEnumerable<JsonObject>? discard = null)
        {
            var batch = Wire.Batch(now, _config.SettleAfter, upsert, delete, discards: discard, vocabularies: unsent);
            unsent = null;
            return batch;
        }

        foreach (var chunk in upserts.Chunk(_config.BatchSize))
        {
            var batch = NewBatch(upsert: chunk.Select(u => u.Item));
            await _state.CommitAsync(batch.ToJsonString(), chunk.ToDictionary(u => u.Id, u => u.State), []);
        }

        foreach (var chunk in discards.Chunk(_config.BatchSize))
        {
            var batch = NewBatch(discard: chunk.Select(d => d.Item));
            await _state.CommitAsync(batch.ToJsonString(), chunk.ToDictionary(d => d.Id, d => d.State), []);
        }

        foreach (var chunk in deletes.Chunk(_config.BatchSize))
        {
            var batch = NewBatch(delete: chunk.Select(d => d.Item));
            await _state.CommitAsync(batch.ToJsonString(), new Dictionary<int, RecordState>(), chunk.Select(d => d.Id).ToList());
        }

        if (upserts.Count == 0 && discards.Count == 0 && deletes.Count == 0)
        {
            // Nothing changed, but the edge's data_as_of should still say it was checked.
            await _state.CommitAsync(NewBatch().ToJsonString(), new Dictionary<int, RecordState>(), []);
        }

        if (unsent == null)
        {
            await _state.SetPushedVocabularyAsync(vocabularyHash);
        }

        await _state.SaveAsync(absorbed);
        await _state.SetIssuesAsync(found, issues);

        var pending = upserts.Count(u => u.State.Pending);
        _logger.LogInformation(
            "Projected {Scope}: {Read} read, {Upserted} changed ({Pending} pending), {Discarded} reverted to settled, {Deleted} deleted, {Issues} issues",
            sweep ? "everything" : $"{pageIds!.Count} pages", rows.Count, upserts.Count, pending, discards.Count, deletes.Count,
            issues.Count);
        return new ProjectionResult(rows.Count, upserts.Count, deletes.Count, issues.Count);
    }

    /// <summary>
    /// Promote every pending run whose clock has run out (DR-010 §6.3). Hourly,
    /// on the Projector's queue, so it never races a flush or a sweep.
    /// </summary>
    /// <returns>Records promoted.</returns>
    public async Task<int> SettleDueAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var states = await _state.GetStatesAsync(await _state.GetDueAsync(now - _config.SettleAfter));
        var due = states
            .Where(s => Settling.IsDue(s.Value, _config.SettleAfter, now))
            .Select(s => (Id: s.Key, State: s.Value with { SettledHash = s.Value.Hash, ClockFrom = null }))
            .ToList();

        foreach (var chunk in due.Chunk(_config.BatchSize))
        {
            var batch = Wire.Batch(now, _config.SettleAfter, promotes: chunk.Select(d => Wire.Promote(d.Id)));
            await _state.CommitAsync(batch.ToJsonString(), chunk.ToDictionary(d => d.Id, d => d.State), []);
        }

        if (due.Count > 0)
        {
            _logger.LogInformation("Settled {Count} records", due.Count);
        }

        return due.Count;
    }
}
