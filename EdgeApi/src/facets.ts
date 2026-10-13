import { checkParams, FLAG, followMerges, linkFilter, list, openedParams, parkParam, productParam, stateParam, viewParam, type Link, type View } from './attractions';
import type { Env } from './env';
import { bboxParam, boxCells, boxWhere, searchMatch, searchWhere, updatedSinceParam } from './filters';
import { json, LICENCE } from './http';

/**
 * GET /v1/attractions/facets (DR-010 §12.6): counts per filter value for the
 * set /v1/attractions would list, and a histogram per sortable stat. The
 * Attractions Browser's options call, answered from D1.
 */

const FACET_PARAMS = new Set([
	'type', 'status', 'country', 'location_kind', 'park', 'state', 'opened_from', 'opened_to', 'manufacturer', 'product', 'variant',
	'bbox', 'updated_since', 'q', 'view',
]);

/**
 * The facet dimensions, each a column of attraction_facets. The other filters
 * (a park, a state, the opened range, q, a box) narrow every list without
 * being one. Manufacturer, product and variant are dimensions too, counted
 * from their link tables (LINK_DIMENSIONS).
 */
const DIMENSIONS = ['type', 'status', 'country', 'location_kind'] as const;
type Dimension = (typeof DIMENSIONS)[number];

/** Stats with a histogram: the columns D1 holds (migration 0003), metric. */
const RANGES: Record<string, string> = { height: 'm', speed: 'km/h', length: 'm' };

// The browser's widths (Module:AttractionBrowser histogramWidth), so its
// sidebar draws the same bars from either source.
const MAX_BUCKETS = 32;
const MIN_BUCKETS = 8;

interface Cell {
	type: string;
	status: string;
	country: string;
	location_kind: string;
	country_name: string | null;
	n: number;
}

interface Item {
	value: string;
	label?: string;
	count: number;
}

interface Bucket {
	from: number;
	to: number;
	count: number;
}

interface Range {
	unit: string;
	min: number;
	max: number;
	buckets: Bucket[];
}

type Filters = Record<Dimension, Set<string> | null>;

function filtersOf(url: URL): Filters {
	const set = (values: string[]) => (values.length ? new Set(values) : null);
	return {
		type: set(list(url, 'type')),
		status: set(list(url, 'status')),
		country: set(list(url, 'country', (v) => v.toUpperCase())),
		location_kind: set(list(url, 'location_kind')),
	};
}

interface Query {
	sql: string;
	binds: (string | number)[];
}

export interface FacetQueries {
	view: View;
	filters: Filters;
	/** Every non-empty cell in the view; the dimensions are counted from these in the Worker. */
	cells: Query;
	/** When something narrows the set, its cells carry no country names, so they come from attraction_facets. */
	countryNames: Query | null;
	/** Per stat: each distinct value in the filtered set, with how many record it. */
	ranges: Record<string, Query>;
	/** Per link dimension: value, n and its label source, each with its own filter left out. */
	links: Partial<Record<LinkDimension, Query>>;
}

/** The set a request's filters select, as the pieces each facet query is built from. */
interface FilterSet {
	link: Link | null;
	driver: string | null;
	/** What narrows without being a dimension: the link, a park, a state, the opened range, q, a box, updated_since. */
	narrow: string[];
	narrowBinds: (string | number)[];
	/** narrow and the dimension filters. */
	where: string[];
	binds: (string | number)[];
	/** The FROM: attractions, on the driver's index, or the link joined to its rows (m, a). */
	table: string;
	/** The link alone when its rows aren't needed, else table. */
	rangeTable: string;
	/** A link column, or the table's. */
	m: (col: string) => string;
	a: (col: string) => string;
}

function filterSet(url: URL, view: View, filters: Filters): FilterSet {
	// What narrows the set without being a dimension is read from its own index,
	// as on the list: a manufacturer's or product's links (0008, 0011) joined to
	// the row for the columns they lack, else a park, a state or the opened
	// range (0012). Its counts then come from its own rows, never the table.
	const link = linkFilter(url);
	const park = parkParam(url);
	const states = stateParam(url);
	const opened = openedParams(url);
	const match = searchMatch(url);
	const box = bboxParam(url);
	const since = updatedSinceParam(url);
	// As on the list (DR-010 §12.19): names, a box and what changed since narrow
	// too, each driving from its own index when nothing narrower does.
	const driver = link != null ? 'link'
		: park != null ? 'park'
		: match != null ? 'search'
		: box != null ? 'bbox'
		: states.length > 0 ? 'state'
		: opened.from != null ? 'opened'
		: opened.to != null ? 'opened_latest'
		: since != null ? 'updated'
		: null;
	// The link table's own columns, and the row's.
	const m = (col: string) => (link == null ? col : `m.${col}`);
	const a = (col: string) => (link == null ? col : `a.${col}`);
	// Unary + keeps every index but the driver's out of the plan (§12.12).
	const off = (col: string) => (link == null ? `+${col}` : a(col));

	const narrow: string[] = link == null ? [] : [...link.where];
	const narrowBinds: (string | number)[] = link == null ? [] : [...link.binds];
	if (park != null) {
		narrow.push(`${driver === 'park' ? 'park' : off('park')} = ?`);
		narrowBinds.push(park);
	}
	if (states.length > 0) {
		narrow.push(`${driver === 'state' ? 'state' : off('state')} IN (${states.map(() => '?').join(',')})`);
		narrowBinds.push(...states);
	}
	if (opened.from != null) {
		const col = driver === 'opened' ? 'opened_earliest' : off('opened_earliest');
		narrow.push(`${col} >= ?`);
		narrowBinds.push(opened.from);
		if (opened.to != null) {
			narrow.push(`${col} <= ?`);
			narrowBinds.push(opened.to);
		}
	}
	if (opened.to != null) {
		narrow.push(`${driver === 'opened_latest' ? 'opened_latest' : off('opened_latest')} <= ?`);
		narrowBinds.push(opened.to);
	}
	if (match != null) {
		const q = searchWhere('attraction', m('id'), m('in_latest'), match);
		narrow.push(q.sql);
		narrowBinds.push(...q.binds);
	}
	if (box != null) {
		if (driver === 'bbox') {
			const cells = boxCells(box);
			narrow.push(`cell IN (${cells.map(() => '?').join(',')})`);
			narrowBinds.push(...cells);
		}
		const w = boxWhere(box, off('lat'), off('lon'));
		narrow.push(w.sql);
		narrowBinds.push(...w.binds);
	}
	if (since != null) {
		narrow.push(`${driver === 'updated' ? 'updated_at' : off('updated_at')} >= ?`);
		narrowBinds.push(since);
	}

	const where = [...narrow];
	const binds = [...narrowBinds];
	for (const d of DIMENSIONS) {
		const values = filters[d];
		if (!values) continue;
		// Not the status or kind index (0010, 0012): either would read every row
		// of its value and group them, where the stat's own index stops at its coverage.
		const col = d === 'location_kind' ? (link == null ? '+location_kind' : a(d))
			: link == null ? (d === 'status' || driver != null ? `+${d}` : d)
			: m(d);
		where.push(`${col} IN (${[...values].map(() => '?').join(',')})`);
		binds.push(...values);
	}

	const forced = driver === 'opened' ? ` INDEXED BY attractions_${view}_by_opened`
		: driver === 'opened_latest' ? ` INDEXED BY attractions_${view}_by_opened_latest`
		: driver === 'bbox' ? ` INDEXED BY attractions_${view}_by_cell`
		: driver === 'updated' ? ` INDEXED BY attractions_${view}_by_updated`
		: '';
	const table = link == null
		? `attractions${forced}`
		: `${link.table} m JOIN attractions a ON a.id = m.id AND a.in_latest = m.in_latest`;
	// A link carries every stat and dimension but the kind and the narrowing
	// columns: without those, the histograms read its index alone, as before.
	const needsRow = park != null || states.length > 0 || opened.from != null || opened.to != null || filters.location_kind != null
		|| box != null || since != null;
	const rangeTable = link != null && !needsRow ? `${link.table} m` : table;
	return { link, driver, narrow, narrowBinds, where, binds, table, rangeTable, m, a };
}

/**
 * The link dimensions (DR-010 §7.4): the browser's Manufacturer → Product →
 * Variant cascade. Not cells of attraction_facets: an attraction links several
 * of each, and there are about 1,350 of them. Each is counted live from its
 * link table, with its own filters left out: choosing a manufacturer narrows
 * the products but not the manufacturers. A product leaves out its variant too,
 * which means nothing under another product. Variant is listed only under one
 * product, as the browser shows it: across products No. 1, 48 and Medium mean nothing.
 */
const LINK_DIMENSIONS = ['manufacturer', 'product', 'variant'] as const;
type LinkDimension = (typeof LINK_DIMENSIONS)[number];

const LINKS: Record<LinkDimension, { table: string; col: string; drop: string[]; names: string | null }> = {
	manufacturer: { table: 'attraction_manufacturers', col: 'manufacturer', drop: ['manufacturer'], names: 'manufacturers' },
	product: { table: 'attraction_products', col: 'product', drop: ['product', 'variant'], names: 'products' },
	variant: { table: 'attraction_products', col: 'variant', drop: ['variant'], names: null },
};

/**
 * One link dimension's counts for the set without its own filters. Whatever
 * drives that set drives this, and the dimension's links are read per row by
 * key: another link's range (a product's ≤ 135 rows, a manufacturer's ≤ 1,900),
 * else the narrowing index's rows, else the dimension's own link table, in
 * its value order. CROSS JOIN keeps the link inner: SQLite would otherwise
 * walk it for the GROUP BY's order and look every row up in the driver.
 */
function linkQuery(dim: LinkDimension, url: URL, view: View, filters: Filters): Query {
	const L = LINKS[dim];
	const flag = FLAG[view];
	const set = filterSet(url, view, filters);
	// A blank variant counts toward the total, as any blank does, but no filter selects it.
	const listed = (col: string) => (dim === 'variant' ? [`${col} IS NOT NULL`, `${col} <> ''`] : []);

	let grouped: Query;
	if (set.link?.table === L.table) {
		grouped = {
			sql: `SELECT m.${L.col} AS value, COUNT(*) AS n FROM ${set.rangeTable}
				WHERE ${[`m.${flag} = 1`, ...listed(`m.${L.col}`), ...set.where].join(' AND ')} GROUP BY m.${L.col}`,
			binds: set.binds,
		};
	} else if (set.link) {
		grouped = {
			sql: `SELECT l.${L.col} AS value, COUNT(*) AS n FROM ${set.rangeTable}
				CROSS JOIN ${L.table} l ON l.id = m.id AND l.in_latest = m.in_latest
				WHERE ${[`m.${flag} = 1`, ...listed(`l.${L.col}`), ...set.where].join(' AND ')} GROUP BY l.${L.col}`,
			binds: set.binds,
		};
	} else if (set.driver) {
		// The set's own columns are unqualified, so it is a subquery, which also
		// keeps them from clashing with the link's.
		grouped = {
			sql: `SELECT l.${L.col} AS value, COUNT(*) AS n
				FROM (SELECT id, in_latest FROM ${set.table} WHERE ${[`${flag} = 1`, ...set.where].join(' AND ')}) s
				CROSS JOIN ${L.table} l ON l.id = s.id AND l.in_latest = s.in_latest
				${listed(`l.${L.col}`).length ? `WHERE ${listed(`l.${L.col}`).join(' AND ')}` : ''} GROUP BY l.${L.col}`,
			binds: set.binds,
		};
	} else {
		// Only dimension filters, which the link carries but for the kind.
		const where = [`l.${flag} = 1`, ...listed(`l.${L.col}`)];
		const binds: string[] = [];
		for (const d of DIMENSIONS) {
			const values = filters[d];
			if (!values) continue;
			where.push(`${d === 'location_kind' ? 'a' : 'l'}.${d} IN (${[...values].map(() => '?').join(',')})`);
			binds.push(...values);
		}
		const row = filters.location_kind ? ' JOIN attractions a ON a.id = l.id AND a.in_latest = l.in_latest' : '';
		grouped = {
			sql: `SELECT l.${L.col} AS value, COUNT(*) AS n FROM ${L.table} l${row} WHERE ${where.join(' AND ')} GROUP BY l.${L.col}`,
			binds,
		};
	}

	if (!L.names) return { sql: `SELECT value, n, NULL AS label FROM (${grouped.sql})`, binds: grouped.binds };
	// The name, and the makers to tell two products' names apart: from the
	// view's row, else the other one (a page not in this view yet). One key read per value.
	return {
		sql: `SELECT g.value, g.n, (SELECT json_extract(k.summary, '$.name', '$.manufacturers') FROM ${L.names} k
				WHERE k.id = g.value ORDER BY k.${flag} DESC LIMIT 1) AS label
			FROM (${grouped.sql}) g`,
		binds: grouped.binds,
	};
}

/** The request without some parameters: a dimension's own filters, left out to count it. */
function without(url: URL, names: string[]): URL {
	const copy = new URL(url);
	for (const name of names) copy.searchParams.delete(name);
	return copy;
}

/** The queries for a request, apart from running them, so tests can measure what they read. */
export function facetQueries(url: URL): FacetQueries {
	checkParams(url, FACET_PARAMS);
	const view = viewParam(url);
	const filters = filtersOf(url);
	const set = filterSet(url, view, filters);
	const { m, driver, where, binds, narrow, narrowBinds, table, rangeTable, a } = set;

	// No stat filter exists yet, so every range is measured under the full
	// filter. A stat filter, when one comes, leaves out its own range, as the
	// browser's whereWithout does: bounds measured inside the cut can't be widened.
	const ranges: FacetQueries['ranges'] = {};
	for (const stat of Object.keys(RANGES)) {
		// The flag and IS NOT NULL as literals, so SQLite matches the stat's partial index.
		ranges[stat] = {
			sql: `SELECT ${m(stat)} AS v, COUNT(*) AS n FROM ${rangeTable}
				WHERE ${[`${m(FLAG[view])} = 1`, `${m(stat)} IS NOT NULL`, ...where].join(' AND ')}
				GROUP BY ${m(stat)}`,
			binds,
		};
	}

	const links: FacetQueries['links'] = {};
	for (const dim of LINK_DIMENSIONS) {
		if (dim === 'variant' && productParam(url) == null) continue;
		links[dim] = linkQuery(dim, without(url, LINKS[dim].drop), view, filters);
	}

	if (driver == null) {
		return {
			view,
			filters,
			cells: {
				sql: `SELECT type, status, country, location_kind, country_name, n FROM attraction_facets WHERE view = ? AND n > 0`,
				binds: [view],
			},
			countryNames: null,
			ranges,
			links,
		};
	}

	// The dimension filters are left to countDimensions, as for the whole table,
	// so each dimension is still counted with its own filter left out.
	const kind = `IFNULL(${a('location_kind')}, '')`;
	return {
		view,
		filters,
		cells: {
			sql: `SELECT ${m('type')} AS type, IFNULL(${m('status')}, '') AS status, IFNULL(${m('country')}, '') AS country,
					${kind} AS location_kind, NULL AS country_name, COUNT(*) AS n
				FROM ${table} WHERE ${[...narrow, `${m(FLAG[view])} = 1`].join(' AND ')}
				GROUP BY ${m('type')}, IFNULL(${m('status')}, ''), IFNULL(${m('country')}, ''), ${kind}`,
			binds: narrowBinds,
		},
		countryNames: {
			sql: `SELECT country, country_name FROM attraction_facets WHERE view = ? AND n > 0 AND country_name IS NOT NULL`,
			binds: [view],
		},
		ranges,
		links,
	};
}

/**
 * Counts per value, each dimension with its own filter left out, so choosing a
 * country does not shrink the country list. A blank value counts toward the
 * total and the other dimensions, but is not listed: no filter can select it.
 */
export function countDimensions(cells: Cell[], filters: Filters, labels: Partial<Record<Dimension, Map<string, string>>>,
	countryNames = new Map<string, string>()) {
	const passes = (cell: Cell, skip: Dimension | null) =>
		DIMENSIONS.every((d) => d === skip || !filters[d] || filters[d]!.has(cell[d]));

	let total = 0;
	const dimensions = {} as Record<Dimension, Item[]>;
	const counts = Object.fromEntries(DIMENSIONS.map((d) => [d, new Map<string, number>()])) as Record<Dimension, Map<string, number>>;

	for (const cell of cells) {
		if (passes(cell, null)) total += cell.n;
		if (cell.country_name) countryNames.set(cell.country, cell.country_name);
		for (const d of DIMENSIONS) {
			if (cell[d] === '' || !passes(cell, d)) continue;
			counts[d].set(cell[d], (counts[d].get(cell[d]) ?? 0) + cell.n);
		}
	}

	for (const d of DIMENSIONS) {
		const names = d === 'country' ? countryNames : labels[d];
		dimensions[d] = [...counts[d]]
			.map(([value, count]) => {
				const label = names?.get(value);
				return label ? { value, label, count } : { value, count };
			})
			.sort((a, b) => b.count - a.count || (a.value < b.value ? -1 : a.value > b.value ? 1 : 0));
	}
	return { total, dimensions };
}

interface LinkRow {
	value: number | string;
	n: number;
	/** json_extract's [name, manufacturers] for a page, null for a variant. */
	label: string | null;
}

/**
 * A link dimension's items. value is what the filter takes: the page ID as
 * text, or the variant as written. Two products sharing a name (each maker's
 * "Boomerang") gain their makers, as the browser's labels do; labels still
 * alike stay so.
 */
export function linkItems(rows: LinkRow[]): Item[] {
	const items = rows.map((r) => {
		const [name, makers] = r.label ? (JSON.parse(r.label) as [string | null, { name?: string }[] | null]) : [null, null];
		return { value: String(r.value), label: name ?? undefined, makers: (makers ?? []).map((x) => x.name).filter(Boolean), count: r.n };
	});
	const seen = new Map<string, number>();
	for (const i of items) if (i.label) seen.set(i.label.toLowerCase(), (seen.get(i.label.toLowerCase()) ?? 0) + 1);
	return items
		.map(({ value, label, makers, count }) => {
			if (label && seen.get(label.toLowerCase())! > 1 && makers.length) label = `${label} (${makers.join(', ')})`;
			return label ? { value, label, count } : { value, count };
		})
		.sort((a, b) => b.count - a.count || ((a.label ?? a.value) < (b.label ?? b.value) ? -1 : (a.label ?? a.value) > (b.label ?? b.value) ? 1 : 0));
}

// Bucket index and edges. The epsilon keeps 0.3 / 0.1 (2.9999…) in bucket 3.
const bucketOf = (v: number, w: number) => Math.floor(v / w + 1e-9);

/** The narrowest width 1, 2 or 5 × 10^exp, from 10^lowestExp up, giving no more than MAX_BUCKETS. */
function fitWidth(min: number, max: number, lowestExp: number): { w: number; exp: number } | null {
	for (let exp = lowestExp; exp <= 12; exp++) {
		for (const mult of [1, 2, 5]) {
			const w = mult * 10 ** exp;
			if (bucketOf(max, w) - bucketOf(min, w) + 1 <= MAX_BUCKETS) return { w, exp };
		}
	}
	return null;
}

/**
 * Whole-number widths first. A width below 1 only when whole ones leave fewer
 * than MIN_BUCKETS bars and the values are not all whole, or inversions (0–14)
 * would land in half-inversion buckets, every other one empty.
 */
export function bucketWidth(min: number, max: number, integral: boolean): { w: number; exp: number } | null {
	if (max <= min) return null;
	let fit = fitWidth(min, max, 0);
	if (fit && !integral && bucketOf(max, fit.w) - bucketOf(min, fit.w) + 1 < MIN_BUCKETS) {
		fit = fitWidth(min, max, -3) ?? fit;
	}
	return fit;
}

/**
 * A histogram from each distinct value and its count. Buckets are dense, zeros
 * included, cut at multiples of the width, and [from, to): the first starts at
 * or below min. One value only (min = max) has no span to divide: no buckets.
 */
export function histogram(unit: string, values: { v: number; n: number }[]): Range | null {
	if (values.length === 0) return null;
	let min = Infinity;
	let max = -Infinity;
	let integral = true;
	for (const { v } of values) {
		if (v < min) min = v;
		if (v > max) max = v;
		if (!Number.isInteger(v)) integral = false;
	}

	const fit = bucketWidth(min, max, integral);
	if (!fit) return { unit, min, max, buckets: [] };

	const { w, exp } = fit;
	const edge = (i: number) => Number((i * w).toFixed(Math.max(0, -exp)));
	const first = bucketOf(min, w);
	const counts = new Array<number>(bucketOf(max, w) - first + 1).fill(0);
	for (const { v, n } of values) counts[bucketOf(v, w) - first] += n;
	return { unit, min, max, buckets: counts.map((count, i) => ({ from: edge(first + i), to: edge(first + i + 1), count })) };
}

export async function attractionFacets(url: URL, env: Env): Promise<{ response: Response; rows: number }> {
	const q = facetQueries(await followMerges(url, env));
	const stats = Object.keys(q.ranges);
	const linked = LINK_DIMENSIONS.filter((d) => q.links[d]);
	const db = env.DATA;
	const [cells, vocab, asOf, names, ...rest] = await db.batch<any>([
		db.prepare(q.cells.sql).bind(...q.cells.binds),
		db.prepare(`SELECT value FROM state WHERE key = 'vocabularies'`),
		db.prepare(`SELECT value FROM state WHERE key = 'data_as_of'`),
		// A placeholder keeps the batch's positions when there is nothing to read.
		q.countryNames ? db.prepare(q.countryNames.sql).bind(...q.countryNames.binds) : db.prepare('SELECT NULL WHERE 0'),
		...stats.map((s) => db.prepare(q.ranges[s].sql).bind(...q.ranges[s].binds)),
		...linked.map((d) => db.prepare(q.links[d]!.sql).bind(...q.links[d]!.binds)),
	]);
	const rangeResults = rest.slice(0, stats.length);

	// Labels are the wiki's, from /v1/vocabularies' source; absent until it is published.
	const labels: Partial<Record<Dimension, Map<string, string>>> = {};
	const vocabRow = vocab.results[0] as { value: string } | undefined;
	if (vocabRow) {
		const data = JSON.parse(vocabRow.value).data as Record<string, { value: string; label: string }[]>;
		for (const d of ['type', 'status', 'location_kind'] as const) {
			if (data[d]) labels[d] = new Map(data[d].map((e) => [e.value, e.label]));
		}
	}

	const countryNames = new Map((names.results as { country: string; country_name: string }[]).map((r) => [r.country, r.country_name]));
	const { total, dimensions } = countDimensions(cells.results as Cell[], q.filters, labels, countryNames);
	const all: Record<string, Item[]> = dimensions;
	linked.forEach((d, i) => {
		all[d] = linkItems(rest[stats.length + i].results as LinkRow[]);
	});
	const ranges: Record<string, Range> = {};
	stats.forEach((stat, i) => {
		const range = histogram(RANGES[stat], rangeResults[i].results as { v: number; n: number }[]);
		if (range) ranges[stat] = range;
	});

	return {
		rows: 0,
		response: json({
			data: { total, dimensions: all, ranges },
			meta: { data_as_of: (asOf.results[0] as { value: string } | undefined)?.value ?? null, view: q.view, licence: LICENCE },
		}),
	};
}
