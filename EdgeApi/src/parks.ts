import {
	checkParams, dataAsOf, decodeCursor, encodeCursor, FLAG, getRecord, limitParam, list, viewParam, type View,
} from './attractions';
import type { Env } from './env';
import { badRequest, json, LICENCE, problem } from './http';

/**
 * Contract parameters of /v1/parks whose field is not published yet (DR-010
 * §12.7). Refused, never ignored: ?bbox= answering with every park would look
 * like an answer.
 */
const NOT_YET = new Set(['bbox', 'updated_since', 'q']);

const LIST_PARAMS = new Set(['status', 'country', 'state', 'resort', 'sort', 'view', 'limit', 'cursor']);
const ONE_PARAMS = new Set(['view']);

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

/**
 * state as the wiki spells it ("New York"): a name, not a slug. Repeated for
 * several (state=Ohio&state=Tennessee), never comma-separated, because the
 * infobox allows "Washington, D.C.".
 */
function states(url: URL): string[] {
	const values = url.searchParams.getAll('state').map((v) => v.trim());
	if (values.some((v) => v.length === 0) || values.length > 50) {
		throw badRequest('parameter-invalid', 'state takes 1 to 50 names, each as its own state= parameter');
	}
	return values;
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
	inList('state', states(url));
	// IN implies IS NOT NULL to SQLite, so this matches the partial resort index.
	inList('resort', resorts(url));

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

export async function getPark(url: URL, idText: string, env: Env): Promise<{ response: Response; rows: number }> {
	checkParams(url, ONE_PARAMS, NOT_YET);
	return getRecord(url, idText, env, { kind: 'park', table: 'parks', path: '/v1/parks', noun: 'park' });
}

/**
 * /v1/parks/{id}/attractions needs each attraction's park, which waits on the
 * attraction tables storing LocationKind (DR-010 §12.1). Refused, so nobody
 * reads an empty list as "this park has no attractions".
 */
export async function parkAttractions(): Promise<{ response: Response; rows: number }> {
	return {
		rows: 0,
		response: problem(404, 'not-available', 'Not available yet',
			"An attraction's park is published once the attraction tables store where each attraction is. Until then this endpoint has no answer."),
	};
}
