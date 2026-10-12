import { env } from 'cloudflare:workers';
import { beforeAll, describe, expect, it } from 'vitest';
import { encodeCursor, listQuery } from '../src/attractions';
import { facetQueries } from '../src/facets';
import { FIRST_SEQ_SQL, pageQuery } from '../src/changes';
import { resetData } from './reset';

// D1 bills rows read and rows written, and the free tier allows 5M and 100k a
// day. These pin the cost of the paths a client can repeat at will.

const N = 5000;
const TYPES = ['coaster', 'ride', 'ride', 'ride', 'water_slide'];
const COUNTRIES = ['GB', 'US', 'DE', 'NL', 'FR', 'JP', 'XK'];
const name = (i: number) => `attraction ${String((i * 7919) % N).padStart(5, '0')}`;

async function rowsRead(url: string): Promise<{ read: number; returned: number }> {
	const { sql, binds } = listQuery(new URL(url, 'https://api.coasterpedia.net'));
	const result = await env.DATA.prepare(sql).bind(...binds).all();
	return { read: result.meta.rows_read, returned: result.results.length };
}

beforeAll(async () => {
	await resetData();
	const statements = [];
	for (let start = 1; start <= N; start += 500) {
		const values = [];
		for (let i = start; i < start + 500; i++) {
			// Three in five record a height, as most stats are patchy.
			const height = i % 5 < 3 ? String(((i * 104729) % 15000) / 100) : 'NULL';
			values.push(`(${i}, 1, 1, '${TYPES[i % TYPES.length]}', 'operating', '${COUNTRIES[i % COUNTRIES.length]}', '${name(i)}', 1, '2026-10-11T00:00:00Z', '{}', '{}', ${height})`);
		}
		statements.push(env.DATA.prepare(
			`INSERT INTO attractions (id, in_latest, in_settled, type, status, country, sort_name, revision, updated_at, summary, record, height) VALUES ${values.join(',')}`,
		));
	}
	await env.DATA.batch(statements);
});

describe('a page deep in the list reads its page, not the table before it', () => {
	const middle = encodeCursor('name', name(1), 1); // sort position 1 of N: about half way
	const midName = 'attraction 02500';

	it.each([
		['by name', `/v1/attractions?view=latest&limit=100&cursor=${encodeCursor('name', midName, 0)}`],
		['by name, descending', `/v1/attractions?view=latest&limit=100&sort=-name&cursor=${encodeCursor('name', midName, 0)}`],
		['settled view', `/v1/attractions?limit=100&cursor=${encodeCursor('name', midName, 0)}`],
		['filtered by country', `/v1/attractions?view=latest&limit=100&country=GB&cursor=${encodeCursor('name', midName, 0)}`],
		['filtered by type', `/v1/attractions?view=latest&limit=100&type=coaster&cursor=${encodeCursor('name', midName, 0)}`],
		['cursor on an exact name', `/v1/attractions?view=latest&limit=100&cursor=${middle}`],
		['by height, descending', `/v1/attractions?view=latest&limit=100&sort=-height&cursor=${encodeCursor('height', 75, 0)}`],
		['by height, settled view', `/v1/attractions?limit=100&sort=height&cursor=${encodeCursor('height', 75, 0)}`],
	])('%s', async (_, url) => {
		const { read, returned } = await rowsRead(url);
		expect(returned).toBe(101);
		// A page of 101 rows; a scan from the top would be about 2,500. A filter
		// that walks the name index reads past the rows it skips (7 countries,
		// 5 types), so the bound is per filter, well under the scan.
		expect(read).toBeLessThan(url.includes('country=') || url.includes('type=') ? 1000 : 300);
	});
});

describe('record counts follow the rows', () => {
	const count = async (key: string) =>
		Number((await env.DATA.prepare('SELECT value FROM state WHERE key = ?').bind(key).first<{ value: string }>())!.value);

	it('counts every insert, delete and change of view', async () => {
		expect(await count('count_latest')).toBe(N);
		expect(await count('count_settled')).toBe(N);

		await env.DATA.prepare('DELETE FROM attractions WHERE id <= 10').run();
		expect(await count('count_latest')).toBe(N - 10);

		// A change waiting to settle: the old row leaves latest, a new latest-only row arrives.
		await env.DATA.batch([
			env.DATA.prepare('UPDATE attractions SET in_latest = 0 WHERE id = 11'),
			env.DATA.prepare(`INSERT INTO attractions (id, in_latest, in_settled, type, sort_name, revision, updated_at, summary, record)
				VALUES (11, 1, 0, 'ride', 'x', 2, '2026-10-12T00:00:00Z', '{}', '{}')`),
		]);
		expect(await count('count_latest')).toBe(N - 10);
		expect(await count('count_settled')).toBe(N - 10);

		// An ordinary update that moves no flag or facet writes nothing to the counters.
		const plain = await env.DATA.prepare(`UPDATE attractions SET revision = 2, summary = '{}' WHERE id = 12`).run();
		expect(plain.meta.rows_written).toBe(1);

		// A facet move costs its cells, out of one and into another, and the row's
		// status index entry (0010), in each view the row is in.
		const moved = await env.DATA.prepare(`UPDATE attractions SET status = 'defunct' WHERE id = 12`).run();
		expect(moved.meta.rows_written).toBe(7);
	});
});

describe('facets read the counted cells and the stat they draw, never the table', () => {
	const read = async (url: string) => {
		const q = facetQueries(new URL(url, 'https://api.coasterpedia.net'));
		const cells = await env.DATA.prepare(q.cells.sql).bind(...q.cells.binds).all();
		const height = await env.DATA.prepare(q.ranges.height.sql).bind(...q.ranges.height.binds).all();
		return { cells: cells.meta.rows_read, height: height.meta.rows_read };
	};
	const withHeight = async () =>
		Number((await env.DATA.prepare('SELECT COUNT(*) AS n FROM attractions WHERE in_latest = 1 AND height IS NOT NULL').first<{ n: number }>())!.n);

	it.each([
		['unfiltered', '/v1/attractions/facets?view=latest'],
		['filtered', '/v1/attractions/facets?view=latest&country=GB&type=coaster'],
		['settled', '/v1/attractions/facets'],
	])('%s', async (_, url) => {
		const { cells, height } = await read(url);
		// 3 types × 1–2 statuses × 7 countries, per view: a few dozen cells, not 5,000 rows.
		expect(cells).toBeLessThan(100);
		// The histogram reads the attractions recording a height (three in five), no more.
		expect(height).toBeLessThanOrEqual(await withHeight() + 1);
	});
});

describe('the change feed reads its page, not the log', () => {
	const LINES = 20000;

	beforeAll(async () => {
		await env.DATA.prepare('DELETE FROM changes').run();
		const statements = [];
		for (let start = 1; start <= LINES; start += 1000) {
			const values = [];
			for (let i = start; i < start + 1000; i++) {
				// A minute apart; one line in four latest only, as untrusted edits are.
				const at = new Date(Date.UTC(2026, 7, 1) + i * 60_000).toISOString().replace(/\.\d{3}Z$/, 'Z');
				values.push(`(${i}, 'attraction', ${i}, 'upsert', 1, '${at}', 1, ${i % 4 === 0 ? 0 : 1})`);
			}
			statements.push(env.DATA.prepare(
				`INSERT INTO changes (seq, kind, id, op, revision, at, in_latest, in_settled) VALUES ${values.join(',')}`,
			));
		}
		await env.DATA.batch(statements);
	});

	it('finds where a time falls by halving, not by scanning', async () => {
		const at = new Date(Date.UTC(2026, 7, 1) + 12345 * 60_000).toISOString().replace(/\.\d{3}Z$/, 'Z');
		const result = await env.DATA.prepare(FIRST_SEQ_SQL).bind(at).all<{ seq: number }>();
		expect(result.results[0].seq).toBe(12345);
		// log2(20,000) is about 15 halvings; a scan would be 12,345 rows.
		expect(result.meta.rows_read).toBeLessThan(100);
	});

	it.each([
		['latest', 'latest' as const],
		['settled', 'settled' as const],
	])('a page deep in the log, %s view', async (_, view) => {
		const { sql, binds } = pageQuery(10000, 11000, view, ['attraction'], 100);
		const result = await env.DATA.prepare(sql).bind(...binds).all();
		expect(result.results).toHaveLength(101);
		expect(result.meta.rows_read).toBeLessThan(200);
	});

	it('a settled page over latest-only lines stops at the scan bound', async () => {
		await env.DATA.prepare('UPDATE changes SET in_settled = 0 WHERE seq BETWEEN 15001 AND 20000').run();
		const { sql, binds } = pageQuery(15000, 16000, 'settled', [], 100);
		const result = await env.DATA.prepare(sql).bind(...binds).all();
		expect(result.results).toHaveLength(0);
		expect(result.meta.rows_read).toBeLessThanOrEqual(1001);
	});
});
