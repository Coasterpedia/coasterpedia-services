import {
	checkParams, decodeCursor, encodeCursor, FLAG, getRecord, limitParam, list, viewParam, withDataAsOf, type View,
} from './attractions';
import type { Env } from './env';
import { searchMatch, searchWhere, updatedSinceParam } from './filters';
import { badRequest, json, LICENCE } from './http';

/**
 * Contract parameters of /v1/manufacturers whose field is not published yet
 * (DR-010 §12.8). Refused, never ignored, as on the other lists.
 */
const NOT_YET = new Set<string>();

const LIST_PARAMS = new Set(['status', 'country', 'updated_since', 'q', 'sort', 'view', 'limit', 'cursor']);
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

/** The list query for a request, apart from running it, so tests can measure what it reads. */
export function manufacturerListQuery(url: URL): ListQuery {
	checkParams(url, LIST_PARAMS, NOT_YET);
	const view = viewParam(url);
	const limit = limitParam(url);
	const sortParam = url.searchParams.get('sort') ?? 'name';
	if (sortParam !== 'name' && sortParam !== '-name') {
		throw badRequest('parameter-unavailable', `sort=${sortParam} is not available yet`, 'Manufacturers sort by name, or -name for descending.');
	}
	const desc = sortParam === '-name';

	const where: string[] = [`${FLAG[view]} = 1`];
	const binds: (string | number)[] = [];
	const inList = (col: string, values: string[]) => {
		if (values.length === 0) return;
		where.push(`${col} IN (${values.map(() => '?').join(',')})`);
		binds.push(...values);
	};
	// Both walk the name index (0007): the table is too small to index them.
	inList('status', list(url, 'status'));
	inList('country', list(url, 'country', (v) => v.toUpperCase()));
	const since = updatedSinceParam(url);
	if (since != null) {
		where.push('updated_at >= ?');
		binds.push(since);
	}
	const match = searchMatch(url);
	if (match != null) {
		const q = searchWhere('manufacturer', 'id', 'in_latest', match);
		where.push(q.sql);
		binds.push(...q.binds);
	}

	const cursor = url.searchParams.get('cursor');
	if (cursor) {
		const [v, id] = decodeCursor(cursor, 'name');
		where.push(desc ? 'sort_name <= ? AND (sort_name < ? OR id < ?)' : 'sort_name >= ? AND (sort_name > ? OR id > ?)');
		binds.push(v, v, id);
	}

	const dir = desc ? 'DESC' : 'ASC';
	const sql = `SELECT id, sort_name AS position, summary FROM manufacturers
		WHERE ${where.join(' AND ')}
		ORDER BY sort_name ${dir}, id ${dir} LIMIT ?`;
	return { sql, binds: [...binds, limit + 1], limit, view };
}

export async function listManufacturers(url: URL, env: Env): Promise<{ response: Response; rows: number }> {
	const { sql, binds, limit, view } = manufacturerListQuery(url);
	const { results, asOf } = await withDataAsOf<Row>(env, env.DATA.prepare(sql).bind(...binds));

	const page = results.slice(0, limit);
	const last = page[page.length - 1];
	const next = results.length > limit && last ? encodeCursor('name', last.position, last.id) : null;

	return {
		rows: page.length,
		response: json({
			data: page.map((r) => JSON.parse(r.summary)),
			meta: { data_as_of: asOf, next_cursor: next, view, licence: LICENCE },
		}),
	};
}

export async function getManufacturer(url: URL, idText: string, env: Env): Promise<{ response: Response; rows: number }> {
	checkParams(url, ONE_PARAMS, NOT_YET);
	return getRecord(url, idText, env, { kind: 'manufacturer', table: 'manufacturers', path: '/v1/manufacturers', noun: 'manufacturer' });
}
