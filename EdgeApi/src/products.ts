import {
	checkParams, dataAsOf, decodeCursor, encodeCursor, FLAG, followManufacturerMerge, getRecord, limitParam, list,
	manufacturerParam, viewParam, type View,
} from './attractions';
import type { Env } from './env';
import { badRequest, json, LICENCE, problem } from './http';

/**
 * Contract parameters of /v1/products whose field is not published yet
 * (DR-010 §12.10). Refused, never ignored, as on the other lists.
 */
const NOT_YET = new Set(['updated_since', 'q']);

const LIST_PARAMS = new Set(['type', 'status', 'manufacturer', 'sort', 'view', 'limit', 'cursor']);
/** /v1/manufacturers/{id}/products: the list's own, less the manufacturer the path already names. */
const OF_MANUFACTURER_PARAMS = new Set(['type', 'status', 'sort', 'view', 'limit', 'cursor']);
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
 * The list query for a request, apart from running it, so tests can measure
 * what it reads. manufacturer is the one to list for, from the query string or
 * the path; with one, the page is read from its links (migration 0009), which
 * carry the filters, and joined back for the summary.
 */
export function productListQuery(url: URL, manufacturer: number | null): ListQuery {
	const view = viewParam(url);
	const limit = limitParam(url);
	const sortParam = url.searchParams.get('sort') ?? 'name';
	if (sortParam !== 'name' && sortParam !== '-name') {
		throw badRequest('parameter-unavailable', `sort=${sortParam} is not available yet`, 'Products sort by name, or -name for descending.');
	}
	const desc = sortParam === '-name';
	const c = (col: string) => (manufacturer == null ? col : `m.${col}`);

	const where: string[] = [`${c(FLAG[view])} = 1`];
	const binds: (string | number)[] = [];
	if (manufacturer != null) {
		where.push('m.manufacturer = ?');
		binds.push(manufacturer);
	}
	const inList = (col: string, values: string[]) => {
		if (values.length === 0) return;
		where.push(`${c(col)} IN (${values.map(() => '?').join(',')})`);
		binds.push(...values);
	};
	// Without a manufacturer both walk the name index (0009): the table is too small to index them.
	inList('type', list(url, 'type'));
	inList('status', list(url, 'status'));

	const cursor = url.searchParams.get('cursor');
	if (cursor) {
		const [v, id] = decodeCursor(cursor, 'name');
		const [col, key] = [c('sort_name'), c('id')];
		where.push(desc ? `${col} <= ? AND (${col} < ? OR ${key} < ?)` : `${col} >= ? AND (${col} > ? OR ${key} > ?)`);
		binds.push(v, v, id);
	}

	const dir = desc ? 'DESC' : 'ASC';
	const from = manufacturer == null
		? 'products'
		: 'product_manufacturers m JOIN products p ON p.id = m.id AND p.in_latest = m.in_latest';
	const sql = `SELECT ${c('id')} AS id, ${c('sort_name')} AS position, ${manufacturer == null ? 'summary' : 'p.summary'} FROM ${from}
		WHERE ${where.join(' AND ')}
		ORDER BY ${c('sort_name')} ${dir}, ${c('id')} ${dir} LIMIT ?`;
	return { sql, binds: [...binds, limit + 1], limit, view };
}

async function runList(query: ListQuery, env: Env): Promise<{ response: Response; rows: number }> {
	const { sql, binds, limit, view } = query;
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

export async function listProducts(url: URL, env: Env): Promise<{ response: Response; rows: number }> {
	checkParams(url, LIST_PARAMS, NOT_YET);
	const followed = await followManufacturerMerge(url, env);
	return runList(productListQuery(followed, manufacturerParam(followed)), env);
}

/**
 * /v1/manufacturers/{id}/products. A manufacturer the API doesn't hold is a
 * 404 and a merged one redirects, as /v1/manufacturers/{id} does: an empty
 * list would read as "makes nothing". One key read on top of the list.
 */
export async function manufacturerProducts(url: URL, idText: string, env: Env): Promise<{ response: Response; rows: number }> {
	checkParams(url, OF_MANUFACTURER_PARAMS, NOT_YET);
	const id = Number(idText);
	if (!/^\d+$/.test(idText) || !Number.isSafeInteger(id) || id < 1) {
		return { rows: 0, response: problem(404, 'not-found', 'No such manufacturer') };
	}

	const known = await env.DATA.prepare('SELECT 1 AS found FROM manufacturers WHERE id = ? LIMIT 1').bind(id).first();
	if (!known) {
		const merged = await env.DATA.prepare(`SELECT new_id FROM merges WHERE kind = 'manufacturer' AND old_id = ?`)
			.bind(id).first<{ new_id: number }>();
		if (merged) {
			const location = new URL(`/v1/manufacturers/${merged.new_id}/products`, url);
			location.search = url.search;
			return { rows: 0, response: new Response(null, { status: 301, headers: { location: location.toString() } }) };
		}
		return { rows: 0, response: problem(404, 'not-found', 'No such manufacturer') };
	}

	return runList(productListQuery(url, id), env);
}

export async function getProduct(url: URL, idText: string, env: Env): Promise<{ response: Response; rows: number }> {
	checkParams(url, ONE_PARAMS, NOT_YET);
	return getRecord(url, idText, env, { kind: 'product', table: 'products', path: '/v1/products', noun: 'product' });
}

/**
 * /v1/products/{id}/installations needs each attraction's product, which waits
 * on the rebuild's A3 (Product as a real list on coasters and rides). Refused,
 * so nobody reads an empty list as "never built".
 */
export async function productInstallations(): Promise<{ response: Response; rows: number }> {
	return {
		rows: 0,
		response: problem(404, 'not-available', 'Not available yet',
			"Installations are published once attractions publish their product. Until then this endpoint has no answer."),
	};
}
