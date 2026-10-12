import { env } from 'cloudflare:workers';
import { beforeAll, describe, expect, it } from 'vitest';
import { encodeCursor, listQuery } from '../src/attractions';
import { facetQueries } from '../src/facets';
import { resetData } from './reset';

// cost.test.ts spreads types and countries evenly, which hid two paths that
// read most of the live table (DR-010 §12.12). Here the mix is the wiki's:
// mostly rides, a status rarer than one in a thousand, heights mostly on
// coasters. Each path must read about its page, and the plan must say why.

const N = 6000;
const type = (i: number) => (i % 20 < 4 ? 'coaster' : i % 20 < 17 ? 'ride' : 'water_slide');
const status = (i: number) =>
	i % 2000 === 7 ? 'planned' : i % 125 === 3 ? 'under_construction' : i % 4 === 1 ? 'defunct' : 'operating';
// Nine coasters in ten record a height, one ride in twenty, three slides in ten.
const recordsHeight = (i: number) => ({ coaster: i % 10 !== 0, ride: i % 20 === 5, water_slide: i % 10 < 3 })[type(i)];

const count = (where: (i: number) => boolean) => Array.from({ length: N }, (_, k) => k + 1).filter(where).length;

async function measure(url: string): Promise<{ read: number; returned: number; plan: string }> {
	const { sql, binds } = listQuery(new URL(url, 'https://api.coasterpedia.net'));
	const result = await env.DATA.prepare(sql).bind(...binds).all();
	const plan = await env.DATA.prepare(`EXPLAIN QUERY PLAN ${sql}`).bind(...binds).all<{ detail: string }>();
	return { read: result.meta.rows_read, returned: result.results.length, plan: plan.results.map((r) => r.detail).join(' | ') };
}

beforeAll(async () => {
	await resetData();
	const statements = [];
	for (let start = 1; start <= N; start += 500) {
		const values = [];
		for (let i = start; i < start + 500; i++) {
			const height = recordsHeight(i) ? String(((i * 104729) % 15000) / 100) : 'NULL';
			values.push(`(${i}, 1, 1, '${type(i)}', '${status(i)}', 'GB', 'attraction ${String((i * 7919) % N).padStart(5, '0')}', 1, '2026-10-12T00:00:00Z', '{}', '{}', ${height})`);
		}
		statements.push(env.DATA.prepare(
			`INSERT INTO attractions (id, in_latest, in_settled, type, status, country, sort_name, revision, updated_at, summary, record, height) VALUES ${values.join(',')}`,
		));
	}
	await env.DATA.batch(statements);
});

describe('a stat sort under a type filter reads its page, not the type', () => {
	it.each([
		['rides, tallest first', '/v1/attractions?limit=100&sort=-height&type=ride'],
		['coasters, shortest first', '/v1/attractions?limit=100&sort=height&type=coaster'],
		['latest view', '/v1/attractions?view=latest&limit=100&sort=-height&type=coaster'],
		['a page deep in', `/v1/attractions?limit=100&sort=-height&type=coaster&cursor=${encodeCursor('height', 75, 0)}`],
	])('%s', async (_, url) => {
		const { read, returned, plan } = await measure(url);
		expect(returned).toBe(101);
		expect(plan).toMatch(/_by_type_height \(type=\?/);
		expect(plan).not.toMatch(/TEMP B-TREE/);
		// The type's rows alone would be 3,900 rides or 1,200 coasters.
		expect(read).toBeLessThan(150);
	});
});

describe('a status filter reads its status, not the table', () => {
	it('a rare status', async () => {
		const { read, returned, plan } = await measure('/v1/attractions?limit=100&status=planned');
		expect(returned).toBe(count((i) => status(i) === 'planned'));
		expect(plan).toMatch(/_settled_by_status \(status=\?/);
		expect(read).toBeLessThan(10);
	});

	it('a common one still reads one page', async () => {
		const { read, returned } = await measure('/v1/attractions?limit=100&status=operating');
		expect(returned).toBe(101);
		expect(read).toBeLessThan(150);
	});

	it('with a type, bounded by the status', async () => {
		const { read, returned } = await measure('/v1/attractions?limit=100&status=under_construction&type=coaster');
		expect(returned).toBe(count((i) => status(i) === 'under_construction' && type(i) === 'coaster'));
		// Status walks 48 rows; the type's name index would walk 1,200 coasters.
		expect(read).toBeLessThanOrEqual(count((i) => status(i) === 'under_construction') + 1);
	});
});

describe('the status index stays out where it would read the whole status', () => {
	const withHeight = count(recordsHeight);

	it('under a stat sort, which walks the stat instead', async () => {
		const { read, returned, plan } = await measure('/v1/attractions?limit=100&status=operating&sort=-height');
		expect(returned).toBe(101);
		expect(plan).not.toMatch(/_by_status/);
		// The status index would read all 4,000 operating rows and sort them.
		expect(read).toBeLessThanOrEqual(withHeight);
	});

	it('for several statuses, which walk the name index', async () => {
		const { read, returned, plan } = await measure('/v1/attractions?limit=100&status=operating,defunct');
		expect(returned).toBe(101);
		expect(plan).not.toMatch(/TEMP B-TREE/);
		expect(read).toBeLessThan(150);
	});

	it('in a histogram', async () => {
		const q = facetQueries(new URL('/v1/attractions/facets?status=operating', 'https://api.coasterpedia.net'));
		const result = await env.DATA.prepare(q.ranges.height.sql).bind(...q.ranges.height.binds).all();
		expect(result.meta.rows_read).toBeLessThanOrEqual(withHeight + 1);
	});
});

describe('a type-filtered histogram reads the type rows that record the stat', () => {
	it('heights of rides', async () => {
		const q = facetQueries(new URL('/v1/attractions/facets?type=ride', 'https://api.coasterpedia.net'));
		const result = await env.DATA.prepare(q.ranges.height.sql).bind(...q.ranges.height.binds).all();
		// Grouped in index order: no sort pass, so no second read of each row.
		expect(result.meta.rows_read).toBeLessThanOrEqual(count((i) => type(i) === 'ride' && recordsHeight(i)) + 1);
	});
});
