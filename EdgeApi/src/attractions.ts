import type { Env } from './env';
import { badRequest, json, LICENCE, problem, SCHEMA_VERSION } from './http';

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
const SORTS = new Set(['name', '-name']);

interface Row {
	id: number;
	sort_name: string;
	summary: string;
}

async function dataAsOf(env: Env): Promise<string | null> {
	const row = await env.DATA.prepare(`SELECT value FROM state WHERE key = 'data_as_of'`).first<{ value: string }>();
	return row?.value ?? null;
}

/**
 * view (DR-010 §6.3). The contract's default is settled, which the beta does
 * not have yet, so the beta makes clients say latest. A client written now
 * then keeps getting latest after settled becomes the default.
 */
function requireView(url: URL): string {
	const view = url.searchParams.get('view');
	if (view === 'latest') return view;
	if (view === 'settled') {
		throw badRequest('view-unavailable', 'The settled view is not available yet',
			'Pass view=latest during the beta.');
	}
	throw badRequest('view-required', 'Pass view=latest',
		'The default will become view=settled. During the beta, say view=latest so your client keeps the view it was written for.');
}

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
function list(url: URL, name: string, transform: (v: string) => string = (v) => v): string[] {
	const raw = url.searchParams.get(name);
	if (raw == null) return [];
	const values = raw.split(',').map((v) => transform(v.trim())).filter((v) => v.length > 0);
	if (values.length === 0 || values.length > 50) {
		throw badRequest('parameter-invalid', `${name} takes 1 to 50 comma-separated values`);
	}
	return values;
}

function limitParam(url: URL): number {
	const raw = url.searchParams.get('limit');
	if (raw == null) return 25;
	const n = Number(raw);
	if (!Number.isInteger(n) || n < 1 || n > 100) {
		throw badRequest('parameter-invalid', 'limit is an integer from 1 to 100');
	}
	return n;
}

// The cursor is the last row's sort position. Opaque to clients.
function encodeCursor(sortName: string, id: number): string {
	const bytes = new TextEncoder().encode(JSON.stringify([sortName, id]));
	return btoa(String.fromCharCode(...bytes)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

function decodeCursor(cursor: string): [string, number] {
	try {
		const b64 = cursor.replace(/-/g, '+').replace(/_/g, '/');
		const bytes = Uint8Array.from(atob(b64), (c) => c.charCodeAt(0));
		const [s, id] = JSON.parse(new TextDecoder().decode(bytes));
		if (typeof s === 'string' && Number.isInteger(id)) return [s, id];
	} catch {
		// fall through
	}
	throw badRequest('cursor-invalid', 'Invalid cursor', 'Pass next_cursor from a previous response unchanged.');
}

export async function listAttractions(url: URL, env: Env): Promise<{ response: Response; rows: number }> {
	checkParams(url, LIST_PARAMS);
	const view = requireView(url);
	const limit = limitParam(url);
	const sort = url.searchParams.get('sort') ?? 'name';
	if (!SORTS.has(sort)) {
		throw badRequest('parameter-unavailable', `sort=${sort} is not available yet`, 'The beta sorts by name or -name.');
	}
	const desc = sort === '-name';

	const where: string[] = [];
	const binds: (string | number)[] = [];
	const inList = (column: string, values: string[]) => {
		if (values.length === 0) return;
		where.push(`${column} IN (${values.map(() => '?').join(',')})`);
		binds.push(...values);
	};
	inList('type', list(url, 'type'));
	inList('status', list(url, 'status'));
	inList('country', list(url, 'country', (v) => v.toUpperCase()));

	const cursor = url.searchParams.get('cursor');
	if (cursor) {
		const [s, id] = decodeCursor(cursor);
		where.push(desc ? '(sort_name < ? OR (sort_name = ? AND id < ?))' : '(sort_name > ? OR (sort_name = ? AND id > ?))');
		binds.push(s, s, id);
	}

	const dir = desc ? 'DESC' : 'ASC';
	const sql = `SELECT id, sort_name, summary FROM attractions
		${where.length ? 'WHERE ' + where.join(' AND ') : ''}
		ORDER BY sort_name ${dir}, id ${dir} LIMIT ?`;
	const { results } = await env.DATA.prepare(sql).bind(...binds, limit + 1).all<Row>();

	const page = results.slice(0, limit);
	const last = page[page.length - 1];
	const next = results.length > limit && last ? encodeCursor(last.sort_name, last.id) : null;

	return {
		rows: page.length,
		response: json({
			data: page.map((r) => JSON.parse(r.summary)),
			meta: { data_as_of: await dataAsOf(env), next_cursor: next, view, licence: LICENCE },
		}),
	};
}

export async function getAttraction(url: URL, idText: string, env: Env): Promise<{ response: Response; rows: number }> {
	checkParams(url, ONE_PARAMS);
	const view = requireView(url);
	const id = Number(idText);
	if (!Number.isSafeInteger(id) || id < 1) {
		return { rows: 0, response: problem(404, 'not-found', 'No such attraction') };
	}

	const row = await env.DATA.prepare(`SELECT record FROM attractions WHERE id = ?`).bind(id).first<{ record: string }>();
	if (row) {
		return {
			rows: 1,
			response: json({ data: JSON.parse(row.record), meta: { data_as_of: await dataAsOf(env), view, licence: LICENCE } }),
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

export async function meta(env: Env): Promise<Response> {
	const count = await env.DATA.prepare(`SELECT COUNT(*) AS n FROM attractions`).first<{ n: number }>();
	return json({
		data_as_of: await dataAsOf(env),
		schema_version: SCHEMA_VERSION,
		counts: { attractions: count?.n ?? 0 },
		licence: LICENCE,
	});
}
