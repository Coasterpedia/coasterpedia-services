import { dataAsOf, FLAG, limitParam, list, viewParam, type View } from './attractions';
import type { Env } from './env';
import { badRequest, CHANGES_WINDOW_DAYS, decodeOpaque, encodeOpaque, isoSeconds, json, LICENCE, problem } from './http';

const PARAMS = new Set(['since', 'kinds', 'view', 'limit']);

/** Kinds the log records. Any other is refused: filtering by it would answer "nothing changed". */
const KINDS = new Set(['attraction']);

/**
 * Log rows one request may scan. A settled feed skips latest-only rows, so a
 * page can come back short, or empty, with has_more set.
 */
const SCAN = 1000;

/**
 * How far behind now each tier's feed runs (DR-010 §9). Free's 24 hours is the
 * DR's placeholder (§11). A tier not named here gets free's delay, so an
 * unknown tier is never real time by accident.
 */
const DELAY_HOURS: Record<string, number> = { free: 24, beta: 0, commercial: 0 };

/** The tier's feed delay in hours. The cache key carries it, since it changes the answer. */
export function delayHours(tier: string): number {
	return DELAY_HOURS[tier] ?? DELAY_HOURS.free;
}

const HOUR = 3600_000;
const TIMESTAMP = /^\d{4}-\d\d-\d\dT\d\d:\d\d(:\d\d(\.\d+)?)?(Z|[+-]\d\d:\d\d)$/;

interface ChangeRow {
	seq: number;
	kind: string;
	id: number;
	op: string;
	revision: number | null;
	merged_into: number | null;
	at: string;
}

interface Log {
	/** Changes when the database is rebuilt (migration 0004), so a cursor from the old log is refused. */
	epoch: string;
	max: number;
	/** The newest seq the prune has deleted: a cursor below it has missed changes. */
	prunedThrough: number;
	/** Changes before this time may be gone: a timestamp before it has missed changes. */
	prunedBefore: string | null;
}

async function readLog(env: Env): Promise<Log> {
	const row = await env.DATA.prepare(
		`SELECT (SELECT MAX(seq) FROM changes) AS max,
		   (SELECT value FROM state WHERE key = 'changes_epoch') AS epoch,
		   (SELECT value FROM state WHERE key = 'changes_pruned_through') AS pruned_through,
		   (SELECT value FROM state WHERE key = 'changes_pruned_before') AS pruned_before`,
	).first<{ max: number | null; epoch: string | null; pruned_through: string | null; pruned_before: string | null }>();
	return {
		epoch: row?.epoch ?? '',
		max: row?.max ?? 0,
		prunedThrough: Number(row?.pruned_through ?? 0),
		prunedBefore: row?.pruned_before ?? null,
	};
}

/**
 * The first seq recorded at or after a time, or MAX(seq) + 1 if none: a binary
 * search over seq, which reads about two rows per halving instead of scanning
 * the unindexed `at`. Exact because seq has no gaps (the prune only takes a
 * prefix) and `at` rises with it (one Projector drains its outbox in order).
 */
export const FIRST_SEQ_SQL = `WITH RECURSIVE search(n, lo, hi) AS (
	  SELECT 0, (SELECT MIN(seq) FROM changes), (SELECT MAX(seq) FROM changes) + 1
	  UNION ALL
	  SELECT n + 1,
	    CASE WHEN c.at < ?1 THEN c.seq + 1 ELSE lo END,
	    CASE WHEN c.at < ?1 THEN hi ELSE c.seq END
	  FROM search JOIN changes c ON c.seq = (lo + hi) / 2
	  WHERE lo < hi
	)
	SELECT lo AS seq FROM search ORDER BY n DESC LIMIT 1`;

export async function firstSeqFrom(env: Env, at: string): Promise<number> {
	const row = await env.DATA.prepare(FIRST_SEQ_SQL).bind(at).first<{ seq: number | null }>();
	return row?.seq ?? 1;
}

/** One page of the log, apart from running it, so tests can measure what it reads. */
export function pageQuery(after: number, upper: number, view: View, kinds: string[], limit: number): { sql: string; binds: (string | number)[] } {
	const where = ['seq > ?1', 'seq <= ?2', `${FLAG[view]} = 1`];
	if (kinds.length > 0) where.push(`kind IN (${kinds.map((_, i) => `?${i + 4}`).join(',')})`);
	return {
		sql: `SELECT seq, kind, id, op, revision, merged_into, at FROM changes WHERE ${where.join(' AND ')} ORDER BY seq LIMIT ?3`,
		binds: [after, upper, limit + 1, ...kinds],
	};
}

function expired(detail: string): Response {
	return problem(410, 'cursor-expired', 'This cursor has expired',
		`${detail} Resync from /v1/attractions, then follow /v1/changes with since set to the time the resync began.`);
}

function encodeCursor(epoch: string, seq: number): string {
	return encodeOpaque(['changes', epoch, seq]);
}

function decodeCursor(cursor: string): [string, number] {
	try {
		const parsed = decodeOpaque(cursor);
		if (Array.isArray(parsed) && parsed.length === 3 && parsed[0] === 'changes' && typeof parsed[1] === 'string'
			&& Number.isSafeInteger(parsed[2]) && parsed[2] >= 0) {
			return [parsed[1], parsed[2]];
		}
	} catch {
		// fall through
	}
	throw badRequest('cursor-invalid', 'Invalid cursor', 'Pass next_cursor from a previous /v1/changes response unchanged, or a timestamp.');
}

/**
 * GET /v1/changes (DR-010 §6, §6.3, §9). Ordered by the log, from a cursor or
 * a time. The view picks which log lines count: settled lists only changes to
 * the settled view, so a vandal edit and its revert never appear there. The
 * tier's delay hides the newest lines in both views.
 */
export async function listChanges(url: URL, env: Env, tier: string, now = new Date()): Promise<{ response: Response; rows: number }> {
	for (const name of url.searchParams.keys()) {
		if (!PARAMS.has(name)) throw badRequest('parameter-unknown', `Unknown parameter ${name}`);
	}
	const view = viewParam(url);
	const limit = limitParam(url);
	const kinds = list(url, 'kinds');
	for (const kind of kinds) {
		if (!KINDS.has(kind)) {
			throw badRequest('parameter-unavailable', `kinds=${kind} is not available yet`, 'The beta publishes attractions only.');
		}
	}
	const since = url.searchParams.get('since');
	if (since == null) {
		throw badRequest('parameter-required', 'since is required',
			'Pass the time your copy was taken (2026-10-11T12:00:00Z), then next_cursor from each response.');
	}

	const log = await readLog(env);
	let after: number;
	if (TIMESTAMP.test(since)) {
		const time = new Date(since);
		if (Number.isNaN(time.getTime())) throw badRequest('parameter-invalid', 'since is not a valid time');
		const at = isoSeconds(time);
		if (log.prunedBefore && at < log.prunedBefore) {
			return { rows: 0, response: expired(`The change log keeps ${CHANGES_WINDOW_DAYS} days, and changes before ${log.prunedBefore} are gone.`) };
		}
		after = (await firstSeqFrom(env, at)) - 1;
	} else {
		const [epoch, seq] = decodeCursor(since);
		if (epoch !== log.epoch) {
			return { rows: 0, response: expired('The change log was rebuilt since this cursor was issued.') };
		}
		if (seq < log.prunedThrough) {
			return { rows: 0, response: expired(`The change log keeps ${CHANGES_WINDOW_DAYS} days, and this cursor is older.`) };
		}
		if (seq > log.max) {
			// Only a log restored to an earlier point gets here; its next lines would reuse numbers the client has seen.
			return { rows: 0, response: expired('The change log was restored to an earlier point since this cursor was issued.') };
		}
		after = seq;
	}

	const delay = delayHours(tier);
	const visibleBefore = delay > 0 ? isoSeconds(new Date(now.getTime() - delay * HOUR)) : null;
	// The newest line this caller may see. A prefix by seq, never a filter on at,
	// so a cursor can never step over a line that becomes visible later.
	const head = visibleBefore ? (await firstSeqFrom(env, visibleBefore)) - 1 : log.max;
	const upper = Math.min(head, after + SCAN);

	const { sql, binds } = pageQuery(after, upper, view, kinds, limit);
	const { results } = await env.DATA.prepare(sql).bind(...binds).all<ChangeRow>();

	const page = results.slice(0, limit);
	const full = results.length > limit;
	// Past the last line returned when the page is full; otherwise past every line scanned.
	const next = full ? page[page.length - 1].seq : Math.max(after, upper);

	let asOf = await dataAsOf(env);
	if (visibleBefore && (asOf == null || asOf > visibleBefore)) asOf = visibleBefore;

	return {
		rows: page.length,
		response: json({
			data: page.map((r) => r.op === 'delete'
				? { kind: r.kind, id: r.id, op: r.op, merged_into: r.merged_into, at: r.at }
				: { kind: r.kind, id: r.id, op: r.op, revision: r.revision, at: r.at }),
			meta: {
				data_as_of: asOf,
				next_cursor: encodeCursor(log.epoch, next),
				has_more: full || upper < head,
				view,
				delay: `PT${delay}H`,
				licence: LICENCE,
			},
		}),
	};
}

/**
 * The daily Cron Trigger: drop log lines older than the window. Writes one row
 * per line deleted, plus the two expiry marks.
 */
export async function pruneChanges(env: Env, now = new Date()): Promise<number> {
	const before = isoSeconds(new Date(now.getTime() - CHANGES_WINDOW_DAYS * 24 * HOUR));
	const cut = await firstSeqFrom(env, before);
	// Never the newest line. Without AUTOINCREMENT (0002), SQLite numbers a new
	// row MAX(seq) + 1, so a prune that emptied the log in a quiet spell would
	// restart it at 1, under every client's cursor.
	const doomed = 'FROM changes WHERE seq < ?1 AND seq < (SELECT MAX(seq) FROM changes)';
	const db = env.DATA;
	const results = await db.batch([
		// The expiry marks share the delete's transaction, so a cursor can never
		// outlive the lines it needs.
		db.prepare(
			`INSERT INTO state (key, value) SELECT 'changes_pruned_through', m FROM (SELECT MAX(seq) AS m ${doomed}) WHERE m IS NOT NULL
			 ON CONFLICT (key) DO UPDATE SET value = MAX(CAST(value AS INTEGER), CAST(excluded.value AS INTEGER))`,
		).bind(cut),
		db.prepare(
			`INSERT INTO state (key, value) SELECT 'changes_pruned_before', ?2 WHERE EXISTS (SELECT 1 ${doomed})
			 ON CONFLICT (key) DO UPDATE SET value = MAX(value, excluded.value)`,
		).bind(cut, before),
		db.prepare(`DELETE ${doomed}`).bind(cut),
	]);
	return results[2].meta.changes;
}
