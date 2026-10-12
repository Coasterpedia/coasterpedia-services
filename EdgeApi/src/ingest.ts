import type { Env } from './env';
import { isoSeconds, json, problem } from './http';

interface Upsert {
	kind: string;
	id: number;
	revision: number;
	updated_at: string;
	/** Absent from batches a pre-settlement Projector queued; they meant the only view there was. */
	settled?: boolean;
	settles_at?: string | null;
	sort_name: string;
	/** Attractions' metric headline stats to sort by; absent from batches queued before slice 1. */
	sort_values?: { height: number | null; speed: number | null; length: number | null };
	/** The kind's own filter columns (KINDS). */
	filters: Record<string, string | number | null | undefined>;
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

/** One allowed value of a vocabulary field, as /v1/vocabularies serves it. */
interface VocabularyValue {
	value: string;
	label: string;
	applies_to?: string[];
}

interface Batch {
	as_of: string;
	settle_after?: string;
	upsert: Upsert[];
	delete: Delete[];
	promote?: Item[];
	discard?: Item[];
	/** API field → allowed values. Only on the first batch of a pass whose vocabularies changed. */
	vocabularies?: Record<string, VocabularyValue[]>;
}

/** D1 allows 1,000 queries per invocation; each item costs three. */
const MAX_ITEMS = 300;

type Value = string | number | null;

/** Where each kind lives, and the filter and sort columns the Projector fills beyond the shared ones. */
interface KindTable {
	table: string;
	columns: string[];
	values(u: Upsert): Value[];
	check(u: Upsert): string | null;
}

const STATS = ['height', 'speed', 'length'] as const;

export const KINDS: Record<string, KindTable> = {
	attraction: {
		table: 'attractions',
		columns: ['type', 'status', 'country', ...STATS],
		values: (u) => [u.filters.type ?? null, u.filters.status ?? null, u.filters.country ?? null,
			...STATS.map((k) => u.sort_values?.[k] ?? null)],
		check: (u) => {
			if (typeof u.filters?.type !== 'string') return 'missing type';
			for (const key of STATS) {
				const v = u.sort_values?.[key];
				if (v != null && !(typeof v === 'number' && Number.isFinite(v))) return `sort_values.${key} must be a number or null`;
			}
			return null;
		},
	},
	park: {
		table: 'parks',
		columns: ['status', 'country', 'state', 'resort'],
		values: (u) => [u.filters.status ?? null, u.filters.country ?? null, u.filters.state ?? null, u.filters.resort ?? null],
		check: (u) => (u.filters.resort == null || Number.isSafeInteger(u.filters.resort) ? null : 'filters.resort must be an integer or null'),
	},
	manufacturer: {
		table: 'manufacturers',
		columns: ['status', 'country'],
		values: (u) => [u.filters.status ?? null, u.filters.country ?? null],
		check: () => null,
	},
	product: {
		table: 'products',
		columns: ['type', 'status'],
		values: (u) => [u.filters.type ?? null, u.filters.status ?? null],
		check: (u) => (typeof u.filters?.type === 'string' ? null : 'missing type'),
	},
};

/** Insert as latest, or replace the latest row; ?2 is whether it is settled too. Built once per kind. */
const UPSERT_SQL = Object.fromEntries(Object.entries(KINDS).map(([kind, k]) => {
	const shared = ['sort_name', 'revision', 'updated_at', 'settles_at', 'summary', 'record', ...k.columns];
	const params = shared.map((_, i) => `?${i + 3}`);
	return [kind, `INSERT INTO ${k.table} (id, in_latest, in_settled, ${shared.join(', ')})
		 VALUES (?1, 1, ?2, ${params.join(', ')})
		 ON CONFLICT (id, in_latest) DO UPDATE SET in_settled = ?2, ${shared.map((c, i) => `${c} = ${params[i]}`).join(', ')}`];
}));

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

	const at = isoSeconds(new Date());
	const db = env.DATA;
	const statements: D1PreparedStatement[] = [];
	const promote = batch.promote ?? [];
	const discard = batch.discard ?? [];

	for (const u of batch.upsert) {
		const { table } = KINDS[u.kind];
		const settled = u.settled ?? true;
		statements.push(
			settled
				// Both views: any older settled row goes.
				? db.prepare(`DELETE FROM ${table} WHERE id = ?1 AND in_latest = 0`).bind(u.id)
				// Latest only: a row that was both stays, as settled only.
				: db.prepare(`UPDATE ${table} SET in_latest = 0 WHERE id = ?1 AND in_latest = 1 AND in_settled = 1`).bind(u.id),
			db.prepare(UPSERT_SQL[u.kind]).bind(u.id, settled ? 1 : 0, u.sort_name, u.revision, u.updated_at,
				settled ? null : u.settles_at, JSON.stringify(u.summary), JSON.stringify(u.record), ...KINDS[u.kind].values(u)),
			db.prepare(
				`INSERT INTO changes (kind, id, op, revision, at, in_latest, in_settled) VALUES (?1, ?2, 'upsert', ?3, ?4, 1, ?5)`,
			).bind(u.kind, u.id, u.revision, at, settled ? 1 : 0),
		);
	}

	// Back to the settled content: the settled row is latest again. Guarded, so
	// a record with no settled row never loses its latest one.
	for (const d of discard) {
		const { table } = KINDS[d.kind];
		statements.push(
			db.prepare(
				`DELETE FROM ${table} WHERE id = ?1 AND in_latest = 1 AND in_settled = 0
				 AND EXISTS (SELECT 1 FROM ${table} WHERE id = ?1 AND in_latest = 0)`,
			).bind(d.id),
			db.prepare(`UPDATE ${table} SET in_latest = 1 WHERE id = ?1 AND in_latest = 0`).bind(d.id),
			db.prepare(
				`INSERT INTO changes (kind, id, op, revision, at, in_latest, in_settled)
				 SELECT ?3, id, 'upsert', revision, ?2, 1, 0 FROM ${table}
				 WHERE id = ?1 AND in_latest = 1 AND in_settled = 1`,
			).bind(d.id, at, d.kind),
		);
	}

	for (const p of promote) {
		const { table } = KINDS[p.kind];
		statements.push(
			db.prepare(`DELETE FROM ${table} WHERE id = ?1 AND in_latest = 0`).bind(p.id),
			db.prepare(`UPDATE ${table} SET in_settled = 1, settles_at = NULL WHERE id = ?1 AND in_latest = 1`).bind(p.id),
			db.prepare(
				`INSERT INTO changes (kind, id, op, revision, at, in_latest, in_settled)
				 SELECT ?3, id, 'upsert', revision, ?2, 0, 1 FROM ${table} WHERE id = ?1 AND in_latest = 1`,
			).bind(p.id, at, p.kind),
		);
	}

	// Deletions and merges reach both views at once.
	for (const d of batch.delete) {
		statements.push(db.prepare(`DELETE FROM ${KINDS[d.kind].table} WHERE id = ?1`).bind(d.id));
		if (d.merged_into != null) {
			statements.push(
				db.prepare(`INSERT OR REPLACE INTO merges (kind, old_id, new_id, at) VALUES (?4, ?1, ?2, ?3)`)
					.bind(d.id, d.merged_into, at, d.kind),
			);
		}
		statements.push(
			db.prepare(`INSERT INTO changes (kind, id, op, merged_into, at) VALUES (?4, ?1, 'delete', ?2, ?3)`)
				.bind(d.id, d.merged_into, at, d.kind),
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

	if (batch.vocabularies) {
		// Moves forward only, like data_as_of.
		statements.push(
			db.prepare(
				`INSERT INTO state (key, value) VALUES ('vocabularies', ?1)
				 ON CONFLICT (key) DO UPDATE SET value = excluded.value
				 WHERE json_extract(state.value, '$.as_of') <= json_extract(excluded.value, '$.as_of')`,
			).bind(JSON.stringify({ as_of: batch.as_of, data: batch.vocabularies })),
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
			if (!Object.hasOwn(KINDS, i?.kind)) return `Unknown kind ${String(i?.kind)}`;
			if (!Number.isInteger(i.id)) return 'id must be an integer';
		}
	}
	const count = b.upsert.length + b.delete.length + (b.promote?.length ?? 0) + (b.discard?.length ?? 0);
	if (count > MAX_ITEMS) {
		return `At most ${MAX_ITEMS} items per batch`;
	}
	for (const u of b.upsert) {
		if (!Object.hasOwn(KINDS, u?.kind)) return `Unknown kind ${String(u?.kind)}`;
		if (!Number.isInteger(u.id) || !Number.isInteger(u.revision)) return 'id and revision must be integers';
		if (typeof u.filters !== 'object' || u.filters === null || typeof u.sort_name !== 'string') return `Upsert ${u.id}: missing filters or sort_name`;
		for (const [name, v] of Object.entries(u.filters)) {
			if (v != null && typeof v !== 'string' && typeof v !== 'number') return `Upsert ${u.id}: filters.${name} must be a string, a number or null`;
		}
		const problem = KINDS[u.kind].check(u);
		if (problem) return `Upsert ${u.id}: ${problem}`;
		if (typeof u.updated_at !== 'string' || u.summary == null || u.record == null) return `Upsert ${u.id}: incomplete`;
		if (u.settled !== undefined && typeof u.settled !== 'boolean') return `Upsert ${u.id}: settled must be a boolean`;
		if (u.settled === false && (typeof u.settles_at !== 'string' || !TIMESTAMP.test(u.settles_at))) {
			return `Upsert ${u.id}: a change waiting to settle needs settles_at`;
		}
	}
	for (const d of b.delete) {
		if (!Object.hasOwn(KINDS, d?.kind)) return `Unknown kind ${String(d?.kind)}`;
		if (!Number.isInteger(d.id)) return 'id must be an integer';
	}
	return b.vocabularies === undefined ? null : validateVocabularies(b.vocabularies);
}

function validateVocabularies(v: unknown): string | null {
	if (typeof v !== 'object' || v === null || Array.isArray(v)) return 'vocabularies must be an object';
	for (const [field, values] of Object.entries(v)) {
		if (!/^[a-z][a-z_.]*$/.test(field)) return `vocabularies: bad field name ${field}`;
		if (!Array.isArray(values)) return `vocabularies.${field} must be an array`;
		for (const x of values as VocabularyValue[]) {
			if (typeof x?.value !== 'string' || x.value.length === 0 || typeof x.label !== 'string') {
				return `vocabularies.${field}: each value needs value and label`;
			}
			if (x.applies_to !== undefined && !(Array.isArray(x.applies_to) && x.applies_to.every((t) => typeof t === 'string'))) {
				return `vocabularies.${field}.${x.value}: applies_to must be strings`;
			}
		}
	}
	return null;
}
