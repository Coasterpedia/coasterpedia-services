import type { Env } from './env';
import { bboxParam, boxCells, boxWhere, searchMatch, searchWhere, updatedSinceParam } from './filters';
import { badRequest, CHANGES_WINDOW, decodeOpaque, encodeOpaque, json, LICENCE, problem, SCHEMA_VERSION } from './http';

/**
 * The beta answers only what the data behind it can answer. A contract
 * parameter whose field is not published yet is refused, never ignored:
 * ignoring ?park= would return every attraction and look like an answer.
 */
export const NOT_YET = new Set<string>();

const LIST_PARAMS = new Set([
	'type', 'status', 'country', 'state', 'park', 'location_kind', 'manufacturer', 'product', 'variant', 'opened_from', 'opened_to',
	'bbox', 'updated_since', 'q', 'sort', 'view', 'limit', 'cursor',
]);
const ONE_PARAMS = new Set(['view']);
const META_PARAMS = new Set(['view']);
/**
 * Sort → column. A stat sort lists only attractions that record the stat: an
 * unknown height cannot be ranked, and each has a partial index that leaves
 * the nulls out (migration 0003). opened ranks by the earliest day an opening
 * can be (0012), so "[..1989]" is left out of it the same way.
 */
const SORT_COLUMNS: Record<string, string> = {
	name: 'sort_name', height: 'height', speed: 'speed', length: 'length', opened: 'opened_earliest', updated: 'updated_at',
};

/** Sorts whose cursor holds text: a name, a day or a time. */
const TEXT_SORTS = new Set(['name', 'opened', 'updated']);

/** Columns the link tables don't carry, so a link-driven list reads them from the row joined back. */
const ROW_ONLY = new Set(['opened_earliest', 'updated_at']);

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

function idParam(url: URL, name: 'park' | 'manufacturer' | 'product'): number | null {
	const raw = url.searchParams.get(name);
	if (raw == null) return null;
	const n = Number(raw);
	if (!/^\d+$/.test(raw.trim()) || !Number.isSafeInteger(n) || n < 1) {
		throw badRequest('parameter-invalid', `${name} takes one ${name} ID`);
	}
	return n;
}

/**
 * The request with a merged park's, manufacturer's or product's ID replaced by
 * the one it merged into, as /v1/manufacturers/{old} redirects: a client
 * holding the old ID would otherwise get an empty list that reads as "makes
 * nothing". Attractions link the new page once the sweep re-resolves their links.
 */
export async function followMerges(url: URL, env: Env): Promise<URL> {
	let followed = url;
	for (const kind of ['park', 'manufacturer', 'product'] as const) {
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
 * state as the wiki spells it ("New York"): a name, not a slug. Repeated for
 * several (state=Ohio&state=Tennessee), never comma-separated, because the
 * infobox allows "Washington, D.C.".
 */
export function stateParam(url: URL): string[] {
	const values = url.searchParams.getAll('state').map((v) => v.trim());
	if (values.some((v) => v.length === 0) || values.length > 50) {
		throw badRequest('parameter-invalid', 'state takes 1 to 50 names, each as its own state= parameter');
	}
	return values;
}

/** park: one park's page ID, as in an attraction's location.park.id. Where it is now: at the park, or stored there. */
export function parkParam(url: URL): number | null {
	return idParam(url, 'park');
}

/**
 * opened_from / opened_to, years, inclusive, as days: an opening matches only
 * when it certainly falls inside, so its earliest day is on or after the
 * first and its latest on or before the last.
 */
export function openedParams(url: URL): { from: string | null; to: string | null } {
	const year = (name: string) => {
		const raw = url.searchParams.get(name);
		if (raw == null) return null;
		const n = Number(raw);
		if (!/^\d{4}$/.test(raw.trim()) || n < 1000) throw badRequest('parameter-invalid', `${name} takes a year, 1994`);
		return n;
	};
	const from = year('opened_from');
	const to = year('opened_to');
	if (from != null && to != null && from > to) throw badRequest('parameter-invalid', 'opened_from is after opened_to');
	return { from: from == null ? null : `${from}-01-01`, to: to == null ? null : `${to}-12-31` };
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
		const valueOk = TEXT_SORTS.has(field) ? typeof value === 'string' : typeof value === 'number';
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

/**
 * Where a park's list looks (/v1/parks/{id}/attractions, DR-010 §12.19):
 * current is where each attraction is now; former adds those with a past stint
 * there (history.locations) and not there now; all is both.
 */
export type Include = 'current' | 'former' | 'all';

/** The list query for a request, apart from running it, so tests can measure what it reads. */
export function listQuery(url: URL, include: Include = 'current'): ListQuery {
	checkParams(url, LIST_PARAMS);
	const view = viewParam(url);
	const limit = limitParam(url);
	const sortParam = url.searchParams.get('sort') ?? 'name';
	const desc = sortParam.startsWith('-');
	const sort = desc ? sortParam.slice(1) : sortParam;
	const column = SORT_COLUMNS[sort];
	if (!column) {
		throw badRequest('parameter-unavailable', `sort=${sortParam} is not available yet`,
			'The beta sorts by name, height, speed, length, opened or updated, each with - for descending.');
	}

	// One index drives each request, the narrowest the filters allow: a
	// manufacturer's or product's links, then a park (or its past stints), the
	// names q matches, a box, a state, the opened range, then what changed since.
	// The links carry every attraction filter and sort column but the location,
	// opened and updated ones, which are read from the row joined back (a),
	// bounded by the links.
	const link = linkFilter(url);
	const park = parkParam(url);
	const states = stateParam(url);
	const opened = openedParams(url);
	const match = searchMatch(url);
	const box = bboxParam(url);
	const since = updatedSinceParam(url);
	const pastStints = park != null && include !== 'current';
	const driver = link != null ? 'link'
		: pastStints ? 'park_history'
		: park != null ? 'park'
		: match != null ? 'search'
		: box != null ? 'bbox'
		: states.length > 0 ? 'state'
		: opened.from != null ? 'opened'
		: opened.to != null ? 'opened_latest'
		: since != null ? 'updated'
		: null;
	const c = (col: string) => (link == null ? col : `m.${col}`);
	const a = (col: string) => (link == null ? col : `a.${col}`);
	// Without statistics SQLite takes any equality's index (§12.12), so every
	// filter but the driver's is kept out of the plan with unary +.
	const off = (col: string) => (link == null ? `+${col}` : col);
	const sortCol = ROW_ONLY.has(column) ? a(column) : c(column);
	// A driver whose index doesn't hold the order sorts what it reads: the sort
	// column is kept off the plan too, or SQLite would walk it instead and read
	// the table to find the few rows the driver names.
	const sortsItself = driver === 'park_history' || driver === 'search' || driver === 'bbox'
		|| (driver === 'updated' && column !== 'updated_at')
		|| (column === 'updated_at' && driver != null && driver !== 'updated' && driver !== 'link');
	const orderCol = sortsItself ? `+${sortCol}` : sortCol;

	const where: string[] = [`${c(FLAG[view])} = 1`];
	const binds: (string | number)[] = [];
	if (link != null) {
		where.push(...link.where);
		binds.push(...link.binds);
	}
	if (column !== 'sort_name') where.push(`${orderCol} IS NOT NULL`);
	const inList = (col: string, values: (string | number)[]) => {
		if (values.length === 0) return;
		where.push(`${col} IN (${values.map(() => '?').join(',')})`);
		binds.push(...values);
	};
	const narrowed = driver != null && driver !== 'link';
	inList(narrowed ? off('type') : c('type'), list(url, 'type'));
	// The status index (0010) serves one status in name order. Anywhere else
	// SQLite, which has no statistics, would still take it, read every row of
	// the status (15,000 operating) and sort them: unary + keeps it out, so a
	// stat sort walks its stat's index and several statuses the name index.
	const statuses = list(url, 'status');
	const statusIndexed = driver == null && column === 'sort_name' && statuses.length === 1;
	inList(link != null ? c('status') : statusIndexed ? 'status' : '+status', statuses);
	inList(narrowed ? off('country') : c('country'), list(url, 'country', (v) => v.toUpperCase()));
	// Location kind is skewed as status is (14,800 at parks): its index (0012)
	// serves one kind in name order when nothing narrower drives.
	const kinds = list(url, 'location_kind');
	const kindIndexed = driver == null && column === 'sort_name' && kinds.length === 1 && !statusIndexed;
	inList(link != null ? a('location_kind') : kindIndexed ? 'location_kind' : '+location_kind', kinds);
	// The park's past stints (0014) lead the join, so each row is one key read;
	// as a filter on the row, SQLite scanned the table and tested each one.
	// former leaves out what is there now; all adds it, read from the park index
	// (the flag as a literal, so the partial index matches).
	let stints: { sql: string; binds: number[] } | null = null;
	if (driver === 'park_history') {
		const past = 'SELECT pp.id AS sid, pp.in_latest AS slatest FROM attraction_past_parks pp WHERE pp.park = ?';
		if (include === 'former') {
			stints = { sql: past, binds: [park!] };
			where.push('(+park IS NULL OR +park <> ?)');
			binds.push(park!);
		} else {
			stints = {
				sql: `SELECT x.id AS sid, x.in_latest AS slatest FROM attractions x WHERE x.park = ? AND x.${FLAG[view]} = 1 UNION ${past}`,
				binds: [park!, park!],
			};
		}
	} else if (park != null) {
		inList(driver === 'park' ? 'park' : a('park'), [park]);
	}
	if (match != null) {
		const s = searchWhere('attraction', c('id'), c('in_latest'), match);
		where.push(s.sql);
		binds.push(...s.binds);
	}
	if (box != null) {
		if (driver === 'bbox') inList('cell', boxCells(box));
		const w = boxWhere(box, link == null ? '+lat' : a('lat'), link == null ? '+lon' : a('lon'));
		where.push(w.sql);
		binds.push(...w.binds);
	}
	if (since != null) {
		where.push(`${driver === 'updated' ? 'updated_at' : link == null ? '+updated_at' : a('updated_at')} >= ?`);
		binds.push(since);
	}
	inList(driver === 'state' ? 'state' : link != null ? a('state') : '+state', states);
	// The opened bounds: earliest in range drives opened_from, latest opened_to.
	if (opened.from != null) {
		const col = driver === 'opened' ? 'opened_earliest' : link != null ? a('opened_earliest') : '+opened_earliest';
		where.push(`${col} >= ?`);
		binds.push(opened.from);
		if (opened.to != null) {
			// Earliest ≤ latest, so this bounds the seek at the other end too.
			where.push(`${col} <= ?`);
			binds.push(opened.to);
		}
	}
	if (opened.to != null) {
		const col = driver === 'opened_latest' ? 'opened_latest' : link != null ? a('opened_latest') : '+opened_latest';
		where.push(`${col} <= ?`);
		binds.push(opened.to);
	}

	// A plain range on the sort column first, so D1 seeks the index to the
	// cursor. The OR form alone scanned from the top of the table on every
	// page: 2.2M rows read paging through 21,067 attractions once (2026-10-11).
	const cursor = url.searchParams.get('cursor');
	if (cursor) {
		const [v, id] = decodeCursor(cursor, sort);
		const key = c('id');
		where.push(desc ? `${orderCol} <= ? AND (${orderCol} < ? OR ${key} < ?)` : `${orderCol} >= ? AND (${orderCol} > ? OR ${key} > ?)`);
		binds.push(v, v, id);
	}

	const dir = desc ? 'DESC' : 'ASC';
	// The opened range is forced onto its index: SQLite can't tell a narrow
	// range from a wide one, and walking the name index for a rare decade reads
	// the table.
	const forced = driver === 'opened' ? ` INDEXED BY attractions_${view}_by_opened`
		: driver === 'opened_latest' ? ` INDEXED BY attractions_${view}_by_opened_latest`
		: driver === 'bbox' ? ` INDEXED BY attractions_${view}_by_cell`
		: driver === 'updated' ? ` INDEXED BY attractions_${view}_by_updated`
		: '';
	const from = stints != null
		? `(${stints.sql}) s CROSS JOIN attractions ON attractions.id = s.sid AND attractions.in_latest = s.slatest`
		: link == null
			? `attractions${forced}`
			: `${link.table} m JOIN attractions a ON a.id = m.id AND a.in_latest = m.in_latest`;
	const sql = `SELECT ${c('id')} AS id, ${sortCol} AS position, ${link == null ? 'summary' : 'a.summary'} FROM ${from}
		WHERE ${where.join(' AND ')}
		ORDER BY ${orderCol} ${dir}, ${c('id')} ${dir} LIMIT ?`;
	return { sql, binds: [...(stints?.binds ?? []), ...binds, limit + 1], limit, view, sort, column };
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

/** ids: the most a call names. Each costs three rows read at most, so a call's cost is bounded however it's used. */
export const MAX_IDS = 50;

/** Merges followed from a requested ID: a merge into a page that merged again, and so on. */
const MERGE_HOPS = 3;

/**
 * ids: record IDs, comma-separated, each once, in the order given. It takes no
 * filter, sort or cursor, which choose rows of the list: ids names them.
 * @param noun  what the IDs are, for the problem ("attraction", "park")
 */
export function idsParam(url: URL, noun = 'attraction'): number[] {
	for (const name of url.searchParams.keys()) {
		if (name !== 'ids' && name !== 'view') {
			throw badRequest('parameter-invalid', `ids can't be used with ${name}`,
				`ids fetches the ${noun}s you name, so filters, sort, limit and cursor have nothing to choose from. Use view only.`);
		}
	}
	const raw = url.searchParams.getAll('ids').join(',').split(',').map((v) => v.trim()).filter((v) => v.length > 0);
	const ids = [...new Set(raw.map(Number))];
	if (raw.length === 0 || raw.some((v) => !/^\d+$/.test(v)) || ids.some((n) => !Number.isSafeInteger(n) || n < 1)
		|| ids.length > MAX_IDS) {
		throw badRequest('parameter-invalid', `ids takes 1 to ${MAX_IDS} ${noun} IDs, separated by commas`);
	}
	return ids;
}

/** Both views' rows for some records of one kind: one primary-key seek per ID (cost.test.ts). */
export function idsQuery(ids: number[], table = 'attractions'): { sql: string; binds: number[] } {
	return {
		sql: `SELECT id, in_latest, in_settled, revision, settles_at, record FROM ${table} WHERE id IN (${ids.map(() => '?').join(',')})`,
		binds: ids,
	};
}

export const ATTRACTION: RecordKind = { kind: 'attraction', table: 'attractions', path: '/v1/attractions', noun: 'attraction' };

/** GET /v1/attractions?ids= (DR-010 §9, §12.24). */
export async function getAttractions(url: URL, env: Env): Promise<{ response: Response; rows: number }> {
	return getByIds(url, env, ATTRACTION);
}

/**
 * ?ids= on a kind's list: the full records of up to 50, as `${path}/{id}`
 * serves each. A merged ID gives the record it merged into, once however many
 * IDs lead there, and meta.merged says which. An ID with nothing in the view is
 * in meta.missing, or in meta.not_settled when only the latest view has it yet;
 * neither fails the call.
 */
export async function getByIds(url: URL, env: Env, k: RecordKind): Promise<{ response: Response; rows: number }> {
	const view = viewParam(url);
	const ids = idsParam(url, k.noun);

	const rowsById = new Map<number, RecordRow[]>();
	const read = async (wanted: number[]) => {
		const { sql, binds } = idsQuery(wanted, k.table);
		const { results } = await env.DATA.prepare(sql).bind(...binds).all<RecordRow & { id: number }>();
		for (const r of results) rowsById.set(r.id, [...(rowsById.get(r.id) ?? []), r]);
	};
	await read(ids);

	// Where each requested ID ends up: itself, or the record a merge leads to.
	const target = new Map(ids.map((id) => [id, id]));
	for (let hop = 0; hop < MERGE_HOPS; hop++) {
		const unresolved = ids.filter((id) => !rowsById.has(target.get(id)!));
		if (unresolved.length === 0) break;
		const from = [...new Set(unresolved.map((id) => target.get(id)!))];
		const { results: merges } = await env.DATA.prepare(
			`SELECT old_id, new_id FROM merges WHERE kind = ? AND old_id IN (${from.map(() => '?').join(',')})`,
		).bind(k.kind, ...from).all<{ old_id: number; new_id: number }>();
		if (merges.length === 0) break;
		const next = new Map(merges.map((m) => [m.old_id, m.new_id]));
		for (const id of unresolved) {
			const to = next.get(target.get(id)!);
			if (to != null) target.set(id, to);
		}
		const unread = [...new Set(merges.map((m) => m.new_id))].filter((id) => !rowsById.has(id));
		if (unread.length > 0) await read(unread);
	}

	const data: Record<string, unknown>[] = [];
	const served = new Set<number>();
	const merged: Record<string, number> = {};
	const missing: number[] = [];
	const notSettled: number[] = [];
	for (const id of ids) {
		const to = target.get(id)!;
		const { record, pending } = inView(rowsById.get(to) ?? [], view);
		if (!record) {
			(pending ? notSettled : missing).push(id);
			continue;
		}
		if (to !== id) merged[id] = to;
		if (!served.has(to)) {
			served.add(to);
			data.push(record);
		}
	}
	if (k.decorateAll) await k.decorateAll(data, view, env);
	else if (k.decorate) for (const record of data) await k.decorate(record, record.id as number, view, env);

	return {
		rows: data.length,
		response: json({
			data,
			meta: { data_as_of: await dataAsOf(env), view, licence: LICENCE, merged, missing, not_settled: notSettled },
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
	return getRecord(url, idText, env, ATTRACTION);
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
	/** decorate for a batch (?ids=), sharing its reads, so 50 records don't cost 50 of them. */
	decorateAll?: (records: Record<string, unknown>[], view: View, env: Env) => Promise<void>;
}

/**
 * One record's rows → the record the view serves, with its settlement, which
 * reads the same in both views (DR-010 §6.3): a settled client can see the site
 * already shows something newer, a latest one that nobody has vetted it.
 * pending is the latest row when it hasn't settled, so a settled miss can say
 * when it will.
 */
function inView(rows: RecordRow[], view: View): { record: Record<string, unknown> | null; pending?: RecordRow } {
	const latest = rows.find((r) => r.in_latest === 1);
	const settled = rows.find((r) => r.in_settled === 1);
	const pending = latest && latest.in_settled === 0 ? latest : undefined;
	const row = view === 'latest' ? latest : settled;
	if (!row) return { record: null, pending };
	const record = JSON.parse(row.record);
	record.settlement = {
		settled_revision: settled?.revision ?? null,
		pending_revision: pending?.revision ?? null,
		settles_at: pending?.settles_at ?? null,
	};
	return { record, pending };
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
	const { record, pending } = inView(results, view);

	if (record) {
		if (k.decorate) await k.decorate(record, id, view, env);
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
	// Kept by triggers (migrations 0002, 0006, 0007, 0009, 0013); never COUNT(*) here, as this needs no key.
	const { results: counts } = await env.DATA.prepare(`SELECT key, value FROM state WHERE key IN (?1, ?2, ?3, ?4, ?5)`)
		.bind(`count_${view}`, `count_parks_${view}`, `count_manufacturers_${view}`, `count_products_${view}`,
			`count_park_companies_${view}`)
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
			park_companies: count(`count_park_companies_${view}`),
		},
		licence: LICENCE,
	});
}
