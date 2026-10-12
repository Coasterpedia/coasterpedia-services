import type { Env } from './env';
import { badRequest, CHANGES_WINDOW, decodeOpaque, encodeOpaque, json, LICENCE, problem, SCHEMA_VERSION } from './http';

/**
 * The beta answers only what the data behind it can answer. A contract
 * parameter whose field is not published yet is refused, never ignored:
 * ignoring ?park= would return every attraction and look like an answer.
 */
export const NOT_YET = new Set([
	'state', 'park', 'location_kind', 'opened_from', 'opened_to',
	'bbox', 'updated_since', 'q',
]);

const LIST_PARAMS = new Set(['type', 'status', 'country', 'manufacturer', 'product', 'variant', 'sort', 'view', 'limit', 'cursor']);
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

/** notYet: the endpoint's contract parameters whose field is not published yet. */
export function checkParams(url: URL, allowed: Set<string>, notYet: Set<string> = NOT_YET): void {
	for (const name of url.searchParams.keys()) {
		if (notYet.has(name)) {
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

/**
 * manufacturer: one manufacturer's page ID, as in an attraction's
 * manufacturers[].id. One, as the contract has it; a list would be additive.
 */
export function manufacturerParam(url: URL): number | null {
	return idParam(url, 'manufacturer');
}

/** product: one product's page ID, as in an attraction's products[].product.id. */
export function productParam(url: URL): number | null {
	return idParam(url, 'product');
}

function idParam(url: URL, name: 'manufacturer' | 'product'): number | null {
	const raw = url.searchParams.get(name);
	if (raw == null) return null;
	const n = Number(raw);
	if (!/^\d+$/.test(raw.trim()) || !Number.isSafeInteger(n) || n < 1) {
		throw badRequest('parameter-invalid', `${name} takes one ${name} ID`);
	}
	return n;
}

/**
 * The request with a merged manufacturer's or product's ID replaced by the one
 * it merged into, as /v1/manufacturers/{old} redirects: a client holding the
 * old ID would otherwise get an empty list that reads as "makes nothing".
 * Attractions link the new page once the sweep re-resolves their links.
 */
export async function followMerges(url: URL, env: Env): Promise<URL> {
	let followed = url;
	for (const kind of ['manufacturer', 'product'] as const) {
		const id = idParam(url, kind);
		if (id == null) continue;
		const merged = await env.DATA.prepare(`SELECT new_id FROM merges WHERE kind = ? AND old_id = ?`)
			.bind(kind, id).first<{ new_id: number }>();
		if (!merged) continue;
		if (followed === url) followed = new URL(url);
		followed.searchParams.set(kind, String(merged.new_id));
	}
	return followed;
}

/**
 * A filter answered from a link table, which carries every attraction filter
 * and sort column (migrations 0008, 0011), aliased m. A product drives when
 * given: its range is small (about 135 at most), so a manufacturer with it is
 * one key read per row. variant narrows a product, case-blind.
 */
export interface Link {
	table: 'attraction_manufacturers' | 'attraction_products';
	where: string[];
	binds: number[] | (number | string)[];
}

export function linkFilter(url: URL): Link | null {
	const manufacturer = manufacturerParam(url);
	const product = productParam(url);
	const variant = url.searchParams.get('variant');
	if (variant != null && (product == null || variant.trim().length === 0)) {
		throw badRequest('parameter-invalid', 'variant takes a variant name, with product',
			'A variant is a configuration of one product, so it filters only with product.');
	}

	if (product != null) {
		const where = ['m.product = ?'];
		const binds: (number | string)[] = [product];
		if (variant != null) {
			where.push('m.variant = ?');
			binds.push(variant.trim());
		}
		if (manufacturer != null) {
			where.push('EXISTS (SELECT 1 FROM attraction_manufacturers x WHERE x.id = m.id AND x.in_latest = m.in_latest AND x.manufacturer = ?)');
			binds.push(manufacturer);
		}
		return { table: 'attraction_products', where, binds };
	}
	if (manufacturer != null) {
		return { table: 'attraction_manufacturers', where: ['m.manufacturer = ?'], binds: [manufacturer] };
	}
	return null;
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

export function decodeCursor(cursor: string, sort: string): [string | number, number] {
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

export interface ListQuery {
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

	// With a manufacturer or a product, the page is read from its links, which
	// carry every filter and sort column, and joined back for the summary.
	const link = linkFilter(url);
	const c = (col: string) => (link == null ? col : `m.${col}`);

	const where: string[] = [`${c(FLAG[view])} = 1`];
	const binds: (string | number)[] = [];
	if (link != null) {
		where.push(...link.where);
		binds.push(...link.binds);
	}
	if (column !== 'sort_name') where.push(`${c(column)} IS NOT NULL`);
	const inList = (col: string, values: string[]) => {
		if (values.length === 0) return;
		where.push(`${c(col)} IN (${values.map(() => '?').join(',')})`);
		binds.push(...values);
	};
	inList('type', list(url, 'type'));
	// The status index (0010) serves one status in name order. Anywhere else
	// SQLite, which has no statistics, would still take it, read every row of
	// the status (15,000 operating) and sort them: unary + keeps it out, so a
	// stat sort walks its stat's index and several statuses the name index.
	const statuses = list(url, 'status');
	inList(link == null && (column !== 'sort_name' || statuses.length > 1) ? '+status' : 'status', statuses);
	inList('country', list(url, 'country', (v) => v.toUpperCase()));

	// A plain range on the sort column first, so D1 seeks the index to the
	// cursor. The OR form alone scanned from the top of the table on every
	// page: 2.2M rows read paging through 21,067 attractions once (2026-10-11).
	const cursor = url.searchParams.get('cursor');
	if (cursor) {
		const [v, id] = decodeCursor(cursor, sort);
		const [col, key] = [c(column), c('id')];
		where.push(desc ? `${col} <= ? AND (${col} < ? OR ${key} < ?)` : `${col} >= ? AND (${col} > ? OR ${key} > ?)`);
		binds.push(v, v, id);
	}

	const dir = desc ? 'DESC' : 'ASC';
	const from = link == null
		? 'attractions'
		: `${link.table} m JOIN attractions a ON a.id = m.id AND a.in_latest = m.in_latest`;
	const sql = `SELECT ${c('id')} AS id, ${c(column)} AS position, ${link == null ? 'summary' : 'a.summary'} FROM ${from}
		WHERE ${where.join(' AND ')}
		ORDER BY ${c(column)} ${dir}, ${c('id')} ${dir} LIMIT ?`;
	return { sql, binds: [...binds, limit + 1], limit, view, sort, column };
}

export async function listAttractions(url: URL, env: Env): Promise<{ response: Response; rows: number }> {
	return runAttractionList(listQuery(await followMerges(url, env)), env);
}

/** One page of attraction summaries, with the cursor for the next. */
export async function runAttractionList(query: ListQuery, env: Env): Promise<{ response: Response; rows: number }> {
	const { sql, binds, limit, view, sort } = query;
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
	return getRecord(url, idText, env, { kind: 'attraction', table: 'attractions', path: '/v1/attractions', noun: 'attraction' });
}

/** Where one kind's full records live, and how its problems name it. */
export interface RecordKind {
	kind: string;
	table: string;
	/** The list's path; a merged ID redirects to `${path}/{new id}`. */
	path: string;
	noun: string;
	/** What the edge adds to the stored record before it is served: one read at most. */
	decorate?: (record: Record<string, unknown>, id: number, view: View, env: Env) => Promise<void>;
}

/** One full record in the view asked for, with its settlement; the caller has checked its parameters. */
export async function getRecord(url: URL, idText: string, env: Env, k: RecordKind): Promise<{ response: Response; rows: number }> {
	const view = viewParam(url);
	const id = Number(idText);
	if (!Number.isSafeInteger(id) || id < 1) {
		return { rows: 0, response: problem(404, 'not-found', `No such ${k.noun}`) };
	}

	const { results } = await env.DATA.prepare(
		`SELECT in_latest, in_settled, revision, settles_at, record FROM ${k.table} WHERE id = ?`,
	).bind(id).all<RecordRow>();
	const latest = results.find((r) => r.in_latest === 1);
	const settled = results.find((r) => r.in_settled === 1);
	const pending = latest && latest.in_settled === 0 ? latest : undefined;

	const row = view === 'latest' ? latest : settled;
	if (row) {
		// The same in both views (DR-010 §6.3): a settled client can see the site
		// already shows something newer, a latest one that nobody has vetted it.
		const record = JSON.parse(row.record);
		if (k.decorate) await k.decorate(record, id, view, env);
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
				`This ${k.noun} has changes that have not settled yet; it appears here at ${pending.settles_at} if nothing changes. Pass view=latest to see it now.`),
		};
	}

	const merged = await env.DATA.prepare(`SELECT new_id FROM merges WHERE kind = ? AND old_id = ?`)
		.bind(k.kind, id).first<{ new_id: number }>();
	if (merged) {
		const location = new URL(`${k.path}/${merged.new_id}`, url);
		location.search = url.search;
		return { rows: 0, response: new Response(null, { status: 301, headers: { location: location.toString() } }) };
	}

	return { rows: 0, response: problem(404, 'not-found', `No such ${k.noun}`) };
}

export async function meta(url: URL, env: Env): Promise<Response> {
	checkParams(url, META_PARAMS);
	const view = viewParam(url);
	// Kept by triggers (migrations 0002, 0006, 0007, 0009); never COUNT(*) here, as this needs no key.
	const { results: counts } = await env.DATA.prepare(`SELECT key, value FROM state WHERE key IN (?1, ?2, ?3, ?4)`)
		.bind(`count_${view}`, `count_parks_${view}`, `count_manufacturers_${view}`, `count_products_${view}`)
		.all<{ key: string; value: string }>();
	const count = (key: string) => Number(counts.find((c) => c.key === key)?.value ?? 0);
	const settleAfter = await env.DATA.prepare(`SELECT value FROM state WHERE key = 'settle_after'`).first<{ value: string }>();
	return json({
		data_as_of: await dataAsOf(env),
		schema_version: SCHEMA_VERSION,
		settle_after: settleAfter?.value ?? null,
		// How long a /v1/changes cursor stays good: how long a client can be offline.
		changes_window: CHANGES_WINDOW,
		view,
		counts: {
			attractions: count(`count_${view}`),
			parks: count(`count_parks_${view}`),
			manufacturers: count(`count_manufacturers_${view}`),
			products: count(`count_products_${view}`),
		},
		licence: LICENCE,
	});
}
