import {
	checkParams, dataAsOf, decodeCursor, encodeCursor, FLAG, followMerges, getByIds, getRecord, limitParam, list, listQuery,
	runAttractionList, stateParam, viewParam, type Include, type RecordKind, type View,
} from './attractions';
import type { Env } from './env';
import { bboxParam, boxWhere, searchMatch, searchWhere, updatedSinceParam } from './filters';
import { badRequest, json, LICENCE, problem } from './http';

/** Contract parameters of /v1/parks not answered yet: none since DR-010 §12.19. */
const NOT_YET = new Set<string>();

const LIST_PARAMS = new Set(['status', 'country', 'state', 'resort', 'bbox', 'updated_since', 'q', 'sort', 'view', 'limit', 'cursor']);
const ONE_PARAMS = new Set(['view']);
/**
 * /v1/parks/{id}/attractions: the attractions list for the park in the path,
 * so it takes that list's filters and sorts, less the park and where a park
 * already is; include reaches back through past stints (DR-010 §12.19).
 */
const ATTRACTION_PARAMS = new Set([
	'type', 'status', 'location_kind', 'manufacturer', 'product', 'variant', 'opened_from', 'opened_to',
	'updated_since', 'q', 'include', 'sort', 'view', 'limit', 'cursor',
]);
const INCLUDE = new Set(['current', 'former', 'all']);

interface Row {
	id: number;
	position: string;
	summary: string;
}

interface ListQuery {
	sql: string;
	binds: (string | number)[];
	limit: number;
	view: View;
}

/** Resort IDs (the wrapper template's page ID, as in a park's resort.id), comma-separated. */
function resorts(url: URL): number[] {
	return list(url, 'resort').map((v) => {
		const n = Number(v);
		if (!Number.isSafeInteger(n) || n < 1) throw badRequest('parameter-invalid', 'resort takes resort IDs, comma-separated');
		return n;
	});
}

/** The list query for a request, apart from running it, so tests can measure what it reads. */
export function parkListQuery(url: URL): ListQuery {
	checkParams(url, LIST_PARAMS, NOT_YET);
	const view = viewParam(url);
	const limit = limitParam(url);
	const sortParam = url.searchParams.get('sort') ?? 'name';
	if (sortParam !== 'name' && sortParam !== '-name') {
		throw badRequest('parameter-unavailable', `sort=${sortParam} is not available yet`, 'Parks sort by name, or -name for descending.');
	}
	const desc = sortParam === '-name';

	const where: string[] = [`${FLAG[view]} = 1`];
	const binds: (string | number)[] = [];
	const inList = (col: string, values: (string | number)[]) => {
		if (values.length === 0) return;
		where.push(`${col} IN (${values.map(() => '?').join(',')})`);
		binds.push(...values);
	};
	inList('status', list(url, 'status'));
	inList('country', list(url, 'country', (v) => v.toUpperCase()));
	inList('state', stateParam(url));
	// IN implies IS NOT NULL to SQLite, so this matches the partial resort index.
	inList('resort', resorts(url));
	// 2,200 parks: these walk the name index with the rest, bounded by the table.
	const box = bboxParam(url);
	if (box != null) {
		const w = boxWhere(box, 'lat', 'lon');
		where.push(w.sql);
		binds.push(...w.binds);
	}
	const since = updatedSinceParam(url);
	if (since != null) {
		where.push('updated_at >= ?');
		binds.push(since);
	}
	const match = searchMatch(url);
	if (match != null) {
		const q = searchWhere('park', 'id', 'in_latest', match);
		where.push(q.sql);
		binds.push(...q.binds);
	}

	// The leading range first, as on attractions, so D1 seeks the index to the cursor.
	const cursor = url.searchParams.get('cursor');
	if (cursor) {
		const [v, id] = decodeCursor(cursor, 'name');
		where.push(desc ? 'sort_name <= ? AND (sort_name < ? OR id < ?)' : 'sort_name >= ? AND (sort_name > ? OR id > ?)');
		binds.push(v, v, id);
	}

	const dir = desc ? 'DESC' : 'ASC';
	const sql = `SELECT id, sort_name AS position, summary FROM parks
		WHERE ${where.join(' AND ')}
		ORDER BY sort_name ${dir}, id ${dir} LIMIT ?`;
	return { sql, binds: [...binds, limit + 1], limit, view };
}

export async function listParks(url: URL, env: Env): Promise<{ response: Response; rows: number }> {
	const { sql, binds, limit, view } = parkListQuery(url);
	const { results } = await env.DATA.prepare(sql).bind(...binds).all<Row>();

	const page = results.slice(0, limit);
	const last = page[page.length - 1];
	const next = results.length > limit && last ? encodeCursor('name', last.position, last.id) : null;

	return {
		rows: page.length,
		response: json({
			data: page.map((r) => JSON.parse(r.summary)),
			meta: { data_as_of: await dataAsOf(env), next_cursor: next, view, licence: LICENCE },
		}),
	};
}

/** Six at most on 2026-10-13. A bound on the read, since anyone can edit a wrapper. */
const MAX_RESORT_PARKS = 50;

/**
 * Most resorts have no article and there is no /v1/resorts, so the record's
 * resort lists its parks: this one included, in the wrapper's order, from the
 * same view. Read here rather than sent with the record, so a sibling's edit
 * shows at once and no other park's record changes (DR-010 §12.20).
 */
export const resortParksSql = (view: View) => `SELECT summary FROM parks
	WHERE ${FLAG[view]} = 1 AND resort = ?
	ORDER BY resort_order IS NULL, resort_order, sort_name, id LIMIT ${MAX_RESORT_PARKS}`;

type ResortPark = { id: number; name: string; status?: string };

async function resortParks(resortId: number, view: View, env: Env): Promise<ResortPark[]> {
	const { results } = await env.DATA.prepare(resortParksSql(view)).bind(resortId).all<{ summary: string }>();
	return results.map((r) => {
		const { id, name, status } = JSON.parse(r.summary);
		return status == null ? { id, name } : { id, name, status };
	});
}

async function withResortParks(record: Record<string, unknown>, _id: number, view: View, env: Env): Promise<void> {
	const resort = record.resort as { id: number } | undefined;
	if (resort == null) return;
	record.resort = { ...resort, parks: await resortParks(resort.id, view, env) };
}

/** The same for a batch: one read per resort, however many of its parks are in it. */
async function withResortParksAll(records: Record<string, unknown>[], view: View, env: Env): Promise<void> {
	const byResort = new Map<number, Promise<ResortPark[]>>();
	for (const record of records) {
		const resort = record.resort as { id: number } | undefined;
		if (resort == null) continue;
		if (!byResort.has(resort.id)) byResort.set(resort.id, resortParks(resort.id, view, env));
		record.resort = { ...resort, parks: await byResort.get(resort.id)! };
	}
}

const PARK: RecordKind = {
	kind: 'park', table: 'parks', path: '/v1/parks', noun: 'park', decorate: withResortParks, decorateAll: withResortParksAll,
};

export async function getPark(url: URL, idText: string, env: Env): Promise<{ response: Response; rows: number }> {
	checkParams(url, ONE_PARAMS, NOT_YET);
	return getRecord(url, idText, env, PARK);
}

/** GET /v1/parks?ids= (DR-010 §12.26): up to 50 full park records, as /v1/attractions?ids= gives attractions. */
export async function getParks(url: URL, env: Env): Promise<{ response: Response; rows: number }> {
	return getByIds(url, env, PARK);
}

/**
 * /v1/parks/{id}/attractions: /v1/attractions?park={id}, where each attraction
 * is now (at the park, or stored there). A park the API doesn't hold is a 404
 * and a merged one redirects, as /v1/manufacturers/{id}/products does: an empty
 * list would read as "has no attractions". One key read on top.
 */
export async function parkAttractions(url: URL, idText: string, env: Env): Promise<{ response: Response; rows: number }> {
	checkParams(url, ATTRACTION_PARAMS, NOT_YET);
	const include = url.searchParams.get('include') ?? 'current';
	if (!INCLUDE.has(include)) throw badRequest('parameter-invalid', `include=${include} is not one of current, former, all`);
	const id = Number(idText);
	if (!/^\d+$/.test(idText) || !Number.isSafeInteger(id) || id < 1) {
		return { rows: 0, response: problem(404, 'not-found', 'No such park') };
	}

	const known = await env.DATA.prepare('SELECT 1 AS found FROM parks WHERE id = ? LIMIT 1').bind(id).first();
	if (!known) {
		const merged = await env.DATA.prepare(`SELECT new_id FROM merges WHERE kind = 'park' AND old_id = ?`)
			.bind(id).first<{ new_id: number }>();
		if (merged) {
			const location = new URL(`/v1/parks/${merged.new_id}/attractions`, url);
			location.search = url.search;
			return { rows: 0, response: new Response(null, { status: 301, headers: { location: location.toString() } }) };
		}
		return { rows: 0, response: problem(404, 'not-found', 'No such park') };
	}

	const asList = new URL(await followMerges(url, env));
	asList.searchParams.set('park', String(id));
	asList.searchParams.delete('include');
	return runAttractionList(listQuery(asList, include as Include), env);
}
