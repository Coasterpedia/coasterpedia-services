import { checkParams, FLAG, followMerges, linkFilter, list, openedParams, parkParam, stateParam, viewParam, type View } from './attractions';
import type { Env } from './env';
import { json, LICENCE } from './http';

/**
 * GET /v1/attractions/facets (DR-010 §12.6): counts per filter value for the
 * set /v1/attractions would list, and a histogram per sortable stat. The
 * Attractions Browser's options call, answered from D1.
 */

const FACET_PARAMS = new Set([
	'type', 'status', 'country', 'location_kind', 'park', 'state', 'opened_from', 'opened_to', 'manufacturer', 'product', 'variant', 'view',
]);

/**
 * The facet dimensions, each a column of attraction_facets. The other filters
 * (a park, a state, the opened range, a manufacturer or product) narrow every
 * list without being one.
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
}

/** The queries for a request, apart from running them, so tests can measure what they read. */
export function facetQueries(url: URL): FacetQueries {
	checkParams(url, FACET_PARAMS);
	const view = viewParam(url);
	const filters = filtersOf(url);

	// No stat filter exists yet, so every range is measured under the full
	// filter. A stat filter, when one comes, leaves out its own range, as the
	// browser's whereWithout does: bounds measured inside the cut can't be widened.
	//
	// What narrows the set without being a dimension is read from its own index,
	// as on the list: a manufacturer's or product's links (0008, 0011) joined to
	// the row for the columns they lack, else a park, a state or the opened
	// range (0012). Its counts then come from its own rows, never the table.
	const link = linkFilter(url);
	const park = parkParam(url);
	const states = stateParam(url);
	const opened = openedParams(url);
	const driver = link != null ? 'link'
		: park != null ? 'park'
		: states.length > 0 ? 'state'
		: opened.from != null ? 'opened'
		: opened.to != null ? 'opened_latest'
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
		: '';
	const table = link == null
		? `attractions${forced}`
		: `${link.table} m JOIN attractions a ON a.id = m.id AND a.in_latest = m.in_latest`;
	// A link carries every stat and dimension but the kind and the narrowing
	// columns: without those, the histograms read its index alone, as before.
	const needsRow = park != null || states.length > 0 || opened.from != null || opened.to != null || filters.location_kind != null;
	const rangeTable = link != null && !needsRow ? `${link.table} m` : table;
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
	const db = env.DATA;
	const [cells, vocab, asOf, names, ...rangeResults] = await db.batch<any>([
		db.prepare(q.cells.sql).bind(...q.cells.binds),
		db.prepare(`SELECT value FROM state WHERE key = 'vocabularies'`),
		db.prepare(`SELECT value FROM state WHERE key = 'data_as_of'`),
		// A placeholder keeps the batch's positions when there is nothing to read.
		q.countryNames ? db.prepare(q.countryNames.sql).bind(...q.countryNames.binds) : db.prepare('SELECT NULL WHERE 0'),
		...stats.map((s) => db.prepare(q.ranges[s].sql).bind(...q.ranges[s].binds)),
	]);

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
	const ranges: Record<string, Range> = {};
	stats.forEach((stat, i) => {
		const range = histogram(RANGES[stat], rangeResults[i].results as { v: number; n: number }[]);
		if (range) ranges[stat] = range;
	});

	return {
		rows: 0,
		response: json({
			data: { total, dimensions, ranges },
			meta: { data_as_of: (asOf.results[0] as { value: string } | undefined)?.value ?? null, view: q.view, licence: LICENCE },
		}),
	};
}
