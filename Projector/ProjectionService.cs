using System.Text.Json.Nodes;
using CoasterpediaServices.Projector.Data;
using CoasterpediaServices.Projector.Model;
using CoasterpediaServices.Projector.Options;
using CoasterpediaServices.Projector.State;
using CoasterpediaServices.Projector.Vocabulary;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace CoasterpediaServices.Projector;

public sealed record ProjectionResult(int Read, int Upserted, int Deleted, int Issues);

/// <summary>
/// One projection pass: for each kind, read rows, map, diff against what was
/// last pushed, queue the difference. Used by the debounced event flush (some
/// page IDs, tried against every kind) and the nightly sweep (everything).
/// </summary>
public sealed class ProjectionService
{
    /// <summary>
    /// Passes that must find a page with no row before its record is deleted.
    /// Cargo deletes a page's rows and then re-inserts them on every save, so
    /// one empty read can be a save in flight.
    /// </summary>
    private const int MissingPassesBeforeDelete = 2;

    private readonly CargoReader _reader;
    private readonly IReadOnlyList<IKindProjection> _kinds;
    private readonly VocabularySource _vocabularies;
    private readonly ProjectionState _state;
    private readonly ProjectorConfig _config;
    private readonly ILogger<ProjectionService> _logger;

    /// <param name="kinds">In registration order: attractions first, so the vocabulary rides on their batch as before.</param>
    public ProjectionService(CargoReader reader, IEnumerable<IKindProjection> kinds, VocabularySource vocabularies,
        ProjectionState state, IOptions<ProjectorConfig> config, ILogger<ProjectionService> logger)
    {
        _reader = reader;
        _kinds = kinds.ToList();
        _vocabularies = vocabularies;
        _state = state;
        _config = config.Value;
        _logger = logger;
    }

    /// <summary>
    /// What one pass shares across its kinds. The vocabulary this pass validated
    /// against rides on its first batch, so a value never reaches a record
    /// before /v1/vocabularies lists it.
    /// </summary>
    private sealed class Pass(DateTimeOffset now, TimeSpan settleAfter, JsonObject? unsentVocabulary)
    {
        public DateTimeOffset Now => now;
        public bool VocabularySent => unsentVocabulary == null;
        public int Batches { get; private set; }

        public JsonObject NewBatch(IEnumerable<JsonObject>? upsert = null, IEnumerable<JsonObject>? delete = null,
            IEnumerable<JsonObject>? discard = null, IEnumerable<JsonObject>? promote = null, IEnumerable<JsonObject>? merge = null)
        {
            var batch = Wire.Batch(now, settleAfter, upsert, delete, promote, discard, unsentVocabulary, merge);
            unsentVocabulary = null;
            Batches++;
            return batch;
        }
    }

    /// <param name="pageIds">Pages to project, or null for a full sweep.</param>
    public async Task<ProjectionResult> ProjectAsync(IReadOnlyCollection<int>? pageIds, CancellationToken ct = default)
    {
        var vocab = await _vocabularies.GetAsync(ct);
        await using var conn = await _reader.OpenAsync(ct);

        var vocabulary = VocabularyDocument.Build(vocab, _config.AttractionTemplates, [Wire.Park, Wire.Manufacturer, Wire.Product, Wire.ParkCompany]);
        var vocabularyHash = VocabularyDocument.Hash(vocabulary);
        var pass = new Pass(DateTimeOffset.UtcNow, _config.SettleAfter,
            await _state.GetPushedVocabularyAsync() == vocabularyHash ? null : vocabulary);

        var established = await _state.GetEstablishedAsync();
        // A sweep also records every redirect to a record as a merge (MergesAsync).
        var redirects = pageIds == null ? await _reader.ReadRedirectsAsync(conn, ct) : null;
        var total = new ProjectionResult(0, 0, 0, 0);
        foreach (var kind in _kinds)
        {
            var r = await ProjectKindAsync(kind, conn, pageIds, vocab, pass,
                Settling.IsBootstrap(kind.Kind, established), redirects, ct);
            total = new ProjectionResult(total.Read + r.Read, total.Upserted + r.Upserted, total.Deleted + r.Deleted,
                total.Issues + r.Issues);
        }

        if (pass.Batches == 0)
        {
            // Nothing changed, but the edge's data_as_of should still say it was checked.
            await _state.EnqueueAsync(pass.NewBatch().ToJsonString());
        }

        if (pass.VocabularySent)
        {
            await _state.SetPushedVocabularyAsync(vocabularyHash);
        }

        return total;
    }

    private async Task<ProjectionResult> ProjectKindAsync(IKindProjection projection, MySqlConnection conn,
        IReadOnlyCollection<int>? pageIds, Vocabularies vocab, Pass pass, bool bootstrap, IReadOnlyList<RedirectRow>? redirects,
        CancellationToken ct)
    {
        var kind = projection.Kind;
        var sweep = pageIds == null;
        var issues = new List<DataIssue>();
        var projected = await projection.ReadAsync(conn, pageIds, vocab, issues, ct);
        var states = await _state.GetStatesAsync(kind, pageIds);

        // Trust only matters where something moved: new content, or a newer revision.
        var trust = await _state.GetTrustAsync(projected
            .Where(p => !states.TryGetValue(p.PageId, out var last) || last.Hash != p.Hash || last.Revision != p.Revision)
            .Select(p => p.PageId)
            .ToList());

        var upserts = new List<(JsonObject Item, int Id, RecordState State)>();
        var discards = new List<(JsonObject Item, int Id, RecordState State)>();
        var promotes = new List<(JsonObject Item, int Id, RecordState State)>();
        var absorbed = new Dictionary<int, RecordState>();
        foreach (var p in projected)
        {
            var pageTrust = trust.GetValueOrDefault(p.PageId, PageTrust.None);
            states.TryGetValue(p.PageId, out var last);
            if (last != null && last.Hash == p.Hash)
            {
                var kept = Settling.CanAbsorb(last, p.Revision, pageTrust, sweep) ? last with { Revision = p.Revision } : last;
                if (bootstrap && kept.Pending)
                {
                    promotes.Add((Wire.Promote(p.PageId, kind), p.PageId, Settling.Promote(kept)));
                }
                else if (kept != last)
                {
                    absorbed[p.PageId] = kept;
                }

                continue;
            }

            var (next, op) = bootstrap
                ? Settling.Bootstrap(p.Hash, p.Revision, pass.Now)
                : Settling.Decide(last, p.Hash, p.Revision, Settling.IsTrusted(last, p.Revision, pageTrust), pass.Now);
            if (op == SettleOp.Discard)
            {
                discards.Add((Wire.Discard(p.PageId, kind), p.PageId, next));
            }
            else
            {
                var settlesAt = op == SettleOp.Pending ? next.ClockFrom + _config.SettleAfter : null;
                upserts.Add((p.Upsert(pass.Now, settlesAt), p.PageId, next));
            }
        }

        var found = projected.Select(p => p.PageId).ToHashSet();
        await _state.ClearMissingAsync(kind, found);

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
                    deletes.Add((Wire.Delete(id, null, kind), id)); // the page is gone
                }
                else if (page.IsRedirect)
                {
                    deletes.Add((Wire.Delete(id, page.RedirectTargetId, kind), id)); // merged into another
                }
                else if (page.Namespace != 0)
                {
                    // Moved out (draftified in patrol). A save never changes the
                    // namespace, so this needs no second pass.
                    deletes.Add((Wire.Delete(id, null, kind), id));
                }
                else if (await _state.IncrementMissingAsync(kind, id) >= MissingPassesBeforeDelete)
                {
                    deletes.Add((Wire.Delete(id, null, kind), id)); // no longer this kind
                }
            }
        }

        foreach (var chunk in upserts.Chunk(_config.BatchSize))
        {
            var batch = pass.NewBatch(upsert: chunk.Select(u => u.Item));
            await _state.CommitAsync(kind, batch.ToJsonString(), chunk.ToDictionary(u => u.Id, u => u.State), []);
        }

        foreach (var chunk in discards.Chunk(_config.BatchSize))
        {
            var batch = pass.NewBatch(discard: chunk.Select(d => d.Item));
            await _state.CommitAsync(kind, batch.ToJsonString(), chunk.ToDictionary(d => d.Id, d => d.State), []);
        }

        foreach (var chunk in promotes.Chunk(_config.BatchSize))
        {
            var batch = pass.NewBatch(promote: chunk.Select(d => d.Item));
            await _state.CommitAsync(kind, batch.ToJsonString(), chunk.ToDictionary(d => d.Id, d => d.State), []);
        }

        foreach (var chunk in deletes.Chunk(_config.BatchSize))
        {
            var batch = pass.NewBatch(delete: chunk.Select(d => d.Item));
            await _state.CommitAsync(kind, batch.ToJsonString(), new Dictionary<int, RecordState>(),
                chunk.Select(d => d.Id).ToList());
        }

        var merged = redirects == null ? 0 : await MergesAsync(kind, redirects, found, pass);

        await _state.SaveAsync(kind, absorbed);
        await _state.SetIssuesAsync(kind, found, issues);
        if (sweep)
        {
            await _state.MarkEstablishedAsync(kind);
        }

        var pending = upserts.Count(u => u.State.Pending);
        _logger.LogInformation(
            "Projected {Kind}s, {Scope}{Bootstrap}: {Read} read, {Upserted} changed ({Pending} pending), {Promoted} promoted, {Discarded} reverted to settled, {Deleted} deleted, {Merged} redirects merged, {Issues} issues",
            kind, sweep ? "everything" : $"{pageIds!.Count} pages", bootstrap ? ", bootstrapping" : "", projected.Count,
            upserts.Count, pending, promotes.Count, discards.Count, deletes.Count, merged, issues.Count);
        return new ProjectionResult(projected.Count, upserts.Count, deletes.Count, issues.Count);
    }

    /// <summary>
    /// Every redirect to one of this kind's records becomes a merge, so its ID
    /// answers 301. The delete path records a merge only when it sees a record
    /// turn into a redirect; this also covers pages redirected before the API
    /// existed, and a page dropped first ({{delete}}) and redirected later.
    /// Only new or retargeted redirects are sent. A redirect that goes away
    /// keeps its merge: the 301 still leads to a live record.
    /// </summary>
    /// <returns>Merges sent.</returns>
    private async Task<int> MergesAsync(string kind, IReadOnlyList<RedirectRow> redirects, IReadOnlySet<int> records, Pass pass)
    {
        var due = DueMerges(redirects, records, await _state.GetMergesAsync(kind));
        foreach (var chunk in due.Chunk(_config.BatchSize))
        {
            var batch = pass.NewBatch(merge: chunk.Select(m => Wire.Merge(m.PageId, m.Target, kind)));
            await _state.CommitMergesAsync(kind, batch.ToJsonString(), chunk);
        }

        return due.Count;
    }

    /// <summary>Redirects to a record that the edge hasn't been sent, or was sent pointing elsewhere.</summary>
    /// <param name="pushed">Redirect ID → the target last sent.</param>
    public static List<RedirectRow> DueMerges(IEnumerable<RedirectRow> redirects, IReadOnlySet<int> records,
        IReadOnlyDictionary<int, int> pushed) =>
        redirects
            .Where(r => records.Contains(r.Target) && !records.Contains(r.PageId)
                && !(pushed.TryGetValue(r.PageId, out var target) && target == r.Target))
            .ToList();

    /// <summary>
    /// Promote every pending run whose clock has run out (DR-010 §6.3). Hourly,
    /// on the Projector's queue, so it never races a flush or a sweep.
    /// </summary>
    /// <returns>Records promoted.</returns>
    public async Task<int> SettleDueAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var promoted = 0;
        foreach (var kind in _kinds.Select(k => k.Kind))
        {
            var states = await _state.GetStatesAsync(kind, await _state.GetDueAsync(kind, now - _config.SettleAfter));
            var due = states
                .Where(s => Settling.IsDue(s.Value, _config.SettleAfter, now))
                .Select(s => (Id: s.Key, State: Settling.Promote(s.Value)))
                .ToList();

            foreach (var chunk in due.Chunk(_config.BatchSize))
            {
                var batch = Wire.Batch(now, _config.SettleAfter, promotes: chunk.Select(d => Wire.Promote(d.Id, kind)));
                await _state.CommitAsync(kind, batch.ToJsonString(), chunk.ToDictionary(d => d.Id, d => d.State), []);
            }

            if (due.Count > 0)
            {
                _logger.LogInformation("Settled {Count} {Kind}s", due.Count, kind);
            }

            promoted += due.Count;
        }

        return promoted;
    }
}
