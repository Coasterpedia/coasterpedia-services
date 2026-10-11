import type { Env } from './env';
import { json, problem } from './http';

interface Upsert {
	kind: string;
	id: number;
	revision: number;
	updated_at: string;
	sort_name: string;
	filters: { type: string; status: string | null; country: string | null };
	summary: unknown;
	record: unknown;
}

interface Delete {
	kind: string;
	id: number;
	merged_into: number | null;
}

interface Batch {
	as_of: string;
	upsert: Upsert[];
	delete: Delete[];
}

/** D1 allows 1,000 queries per invocation; each item costs two. */
const MAX_ITEMS = 400;

/**
 * POST /internal/ingest. The Projector sends only what changed, already
 * shaped. Applying a batch twice leaves the same rows (the change log gains a
 * duplicate line, which a client following it reads as a no-op).
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
	const statements: D1PreparedStatement[] = [];

	for (const u of batch.upsert) {
		statements.push(
			env.DATA.prepare(
				`INSERT INTO attractions (id, type, status, country, sort_name, revision, updated_at, summary, record)
				 VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9)
				 ON CONFLICT (id) DO UPDATE SET type = ?2, status = ?3, country = ?4, sort_name = ?5,
				   revision = ?6, updated_at = ?7, summary = ?8, record = ?9`,
			).bind(u.id, u.filters.type, u.filters.status, u.filters.country, u.sort_name, u.revision, u.updated_at,
				JSON.stringify(u.summary), JSON.stringify(u.record)),
			env.DATA.prepare(`INSERT INTO changes (kind, id, op, revision, at) VALUES ('attraction', ?1, 'upsert', ?2, ?3)`)
				.bind(u.id, u.revision, at),
		);
	}

	for (const d of batch.delete) {
		statements.push(env.DATA.prepare(`DELETE FROM attractions WHERE id = ?1`).bind(d.id));
		if (d.merged_into != null) {
			statements.push(
				env.DATA.prepare(`INSERT OR REPLACE INTO merges (kind, old_id, new_id, at) VALUES ('attraction', ?1, ?2, ?3)`)
					.bind(d.id, d.merged_into, at),
			);
		}
		statements.push(
			env.DATA.prepare(`INSERT INTO changes (kind, id, op, merged_into, at) VALUES ('attraction', ?1, 'delete', ?2, ?3)`)
				.bind(d.id, d.merged_into, at),
		);
	}

	// data_as_of only moves forward: a delayed batch from the outbox must not wind it back.
	statements.push(
		env.DATA.prepare(
			`INSERT INTO state (key, value) VALUES ('data_as_of', ?1)
			 ON CONFLICT (key) DO UPDATE SET value = MAX(value, ?1)`,
		).bind(batch.as_of),
	);

	await env.DATA.batch(statements);
	return json({ upserted: batch.upsert.length, deleted: batch.delete.length });
}

function validate(b: Batch): string | null {
	if (typeof b !== 'object' || b === null || !Array.isArray(b.upsert) || !Array.isArray(b.delete)) {
		return 'Expected { as_of, upsert: [], delete: [] }';
	}
	if (typeof b.as_of !== 'string' || !/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$/.test(b.as_of)) {
		return 'as_of must be a UTC timestamp, 2026-10-11T12:00:00Z';
	}
	if (b.upsert.length + b.delete.length > MAX_ITEMS) {
		return `At most ${MAX_ITEMS} items per batch`;
	}
	for (const u of b.upsert) {
		if (u.kind !== 'attraction') return `Unknown kind ${String(u.kind)}`;
		if (!Number.isInteger(u.id) || !Number.isInteger(u.revision)) return 'id and revision must be integers';
		if (typeof u.filters?.type !== 'string' || typeof u.sort_name !== 'string') return `Upsert ${u.id}: missing type or sort_name`;
		if (typeof u.updated_at !== 'string' || u.summary == null || u.record == null) return `Upsert ${u.id}: incomplete`;
	}
	for (const d of b.delete) {
		if (d.kind !== 'attraction') return `Unknown kind ${String(d.kind)}`;
		if (!Number.isInteger(d.id)) return 'id must be an integer';
	}
	return null;
}
