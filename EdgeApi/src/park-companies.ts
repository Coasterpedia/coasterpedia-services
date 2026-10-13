import {
	checkParams, dataAsOf, decodeCursor, encodeCursor, FLAG, getRecord, limitParam, list, viewParam, type View,
} from './attractions';
import type { Env } from './env';
import { searchMatch, searchWhere, updatedSinceParam } from './filters';
import { badRequest, json, LICENCE, problem } from './http';

/**
 * Contract parameters of /v1/park-companies whose field is not published yet
 * (DR-010 §12.16). Refused, never ignored, as on the other lists.
 */
const NOT_YET = new Set<string>();

const LIST_PARAMS = new Set(['status', 'country', 'updated_since', 'q', 'sort', 'view', 'limit', 'cursor']);
const ONE_PARAMS = new Set(['view']);
const PARKS_PARAMS = new Set(['role', 'include', 'view']);

const ROLES = new Set(['owner', 'operator', 'any']);
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

/** The list query for a request, apart from running it, so tests can measure what it reads. */
export function parkCompanyListQuery(url: URL): ListQuery {
	checkParams(url, LIST_PARAMS, NOT_YET);
	const view = viewParam(url);
	const limit = limitParam(url);
	const sortParam = url.searchParams.get('sort') ?? 'name';
	if (sortParam !== 'name' && sortParam !== '-name') {
		throw badRequest('parameter-unavailable', `sort=${sortParam} is not available yet`, 'Park companies sort by name, or -name for descending.');
	}
	const desc = sortParam === '-name';

	const where: string[] = [`${FLAG[view]} = 1`];
	const binds: (string | number)[] = [];
	const inList = (col: string, values: string[]) => {
		if (values.length === 0) return;
		where.push(`${col} IN (${values.map(() => '?').join(',')})`);
		binds.push(...values);
	};
	// Both walk the name index (0013): 68 companies.
	inList('status', list(url, 'status'));
	inList('country', list(url, 'country', (v) => v.toUpperCase()));
	const since = updatedSinceParam(url);
	if (since != null) {
		where.push('updated_at >= ?');
		binds.push(since);
	}
	const match = searchMatch(url);
	if (match != null) {
		const q = searchWhere('park_company', 'id', 'in_latest', match);
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
	const sql = `SELECT id, sort_name AS position, summary FROM park_companies
		WHERE ${where.join(' AND ')}
		ORDER BY sort_name ${dir}, id ${dir} LIMIT ?`;
	return { sql, binds: [...binds, limit + 1], limit, view };
}

export async function listParkCompanies(url: URL, env: Env): Promise<{ response: Response; rows: number }> {
	const { sql, binds, limit, view } = parkCompanyListQuery(url);
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

export async function getParkCompany(url: URL, idText: string, env: Env): Promise<{ response: Response; rows: number }> {
	checkParams(url, ONE_PARAMS, NOT_YET);
	return getRecord(url, idText, env, { kind: 'park_company', table: 'park_companies', path: '/v1/park-companies', noun: 'park company' });
}

interface LinkRow {
	park: number;
	park_name: string;
	role: string;
	start: string | null;
	end: string | null;
	current: number;
}

/** The query for one company's parks, apart from running it, so tests can measure what it reads. */
export function parkCompanyParksQuery(url: URL, id: number): { sql: string; binds: (string | number)[]; view: View } {
	checkParams(url, PARKS_PARAMS, NOT_YET);
	const view = viewParam(url);
	const role = url.searchParams.get('role') ?? 'any';
	if (!ROLES.has(role)) throw badRequest('parameter-invalid', `role=${role} is not one of owner, operator, any`);
	const include = url.searchParams.get('include') ?? 'all';
	if (!INCLUDE.has(include)) throw badRequest('parameter-invalid', `include=${include} is not one of current, former, all`);

	const where = ['company = ?', `${FLAG[view]} = 1`];
	const binds: (string | number)[] = [id];
	if (role !== 'any') {
		where.push('role = ?');
		binds.push(role);
	}
	if (include !== 'all') where.push(`current = ${include === 'current' ? 1 : 0}`);

	// Owners before operators at one park, and within a role the page's order, oldest first.
	const sql = `SELECT park, park_name, role, start, "end", current FROM park_company_parks
		WHERE ${where.join(' AND ')}
		ORDER BY sort_name, park, CASE role WHEN 'owner' THEN 0 ELSE 1 END, seq`;
	return { sql, binds, view };
}

/**
 * /v1/park-companies/{id}/parks: every spell the company holds at a park, as
 * owner or operator, now or before, from the parks' History (DR-010 §12.16).
 * One item per spell, so a company that owned a park twice is listed twice.
 * A company the API doesn't hold is a 404, a merged one a 301, as for a
 * manufacturer's products.
 */
export async function parkCompanyParks(url: URL, idText: string, env: Env): Promise<{ response: Response; rows: number }> {
	const id = Number(idText);
	if (!/^\d+$/.test(idText) || !Number.isSafeInteger(id) || id < 1) {
		checkParams(url, PARKS_PARAMS, NOT_YET);
		return { rows: 0, response: problem(404, 'not-found', 'No such park company') };
	}
	const { sql, binds, view } = parkCompanyParksQuery(url, id);

	const known = await env.DATA.prepare('SELECT 1 AS found FROM park_companies WHERE id = ? LIMIT 1').bind(id).first();
	if (!known) {
		const merged = await env.DATA.prepare(`SELECT new_id FROM merges WHERE kind = 'park_company' AND old_id = ?`)
			.bind(id).first<{ new_id: number }>();
		if (merged) {
			const location = new URL(`/v1/park-companies/${merged.new_id}/parks`, url);
			location.search = url.search;
			return { rows: 0, response: new Response(null, { status: 301, headers: { location: location.toString() } }) };
		}
		return { rows: 0, response: problem(404, 'not-found', 'No such park company') };
	}

	const { results } = await env.DATA.prepare(sql).bind(...binds).all<LinkRow>();
	return {
		rows: results.length,
		response: json({
			data: results.map((r) => ({
				park: { id: r.park, name: r.park_name },
				role: r.role,
				start: r.start == null ? null : JSON.parse(r.start),
				end: r.end == null ? null : JSON.parse(r.end),
				current: r.current === 1,
			})),
			meta: { data_as_of: await dataAsOf(env), view, licence: LICENCE },
		}),
	};
}
