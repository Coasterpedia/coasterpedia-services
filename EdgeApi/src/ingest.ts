import type { Env } from './env';
import { json, problem } from './http';

interface Upsert {
	kind: string;
	id: number;
	revision: number;
	updated_at: string;
	/** Absent from batches a pre-settlement Projector queued; they meant the only view there was. */
	settled?: boolean;
	settles_at?: string | null;
	sort_name: string;
	/** Metric headline stats to sort by; absent from batches queued before slice 1. */
	sort_values?: { height: number | null; speed: number | null; length: number | null };
	filters: { type: string; status: string | null; country: string | null };
	summary: unknown;
	record: unknown;
}

interface Delete {
	kind: string;
	id: number;
	merged_into: number | null;
}

interface Item {
	kind: string;
	id: number;
}

interface Batch {
	as_of: string;
	settle_after?: string;
	upsert: Upsert[];
	delete: Delete[];
	promote?: Item[];
	discard?: Item[];
}

/** D1 allows 1,000 queries per invocation; each item costs three. */
const MAX_ITEMS = 300;

const TIMESTAMP = /^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$/;

/**
 * POST /internal/ingest. The Projector sends only what changed, already
 * shaped, and decides which view each change reaches (DR-010 §6.3); the edge
 * only moves rows between the two. Applying a batch twice leaves the same
 * rows (the change log gains a duplicate line, which a client following it
 * reads as a no-op).
 */
export async function ingest(request: Request, env: Env): Promise<Response> {
	let batch: Batch;
	try {
		batch = await request.json<Batch>();
	} catch {
		return problem(400, 'ingest', 'Body is not JSON');
	}

	const error = validate(batch);
	if (error) {
		return problem(400, 'ingest', 'Invalid batch', error);
	}

	const at = new Date().toISOString().replace(/\.\d{3}Z$/, 'Z');
	const db = env.DATA;
	const statements: D1PreparedStatement[] = [];
	const promote = batch.promote ?? [];
	const discard = batch.discard ?? [];

	for (const u of batch.upsert) {
		const settled = u.settled ?? true;
		statements.push(
			settled
				// Both views: any older settled row goes.
				? db.prepare(`DELETE FROM attractions WHERE id = ?1 AND in_latest = 0`).bind(u.id)
				// Latest only: a row that was both stays, as settled only.
				: db.prepare(`UPDATE attractions SET in_latest = 0 WHERE id = ?1 AND in_latest = 1 AND in_settled = 1`).bind(u.id),
			db.prepare(
				`INSERT INTO attractions (id, in_latest, in_settled, type, status, country, sort_name, revision, updated_at, settles_at, summary, record,
				   height, speed, length)
				 VALUES (?1, 1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14)
				 ON CONFLICT (id, in_latest) DO UPDATE SET in_settled = ?2, type = ?3, status = ?4, country = ?5, sort_name = ?6,
				   revision = ?7, updated_at = ?8, settles_at = ?9, summary = ?10, record = ?11, height = ?12, speed = ?13, length = ?14`,
			).bind(u.id, settled ? 1 : 0, u.filters.type, u.filters.status, u.filters.country, u.sort_name, u.revision,
				u.updated_at, settled ? null : u.settles_at, JSON.stringify(u.summary), JSON.stringify(u.record),
				u.sort_values?.height ?? null, u.sort_values?.speed ?? null, u.sort_values?.length ?? null),
			db.prepare(
				`INSERT INTO changes (kind, id, op, revision, at, in_latest, in_settled) VALUES ('attraction', ?1, 'upsert', ?2, ?3, 1, ?4)`,
			).bind(u.id, u.revision, at, settled ? 1 : 0),
		);
	}

	// Back to the settled content: the settled row is latest again. Guarded, so
	// a record with no settled row never loses its latest one.
	for (const d of discard) {
		statements.push(
			db.prepare(
				`DELETE FROM attractions WHERE id = ?1 AND in_latest = 1 AND in_settled = 0
				 AND EXISTS (SELECT 1 FROM attractions WHERE id = ?1 AND in_latest = 0)`,
			).bind(d.id),
			db.prepare(`UPDATE attractions SET in_latest = 1 WHERE id = ?1 AND in_latest = 0`).bind(d.id),
			db.prepare(
				`INSERT INTO changes (kind, id, op, revision, at, in_latest, in_settled)
				 SELECT 'attraction', id, 'upsert', revision, ?2, 1, 0 FROM attractions
				 WHERE id = ?1 AND in_latest = 1 AND in_settled = 1`,
			).bind(d.id, at),
		);
	}

	for (const p of promote) {
		statements.push(
			db.prepare(`DELETE FROM attractions WHERE id = ?1 AND in_latest = 0`).bind(p.id),
			db.prepare(`UPDATE attractions SET in_settled = 1, settles_at = NULL WHERE id = ?1 AND in_latest = 1`).bind(p.id),
			db.prepare(
				`INSERT INTO changes (kind, id, op, revision, at, in_latest, in_settled)
				 SELECT 'attraction', id, 'upsert', revision, ?2, 0, 1 FROM attractions WHERE id = ?1 AND in_latest = 1`,
			).bind(p.id, at),
		);
	}

	// Deletions and merges reach both views at once.
	for (const d of batch.delete) {
		statements.push(db.prepare(`DELETE FROM attractions WHERE id = ?1`).bind(d.id));
		if (d.merged_into != null) {
			statements.push(
				db.prepare(`INSERT OR REPLACE INTO merges (kind, old_id, new_id, at) VALUES ('attraction', ?1, ?2, ?3)`)
					.bind(d.id, d.merged_into, at),
			);
		}
		statements.push(
			db.prepare(`INSERT INTO changes (kind, id, op, merged_into, at) VALUES ('attraction', ?1, 'delete', ?2, ?3)`)
				.bind(d.id, d.merged_into, at),
		);
	}

	// data_as_of only moves forward: a delayed batch from the outbox must not wind it back.
	statements.push(
		db.prepare(
			`INSERT INTO state (key, value) VALUES ('data_as_of', ?1)
			 ON CONFLICT (key) DO UPDATE SET value = MAX(value, ?1)`,
		).bind(batch.as_of),
	);
	if (batch.settle_after) {
		statements.push(
			db.prepare(`INSERT INTO state (key, value) VALUES ('settle_after', ?1) ON CONFLICT (key) DO UPDATE SET value = ?1`)
				.bind(batch.settle_after),
		);
	}

	await db.batch(statements);
	return json({ upserted: batch.upsert.length, deleted: batch.delete.length, promoted: promote.length, discarded: discard.length });
}

function validate(b: Batch): string | null {
	if (typeof b !== 'object' || b === null || !Array.isArray(b.upsert) || !Array.isArray(b.delete)) {
		return 'Expected { as_of, upsert: [], delete: [] }';
	}
	if (typeof b.as_of !== 'string' || !TIMESTAMP.test(b.as_of)) {
		return 'as_of must be a UTC timestamp, 2026-10-11T12:00:00Z';
	}
	if (b.settle_after !== undefined && (typeof b.settle_after !== 'string' || !/^P/.test(b.settle_after))) {
		return 'settle_after must be an ISO 8601 duration, P7D';
	}
	for (const name of ['promote', 'discard'] as const) {
		const items = b[name];
		if (items === undefined) continue;
		if (!Array.isArray(items)) return `${name} must be an array`;
		for (const i of items) {
			if (i?.kind !== 'attraction') return `Unknown kind ${String(i?.kind)}`;
			if (!Number.isInteger(i.id)) return 'id must be an integer';
		}
	}
	const count = b.upsert.length + b.delete.length + (b.promote?.length ?? 0) + (b.discard?.length ?? 0);
	if (count > MAX_ITEMS) {
		return `At most ${MAX_ITEMS} items per batch`;
	}
	for (const u of b.upsert) {
		if (u.kind !== 'attraction') return `Unknown kind ${String(u.kind)}`;
		if (!Number.isInteger(u.id) || !Number.isInteger(u.revision)) return 'id and revision must be integers';
		if (typeof u.filters?.type !== 'string' || typeof u.sort_name !== 'string') return `Upsert ${u.id}: missing type or sort_name`;
		if (typeof u.updated_at !== 'string' || u.summary == null || u.record == null) return `Upsert ${u.id}: incomplete`;
		if (u.settled !== undefined && typeof u.settled !== 'boolean') return `Upsert ${u.id}: settled must be a boolean`;
		if (u.sort_values !== undefined) {
			for (const key of ['height', 'speed', 'length'] as const) {
				const v = u.sort_values?.[key];
				if (v !== null && !(typeof v === 'number' && Number.isFinite(v))) return `Upsert ${u.id}: sort_values.${key} must be a number or null`;
			}
		}
		if (u.settled === false && (typeof u.settles_at !== 'string' || !TIMESTAMP.test(u.settles_at))) {
			return `Upsert ${u.id}: a change waiting to settle needs settles_at`;
		}
	}
	for (const d of b.delete) {
		if (d.kind !== 'attraction') return `Unknown kind ${String(d.kind)}`;
		if (!Number.isInteger(d.id)) return 'id must be an integer';
	}
	return null;
}
