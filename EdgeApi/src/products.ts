import {
	checkParams, dataAsOf, decodeCursor, encodeCursor, FLAG, followMerges, getRecord, limitParam, list, listQuery,
	manufacturerParam, NOT_YET as ATTRACTION_NOT_YET, runAttractionList, viewParam, type View,
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
/**
 * /v1/products/{id}/installations: the attractions list for the product in
 * the path, so it takes that list's filters and sorts, less the product.
 */
const INSTALLATION_PARAMS = new Set([
	'type', 'status', 'country', 'state', 'park', 'location_kind', 'manufacturer', 'variant', 'opened_from', 'opened_to',
	'sort', 'view', 'limit', 'cursor',
]);

/** product_installations' column for each view (migration 0011). */
const COUNT: Record<View, string> = { latest: 'latest', settled: 'settled' };

interface Row {
	id: number;
	position: string;
	summary: string;
	installations: number | null;
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
	// Installations are counted by trigger from the attractions (0011): one key read per row returned.
	const sql = `SELECT ${c('id')} AS id, ${c('sort_name')} AS position, ${manufacturer == null ? 'summary' : 'p.summary'},
			i.${COUNT[view]} AS installations
		FROM ${from} LEFT JOIN product_installations i ON i.product = ${c('id')}
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
			data: page.map((r) => withInstallations(JSON.parse(r.summary), r.installations)),
			meta: { data_as_of: await dataAsOf(env), next_cursor: next, view, licence: LICENCE },
		}),
	};
}

export async function listProducts(url: URL, env: Env): Promise<{ response: Response; rows: number }> {
	checkParams(url, LIST_PARAMS, NOT_YET);
	const followed = await followMerges(url, env);
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

/**
 * The count beside updated_at, where the contract lists it. Absent from the
 * counter table means nothing links the product: 0, never unknown.
 */
function withInstallations(item: Record<string, unknown>, n: number | null | undefined): Record<string, unknown> {
	const { updated_at, ...rest } = item;
	return { ...rest, installations: n ?? 0, ...(updated_at === undefined ? {} : { updated_at }) };
}

export async function getProduct(url: URL, idText: string, env: Env): Promise<{ response: Response; rows: number }> {
	checkParams(url, ONE_PARAMS, NOT_YET);
	return getRecord(url, idText, env, {
		kind: 'product', table: 'products', path: '/v1/products', noun: 'product',
		decorate: async (record, id, view, env) => {
			const row = await env.DATA.prepare(`SELECT ${COUNT[view]} AS n FROM product_installations WHERE product = ?`)
				.bind(id).first<{ n: number }>();
			record.installations = row?.n ?? 0;
		},
	});
}

/**
 * /v1/products/{id}/installations: /v1/attractions?product={id}, so it
 * filters, sorts and pages as that list does. A product the API doesn't hold
 * is a 404 and a merged one redirects, as /v1/manufacturers/{id}/products
 * does: an empty list would read as "never built". One key read on top.
 */
export async function productInstallations(url: URL, idText: string, env: Env): Promise<{ response: Response; rows: number }> {
	checkParams(url, INSTALLATION_PARAMS, ATTRACTION_NOT_YET);
	const id = Number(idText);
	if (!/^\d+$/.test(idText) || !Number.isSafeInteger(id) || id < 1) {
		return { rows: 0, response: problem(404, 'not-found', 'No such product') };
	}

	const known = await env.DATA.prepare('SELECT 1 AS found FROM products WHERE id = ? LIMIT 1').bind(id).first();
	if (!known) {
		const merged = await env.DATA.prepare(`SELECT new_id FROM merges WHERE kind = 'product' AND old_id = ?`)
			.bind(id).first<{ new_id: number }>();
		if (merged) {
			const location = new URL(`/v1/products/${merged.new_id}/installations`, url);
			location.search = url.search;
			return { rows: 0, response: new Response(null, { status: 301, headers: { location: location.toString() } }) };
		}
		return { rows: 0, response: problem(404, 'not-found', 'No such product') };
	}

	const asList = new URL(await followMerges(url, env));
	asList.searchParams.set('product', String(id));
	return runAttractionList(listQuery(asList), env);
}
