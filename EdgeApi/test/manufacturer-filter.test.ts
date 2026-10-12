import { env, exports } from 'cloudflare:workers';
import { beforeAll, beforeEach, describe, expect, it } from 'vitest';
import { encodeCursor, listQuery } from '../src/attractions';
import { sha256Hex } from '../src/auth';
import { facetQueries } from '../src/facets';
import { resetData } from './reset';

const BASE = 'https://api.coasterpedia.net';
const KEY = 'cpk_manufacturerfilter';

const ARROW = 1829;
const ZAMPERLA = 3000;

type Ref = { id?: number; name: string };

// Wire.Upsert's shape (Projector.Tests pins it), with the manufacturers and a height.
function upsert(id: number, name: string, type: string, status: string, country: { code: string; name: string } | null,
	manufacturers: Ref[], height: number | null = null, revision = 1, settlesAt: string | null = null) {
	const summary: Record<string, unknown> = {
		id, type, name, status, ...(country ? { country } : {}), manufacturers,
		...(height != null ? { stats: { height: { value: height, unit: 'm' } } } : {}),
		updated_at: '2026-10-12T12:00:00Z',
	};
	return {
		kind: 'attraction', id, revision, updated_at: '2026-10-12T12:00:00Z',
		settled: settlesAt == null, settles_at: settlesAt,
		sort_name: name.toLowerCase(),
		sort_values: { height, speed: null, length: null },
		filters: { type, status, country: country?.code ?? null },
		summary,
		record: { ...summary, title: name, revision },
	};
}

const ingest = (body: Record<string, unknown>) => exports.default.fetch(new Request(`${BASE}/internal/ingest`, {
	method: 'POST',
	headers: { 'content-type': 'application/json', 'x-dev-ingest-token': 'test-ingest-token' },
	body: JSON.stringify({ as_of: '2026-10-12T12:00:00Z', upsert: [], delete: [], ...body }),
}));

const get = (path: string) =>
	exports.default.fetch(new Request(BASE + path, { redirect: 'manual', headers: { authorization: `Bearer ${KEY}` } }));

const ids = async (q: string) => {
	const res = await get(`/v1/attractions?${q}`);
	expect(res.status).toBe(200);
	return (await res.json<any>()).data.map((a: any) => a.id);
};

const US = { code: 'US', name: 'USA' };
const UK = { code: 'GB', name: 'UK' };
const DE = { code: 'DE', name: 'Germany' };

const ALPHA = upsert(1, 'Alpha', 'coaster', 'operating', US, [{ id: ARROW, name: 'Arrow Dynamics' }], 30);
// Two links that resolve to one page, and a second manufacturer.
const BRAVO = upsert(2, 'Bravo', 'ride', 'operating', UK,
	[{ id: ZAMPERLA, name: 'Zamperla' }, { id: ARROW, name: 'Arrow Development' }, { id: ARROW, name: 'Arrow Dynamics' }]);
// Unlinked text has no ID, so no filter can select it.
const CHARLIE = upsert(3, 'Charlie', 'coaster', 'defunct', US, [{ name: 'Self-built' }]);
// Never settled: latest only.
const DELTA = upsert(4, 'Delta', 'coaster', 'operating', DE, [{ id: ARROW, name: 'Arrow Dynamics' }], 50, 1, '2026-10-19T12:00:00Z');
const ECHO = upsert(5, 'Echo', 'water_slide', 'operating', US, [{ id: ZAMPERLA, name: 'Zamperla' }], 10);

describe('/v1/attractions?manufacturer=', () => {
	beforeEach(async () => {
		await resetData();
		await env.KEYS.put(`key:${await sha256Hex(KEY)}`, JSON.stringify({ account: 'mfr', tier: 'beta', status: 'active' }));
		expect((await ingest({ upsert: [ALPHA, BRAVO, CHARLIE, DELTA, ECHO] })).status).toBe(200);
	});

	it('lists every attraction linking the manufacturer, once each, in each view', async () => {
		expect(await ids(`view=latest&manufacturer=${ARROW}`)).toEqual([1, 2, 4]);
		expect(await ids(`manufacturer=${ARROW}`)).toEqual([1, 2]);
		expect(await ids(`view=latest&manufacturer=${ZAMPERLA}`)).toEqual([2, 5]);
		// A manufacturer nobody links, and one that is no page at all.
		expect(await ids('view=latest&manufacturer=77')).toEqual([]);
	});

	it('serves the same summary as the unfiltered list', async () => {
		const filtered = await (await get(`/v1/attractions?view=latest&manufacturer=${ZAMPERLA}`)).json<any>();
		expect(filtered.data).toEqual([BRAVO.summary, ECHO.summary]);
	});

	it('combines with the other filters', async () => {
		expect(await ids(`view=latest&manufacturer=${ARROW}&type=coaster`)).toEqual([1, 4]);
		expect(await ids(`view=latest&manufacturer=${ARROW}&country=gb`)).toEqual([2]);
		expect(await ids(`view=latest&manufacturer=${ARROW}&status=defunct`)).toEqual([]);
	});

	it('sorts by a stat, leaving out what does not record it, and pages both sorts', async () => {
		expect(await ids(`view=latest&manufacturer=${ARROW}&sort=-height`)).toEqual([4, 1]);
		expect(await ids(`view=latest&manufacturer=${ARROW}&sort=height`)).toEqual([1, 4]);

		const first = await (await get(`/v1/attractions?view=latest&manufacturer=${ARROW}&limit=2`)).json<any>();
		expect(first.data.map((a: any) => a.id)).toEqual([1, 2]);
		const second = await (await get(`/v1/attractions?view=latest&manufacturer=${ARROW}&limit=2&cursor=${first.meta.next_cursor}`)).json<any>();
		expect(second.data.map((a: any) => a.id)).toEqual([4]);
		expect(second.meta.next_cursor).toBeNull();

		const tallest = await (await get(`/v1/attractions?view=latest&manufacturer=${ARROW}&sort=-height&limit=1`)).json<any>();
		const next = await (await get(`/v1/attractions?view=latest&manufacturer=${ARROW}&sort=-height&limit=1&cursor=${tallest.meta.next_cursor}`)).json<any>();
		expect(next.data.map((a: any) => a.id)).toEqual([1]);
	});

	it('follows an edit: a manufacturer changed, a status changed, a height added', async () => {
		await ingest({ upsert: [upsert(1, 'Alpha', 'coaster', 'defunct', US, [{ id: ZAMPERLA, name: 'Zamperla' }], 30, 2)] });
		expect(await ids(`view=latest&manufacturer=${ARROW}`)).toEqual([2, 4]);
		expect(await ids(`view=latest&manufacturer=${ZAMPERLA}&status=defunct`)).toEqual([1]);

		await ingest({ upsert: [{ ...BRAVO, revision: 2, sort_values: { height: 12, speed: null, length: null } }] });
		expect(await ids(`view=latest&manufacturer=${ZAMPERLA}&sort=height`)).toEqual([5, 2, 1]);
	});

	it('keeps a pending change out of the settled view until it settles, and back out on a discard', async () => {
		// An untrusted edit moves Alpha to Zamperla.
		await ingest({ upsert: [upsert(1, 'Alpha', 'coaster', 'operating', US, [{ id: ZAMPERLA, name: 'Zamperla' }], 30, 2, '2026-10-19T12:00:00Z')] });
		expect(await ids(`view=latest&manufacturer=${ZAMPERLA}`)).toEqual([1, 2, 5]);
		expect(await ids(`manufacturer=${ZAMPERLA}`)).toEqual([2, 5]);
		expect(await ids(`manufacturer=${ARROW}`)).toEqual([1, 2]);
		expect(await ids(`view=latest&manufacturer=${ARROW}`)).toEqual([2, 4]);

		await ingest({ discard: [{ kind: 'attraction', id: 1 }] });
		expect(await ids(`view=latest&manufacturer=${ARROW}`)).toEqual([1, 2, 4]);
		expect(await ids(`view=latest&manufacturer=${ZAMPERLA}`)).toEqual([2, 5]);

		await ingest({ upsert: [upsert(1, 'Alpha', 'coaster', 'operating', US, [{ id: ZAMPERLA, name: 'Zamperla' }], 30, 3, '2026-10-19T12:00:00Z')] });
		await ingest({ promote: [{ kind: 'attraction', id: 1 }] });
		expect(await ids(`manufacturer=${ZAMPERLA}`)).toEqual([1, 2, 5]);
		expect(await ids(`manufacturer=${ARROW}`)).toEqual([2]);
	});

	it('drops a deleted attraction', async () => {
		await ingest({ delete: [{ kind: 'attraction', id: 2, merged_into: null }] });
		expect(await ids(`view=latest&manufacturer=${ARROW}`)).toEqual([1, 4]);
		const { results } = await env.DATA.prepare('SELECT COUNT(*) AS n FROM attraction_manufacturers WHERE id = 2').all<{ n: number }>();
		expect(results[0].n).toBe(0);
	});

	it('answers a merged manufacturer with the one it merged into', async () => {
		await ingest({ delete: [{ kind: 'manufacturer', id: 99101, merged_into: ARROW }] });
		expect(await ids('view=latest&manufacturer=99101')).toEqual([1, 2, 4]);
	});

	it.each([
		['not an ID', 'manufacturer=arrow-dynamics'],
		['two IDs', `manufacturer=${ARROW},${ZAMPERLA}`],
		['zero', 'manufacturer=0'],
	])('refuses a manufacturer that is %s', async (_, q) => {
		const res = await get(`/v1/attractions?${q}`);
		expect(res.status).toBe(400);
		expect((await res.json<any>()).type).toMatch(/parameter-invalid$/);
	});

	it('narrows the facets, counting each dimension with its own filter left out', async () => {
		const body = await (await get(`/v1/attractions/facets?view=latest&manufacturer=${ARROW}&type=coaster`)).json<any>();
		expect(body.data.total).toBe(2);
		// type is left out of its own list, so Bravo's ride still shows.
		expect(body.data.dimensions.type).toEqual([{ value: 'coaster', count: 2 }, { value: 'ride', count: 1 }]);
		// Country names come from the whole view's cells.
		expect(body.data.dimensions.country).toEqual([{ value: 'DE', label: 'Germany', count: 1 }, { value: 'US', label: 'USA', count: 1 }]);
		expect(body.data.ranges.height).toMatchObject({ unit: 'm', min: 30, max: 50 });

		const settled = await (await get(`/v1/attractions/facets?manufacturer=${ARROW}`)).json<any>();
		expect(settled.data.total).toBe(2);
		expect(settled.data.dimensions.type).toEqual([{ value: 'coaster', count: 1 }, { value: 'ride', count: 1 }]);
		// One height only: a single value has no buckets.
		expect(settled.data.ranges.height).toEqual({ unit: 'm', min: 30, max: 30, buckets: [] });
	});

	it('answers facets for a merged manufacturer, and refuses a bad one', async () => {
		await ingest({ delete: [{ kind: 'manufacturer', id: 99101, merged_into: ARROW }] });
		expect((await (await get('/v1/attractions/facets?view=latest&manufacturer=99101')).json<any>()).data.total).toBe(3);
		expect((await get('/v1/attractions/facets?manufacturer=x')).status).toBe(400);
	});
});

describe('a manufacturer-filtered read is bounded by the manufacturer, not the table', () => {
	const N = 6000;
	const BIG = 7;
	const SMALL = 8;
	const TYPES = ['coaster', 'ride', 'ride', 'ride', 'water_slide'];
	const COUNTRIES = ['GB', 'US', 'DE', 'NL', 'FR', 'JP', 'XK'];
	const name = (i: number) => `attraction ${String((i * 7919) % N).padStart(5, '0')}`;

	beforeAll(async () => {
		await resetData();
		const statements = [];
		for (let start = 1; start <= N; start += 500) {
			const values = [];
			for (let i = start; i < start + 500; i++) {
				// One in three is the big manufacturer's (2,000, as Zamperla's 1,900);
				// one in thirty also links a small one.
				const links = [i % 3 === 0 ? `{"id":${BIG},"name":"Big"}` : '{"name":"Other"}', ...(i % 30 === 0 ? [`{"id":${SMALL},"name":"Small"}`] : [])];
				const height = i % 5 < 3 ? String(((i * 104729) % 15000) / 100) : 'NULL';
				values.push(`(${i}, 1, 1, '${TYPES[i % TYPES.length]}', 'operating', '${COUNTRIES[i % COUNTRIES.length]}', '${name(i)}', 1, '2026-10-12T00:00:00Z', '{"manufacturers":[${links.join(',')}]}', '{}', ${height})`);
			}
			statements.push(env.DATA.prepare(
				`INSERT INTO attractions (id, in_latest, in_settled, type, status, country, sort_name, revision, updated_at, summary, record, height) VALUES ${values.join(',')}`,
			));
		}
		await env.DATA.batch(statements);
	});

	it('fills the links from the summaries by trigger', async () => {
		const { results } = await env.DATA.prepare('SELECT manufacturer, COUNT(*) AS n FROM attraction_manufacturers GROUP BY manufacturer ORDER BY manufacturer').all();
		expect(results).toEqual([{ manufacturer: BIG, n: 2000 }, { manufacturer: SMALL, n: 200 }]);
	});

	it("builds the same links in the migration's backfill as the triggers do", async () => {
		const migration = env.TEST_MIGRATIONS.find((m) => m.name.startsWith('0008_'))!;
		const backfill = migration.queries.find((q) => q.includes('FROM attractions a, json_each'))!;
		const links = `SELECT id, in_latest, manufacturer, in_settled, type, status, country, sort_name, height FROM attraction_manufacturers ORDER BY id, manufacturer`;
		const byTrigger = (await env.DATA.prepare(links).all()).results;
		await env.DATA.prepare('DELETE FROM attraction_manufacturers').run();
		await env.DATA.prepare(backfill).run();
		expect((await env.DATA.prepare(links).all()).results).toEqual(byTrigger);
	});

	const mid = encodeCursor('name', 'attraction 03000', 0);
	it.each([
		['by name, deep', `/v1/attractions?view=latest&limit=100&manufacturer=${BIG}&cursor=${mid}`, 101, 350],
		['by name, settled, descending', `/v1/attractions?limit=100&sort=-name&manufacturer=${BIG}&cursor=${mid}`, 101, 350],
		['by height, deep', `/v1/attractions?view=latest&limit=100&sort=-height&manufacturer=${BIG}&cursor=${encodeCursor('height', 75, 0)}`, 101, 350],
		// Walks the big manufacturer's range past the rows it skips (5 types), never the table.
		['with a type filter', `/v1/attractions?view=latest&limit=100&type=coaster&manufacturer=${BIG}&cursor=${mid}`, 101, 1200],
		// The whole small manufacturer is fewer rows than a page.
		['a small manufacturer', `/v1/attractions?view=latest&limit=100&manufacturer=${SMALL}`, 101, 350],
	])('%s', async (_, url, returned, bound) => {
		const { sql, binds } = listQuery(new URL(url, BASE));
		const result = await env.DATA.prepare(sql).bind(...binds).all();
		expect(result.results.length).toBe(returned);
		expect(result.meta.rows_read).toBeLessThan(bound);
	});

	it('counts facets from the manufacturer\'s own rows', async () => {
		const q = facetQueries(new URL(`/v1/attractions/facets?view=latest&manufacturer=${BIG}`, BASE));
		const cells = await env.DATA.prepare(q.cells.sql).bind(...q.cells.binds).all<{ n: number }>();
		expect(cells.results.reduce((sum, c) => sum + c.n, 0)).toBe(2000);
		// Twice its links: the covering index, then the GROUP BY's temp b-tree,
		// which D1 counts too. Bounded by the manufacturer either way.
		expect(cells.meta.rows_read).toBeLessThan(4100);
		const height = await env.DATA.prepare(q.ranges.height.sql).bind(...q.ranges.height.binds).all();
		expect(height.meta.rows_read).toBeLessThan(1300);
	});
});
