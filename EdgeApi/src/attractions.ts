import type { Env } from './env';
import { badRequest, CHANGES_WINDOW, decodeOpaque, encodeOpaque, json, LICENCE, problem, SCHEMA_VERSION } from './http';

/**
 * The beta answers only what the data behind it can answer. A contract
 * parameter whose field is not published yet is refused, never ignored:
 * ignoring ?park= would return every attraction and look like an answer.
 */
const NOT_YET = new Set([
	'state', 'park', 'manufacturer', 'product', 'location_kind', 'opened_from', 'opened_to',
	'bbox', 'updated_since', 'q',
]);

const LIST_PARAMS = new Set(['type', 'status', 'country', 'sort', 'view', 'limit', 'cursor']);
const ONE_PARAMS = new Set(['view']);
const META_PARAMS = new Set(['view']);
/**
 * Sort → column. A stat sort lists only attractions that record the stat: an
 * unknown height cannot be ranked, and each has a partial index that leaves
 * the nulls out (migration 0003).
 */
const SORT_COLUMNS: Record<string, string> = { name: 'sort_name', height: 'height', speed: 'speed', length: 'length' };

interface Row {
	id: number;
	position: string | number;
	summary: string;
}

export async function dataAsOf(env: Env): Promise<string | null> {
	const row = await env.DATA.prepare(`SELECT value FROM state WHERE key = 'data_as_of'`).first<{ value: string }>();
	return row?.value ?? null;
}

export type View = 'latest' | 'settled';

/**
 * view (DR-010 §6.3): settled unless the client asks for latest. The view
 * picks the row flag every query filters on, written into the SQL as a literal
 * so the partial indexes match.
 */
export function viewParam(url: URL): View {
	const view = url.searchParams.get('view') ?? 'settled';
	if (view === 'latest' || view === 'settled') return view;
	throw badRequest('parameter-invalid', 'view is latest or settled');
}

export const FLAG: Record<View, string> = { latest: 'in_latest', settled: 'in_settled' };

function checkParams(url: URL, allowed: Set<string>): void {
	for (const name of url.searchParams.keys()) {
		if (NOT_YET.has(name)) {
			throw badRequest('parameter-unavailable', `${name} is not available yet`,
				`The field behind ${name} is not published in this beta, so filtering by it would return the wrong set.`);
		}
		if (!allowed.has(name)) {
			throw badRequest('parameter-unknown', `Unknown parameter ${name}`);
		}
	}
}

/** Comma-separated values; vocabulary is open (DR-010 §6.2), so any value is a valid filter. */
export function list(url: URL, name: string, transform: (v: string) => string = (v) => v): string[] {
	const raw = url.searchParams.get(name);
	if (raw == null) return [];
	const values = raw.split(',').map((v) => transform(v.trim())).filter((v) => v.length > 0);
	if (values.length === 0 || values.length > 50) {
		throw badRequest('parameter-invalid', `${name} takes 1 to 50 comma-separated values`);
	}
	return values;
}

export function limitParam(url: URL): number {
	const raw = url.searchParams.get('limit');
	if (raw == null) return 25;
	const n = Number(raw);
	if (!Number.isInteger(n) || n < 1 || n > 100) {
		throw badRequest('parameter-invalid', 'limit is an integer from 1 to 100');
	}
	return n;
}

// The cursor is the sort and the last row's position in it. Opaque to clients.
export function encodeCursor(sort: string, value: string | number, id: number): string {
	return encodeOpaque([sort, value, id]);
}

function decodeCursor(cursor: string, sort: string): [string | number, number] {
	try {
		const parsed = decodeOpaque(cursor) as any[];
		// Two-part cursors came from before stat sorts, when name was the only sort.
		const [field, value, id] = parsed.length === 2 ? ['name', ...parsed] : parsed;
		const valueOk = field === 'name' ? typeof value === 'string' : typeof value === 'number';
		if (field === sort && valueOk && Number.isInteger(id)) return [value, id];
	} catch {
		// fall through
	}
	throw badRequest('cursor-invalid', 'Invalid cursor', 'Pass next_cursor from a previous response unchanged, with the same sort.');
}

interface ListQuery {
	sql: string;
	binds: (string | number)[];
	limit: number;
	view: View;
	sort: string;
	column: string;
}

/** The list query for a request, apart from running it, so tests can measure what it reads. */
export function listQuery(url: URL): ListQuery {
	checkParams(url, LIST_PARAMS);
	const view = viewParam(url);
	const limit = limitParam(url);
	const sortParam = url.searchParams.get('sort') ?? 'name';
	const desc = sortParam.startsWith('-');
	const sort = desc ? sortParam.slice(1) : sortParam;
	const column = SORT_COLUMNS[sort];
	if (!column) {
		throw badRequest('parameter-unavailable', `sort=${sortParam} is not available yet`,
			'The beta sorts by name, height, speed or length, each with - for descending.');
	}

	const where: string[] = [`${FLAG[view]} = 1`];
	if (column !== 'sort_name') where.push(`${column} IS NOT NULL`);
	const binds: (string | number)[] = [];
	const inList = (col: string, values: string[]) => {
		if (values.length === 0) return;
		where.push(`${col} IN (${values.map(() => '?').join(',')})`);
		binds.push(...values);
	};
	inList('type', list(url, 'type'));
	inList('status', list(url, 'status'));
	inList('country', list(url, 'country', (v) => v.toUpperCase()));

	// A plain range on the sort column first, so D1 seeks the index to the
	// cursor. The OR form alone scanned from the top of the table on every
	// page: 2.2M rows read paging through 21,067 attractions once (2026-10-11).
	const cursor = url.searchParams.get('cursor');
	if (cursor) {
		const [v, id] = decodeCursor(cursor, sort);
		where.push(desc ? `${column} <= ? AND (${column} < ? OR id < ?)` : `${column} >= ? AND (${column} > ? OR id > ?)`);
		binds.push(v, v, id);
	}

	const dir = desc ? 'DESC' : 'ASC';
	const sql = `SELECT id, ${column} AS position, summary FROM attractions
		WHERE ${where.join(' AND ')}
		ORDER BY ${column} ${dir}, id ${dir} LIMIT ?`;
	return { sql, binds: [...binds, limit + 1], limit, view, sort, column };
}

export async function listAttractions(url: URL, env: Env): Promise<{ response: Response; rows: number }> {
	const { sql, binds, limit, view, sort } = listQuery(url);
	const { results } = await env.DATA.prepare(sql).bind(...binds).all<Row>();

	const page = results.slice(0, limit);
	const last = page[page.length - 1];
	const next = results.length > limit && last ? encodeCursor(sort, last.position, last.id) : null;

	return {
		rows: page.length,
		response: json({
			data: page.map((r) => JSON.parse(r.summary)),
			meta: { data_as_of: await dataAsOf(env), next_cursor: next, view, licence: LICENCE },
		}),
	};
}

interface RecordRow {
	in_latest: number;
	in_settled: number;
	revision: number;
	settles_at: string | null;
	record: string;
}

export async function getAttraction(url: URL, idText: string, env: Env): Promise<{ response: Response; rows: number }> {
	checkParams(url, ONE_PARAMS);
	const view = viewParam(url);
	const id = Number(idText);
	if (!Number.isSafeInteger(id) || id < 1) {
		return { rows: 0, response: problem(404, 'not-found', 'No such attraction') };
	}

	const { results } = await env.DATA.prepare(
		`SELECT in_latest, in_settled, revision, settles_at, record FROM attractions WHERE id = ?`,
	).bind(id).all<RecordRow>();
	const latest = results.find((r) => r.in_latest === 1);
	const settled = results.find((r) => r.in_settled === 1);
	const pending = latest && latest.in_settled === 0 ? latest : undefined;

	const row = view === 'latest' ? latest : settled;
	if (row) {
		// The same in both views (DR-010 §6.3): a settled client can see the site
		// already shows something newer, a latest one that nobody has vetted it.
		const record = JSON.parse(row.record);
		record.settlement = {
			settled_revision: settled?.revision ?? null,
			pending_revision: pending?.revision ?? null,
			settles_at: pending?.settles_at ?? null,
		};
		return {
			rows: 1,
			response: json({ data: record, meta: { data_as_of: await dataAsOf(env), view, licence: LICENCE } }),
		};
	}

	if (pending) {
		return {
			rows: 0,
			response: problem(404, 'not-settled', 'Not in the settled view yet',
				`This attraction has changes that have not settled yet; it appears here at ${pending.settles_at} if nothing changes. Pass view=latest to see it now.`),
		};
	}

	const merged = await env.DATA.prepare(`SELECT new_id FROM merges WHERE kind = 'attraction' AND old_id = ?`)
		.bind(id).first<{ new_id: number }>();
	if (merged) {
		const location = new URL(`/v1/attractions/${merged.new_id}`, url);
		location.search = url.search;
		return { rows: 0, response: new Response(null, { status: 301, headers: { location: location.toString() } }) };
	}

	return { rows: 0, response: problem(404, 'not-found', 'No such attraction') };
}

export async function meta(url: URL, env: Env): Promise<Response> {
	checkParams(url, META_PARAMS);
	const view = viewParam(url);
	// Kept by triggers (migration 0002); never COUNT(*) here, as this needs no key.
	const count = await env.DATA.prepare(`SELECT value AS n FROM state WHERE key = ?`)
		.bind(view === 'latest' ? 'count_latest' : 'count_settled').first<{ n: string }>();
	const settleAfter = await env.DATA.prepare(`SELECT value FROM state WHERE key = 'settle_after'`).first<{ value: string }>();
	return json({
		data_as_of: await dataAsOf(env),
		schema_version: SCHEMA_VERSION,
		settle_after: settleAfter?.value ?? null,
		// How long a /v1/changes cursor stays good: how long a client can be offline.
		changes_window: CHANGES_WINDOW,
		view,
		counts: { attractions: Number(count?.n ?? 0) },
		licence: LICENCE,
	});
}
