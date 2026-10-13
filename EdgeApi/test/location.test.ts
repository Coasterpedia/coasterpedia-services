import { env, exports } from 'cloudflare:workers';
import { beforeAll, describe, expect, it } from 'vitest';
import { encodeCursor, listQuery } from '../src/attractions';
import { sha256Hex } from '../src/auth';
import { facetQueries } from '../src/facets';
import { resetData } from './reset';
// Written by the Projector's GoldenBatchTests: the real wire shape, not a hand copy.
import attractions from './fixtures/projector-batch.json';
import parks from './fixtures/projector-park-batch.json';

const BASE = 'https://api.coasterpedia.net';
const KEY = 'cpk_location';
// The refusals' own account: the limiter counts per account, 60 a minute.
const REFUSED_KEY = 'cpk_locationrefused';

const get = (path: string, key = KEY) =>
	exports.default.fetch(new Request(BASE + path, { redirect: 'manual', headers: { authorization: `Bearer ${key}` } }));

const ingest = (body: unknown) => exports.default.fetch(new Request(`${BASE}/internal/ingest`, {
	method: 'POST',
	headers: { 'content-type': 'application/json', 'x-dev-ingest-token': 'test-ingest-token' },
	body: JSON.stringify(body),
}));

const ids = async (path: string) => {
	const res = await get(path);
	expect(res.status).toBe(200);
	return (await res.json<any>()).data.map((a: any) => a.id);
};

// The fixture's attractions: Gold Rusher 171 (1971), Batman 165 (1994) and Colossus 169 (1978) at Six Flags
// Magic Mountain (163, California); Example Twin 9101 travelling ("[..2011]", pending); Over the Hill 19643 on
// loan at Pleasurewood Hills (9136, 2019-05); SkyRide 66486 on Carnival Horizon (67009, 2018); Élan 9102
// standalone ("199X").
describe('where an attraction is, and when it opened', () => {
	beforeAll(async () => {
		await resetData();
		for (const [key, account] of [[KEY, 'location'], [REFUSED_KEY, 'location-refused']]) {
			await env.KEYS.put(`key:${await sha256Hex(key)}`, JSON.stringify({ account, tier: 'beta', status: 'active' }));
		}
		expect((await ingest(parks)).status).toBe(200);
		expect((await ingest(attractions)).status).toBe(200);
	});

	it('summarises the park, the kind and the dates', async () => {
		const body = await (await get('/v1/attractions?view=latest&park=163&limit=1')).json<any>();
		expect(body.data[0]).toMatchObject({
			id: 165, park: { id: 163, name: 'Six Flags Magic Mountain' }, location_kind: 'park',
			opened: { edtf: '1994-03-26', earliest: '1994-03-26', latest: '1994-03-26' },
		});
	});

	it("filters by where it is now: a park, a kind, a state", async () => {
		expect(await ids('/v1/attractions?view=latest&park=163')).toEqual([165, 169, 171]);
		expect(await ids('/v1/attractions?view=latest&park=163&status=defunct')).toEqual([169]);
		expect(await ids('/v1/attractions?view=latest&location_kind=travelling')).toEqual([9101]);
		// Name order is accent-blind: Élan sorts as elan.
		expect(await ids('/v1/attractions?view=latest&location_kind=travelling,standalone')).toEqual([9102, 9101]);
		// Settled leaves out Example Twin, which waits.
		expect(await ids('/v1/attractions?location_kind=travelling')).toEqual([]);
		expect(await ids('/v1/attractions?view=latest&state=California')).toEqual([165, 169, 171]);
		expect(await ids('/v1/attractions?view=latest&state=California&state=Ohio&type=coaster')).toEqual([165, 169, 171]);
		// A past stint is history, not where it is: Example Twin was at Cedar Point.
		expect(await ids('/v1/attractions?view=latest&park=182')).toEqual([]);
	});

	it('matches an opening only when it certainly falls in the years', async () => {
		expect(await ids('/v1/attractions?view=latest&opened_from=1990&opened_to=1999')).toEqual([165, 9102]);
		// "[..2011]" certainly opened by 2011, but not by 2010.
		expect(await ids('/v1/attractions?view=latest&opened_to=2011')).toEqual([165, 169, 9102, 9101, 171]);
		expect(await ids('/v1/attractions?view=latest&opened_to=2010')).toEqual([165, 169, 9102, 171]);
		// ...and it may have opened before 2000, so opened_from leaves it out.
		expect(await ids('/v1/attractions?view=latest&opened_from=2000')).toEqual([19643, 66486]);
		expect(await ids('/v1/attractions?view=latest&opened_from=1970&opened_to=1979&park=163')).toEqual([169, 171]);
	});

	it('sorts by opening, leaving out what has no earliest day, and pages', async () => {
		expect(await ids('/v1/attractions?view=latest&sort=opened')).toEqual([171, 169, 9102, 165, 66486, 19643]);
		expect(await ids('/v1/attractions?view=latest&sort=-opened&type=coaster')).toEqual([165, 169, 171]);
		const first = await (await get('/v1/attractions?view=latest&sort=opened&limit=2')).json<any>();
		const next = await (await get(`/v1/attractions?view=latest&sort=opened&limit=2&cursor=${first.meta.next_cursor}`)).json<any>();
		expect(next.data.map((a: any) => a.id)).toEqual([9102, 165]);
		expect((await get(`/v1/attractions?view=latest&sort=name&cursor=${first.meta.next_cursor}`)).status).toBe(400);
	});

	it("lists a park's attractions under the park", async () => {
		expect(await ids('/v1/parks/67009/attractions')).toEqual([66486]);
		const none = await get('/v1/parks/182/attractions');
		expect(none.status).toBe(200);
		expect((await none.json<any>()).data).toEqual([]);
		const res = await get('/v1/parks/99001/attractions?type=ride');
		expect(res.status).toBe(301);
		expect(res.headers.get('location')).toBe(`${BASE}/v1/parks/182/attractions?type=ride`);
		// Six Flags Magic Mountain is linked but not a park this fixture holds.
		expect((await get('/v1/parks/163/attractions')).status).toBe(404);
	});

	it('counts location kinds as a facet, and narrows the facets to a park or the years', async () => {
		const all = (await (await get('/v1/attractions/facets?view=latest')).json<any>()).data;
		expect(all.dimensions.location_kind).toEqual([
			{ value: 'park', count: 5 }, { value: 'standalone', count: 1 }, { value: 'travelling', count: 1 },
		]);
		const kind = (await (await get('/v1/attractions/facets?view=latest&location_kind=park')).json<any>()).data;
		expect(kind.total).toBe(5);
		// Its own filter left out.
		expect(kind.dimensions.location_kind).toHaveLength(3);

		const park = (await (await get('/v1/attractions/facets?view=latest&park=163')).json<any>()).data;
		expect(park.total).toBe(3);
		expect(park.dimensions.status).toEqual([
			{ value: 'operating', label: 'Operating', count: 2 }, { value: 'defunct', label: 'Defunct', count: 1 },
		]);
		expect(park.dimensions.country).toEqual([{ value: 'US', label: 'USA', count: 3 }]);
		expect(park.ranges.height).toMatchObject({ min: 21.3, max: 38.1 });

		const nineties = (await (await get('/v1/attractions/facets?view=latest&opened_from=1990&opened_to=1999')).json<any>()).data;
		expect(nineties.total).toBe(2);
	});

	it.each([
		['an include that is not one of the three', '/v1/parks/67009/attractions?include=some', 400, 'parameter-invalid'],
		['a second park under a park', '/v1/parks/67009/attractions?park=182', 400, 'parameter-unknown'],
		['two parks', '/v1/attractions?park=163,182', 400, 'parameter-invalid'],
		['a year that is not one', '/v1/attractions?opened_from=94', 400, 'parameter-invalid'],
		['a range the wrong way round', '/v1/attractions?opened_from=2000&opened_to=1990', 400, 'parameter-invalid'],
		['a comma-joined state', '/v1/attractions?state=', 400, 'parameter-invalid'],
		['a park ID that is not a number', '/v1/parks/cedar-point/attractions', 404, 'not-found'],
	])('refuses %s', async (_, path, status, type) => {
		const res = await get(path, REFUSED_KEY);
		expect(res.status).toBe(status);
		expect((await res.json<any>()).type).toMatch(new RegExp(`${type}$`));
	});
});

// Skewed as the wiki is (§12.12): most attractions at a park, a fifth
// travelling, a handful stored; parks of 40; one state in eight; openings
// mostly since 1990. Each path reads about its page or its filter's rows.
describe('the new filters read their own rows, not the table', () => {
	const N = 6000;
	const type = (i: number) => (i % 20 < 4 ? 'coaster' : i % 20 < 17 ? 'ride' : 'water_slide');
	const kind = (i: number) => (i % 100 === 7 ? 'storage' : i % 5 === 1 ? 'travelling' : 'park');
	const park = (i: number) => (kind(i) === 'travelling' ? null : 1000 + (i % 150));
	const state = (i: number) => (i % 8 === 0 ? `'State ${i % 24}'` : 'NULL');
	// One in ten opened before 1960, the rest from 1990.
	const year = (i: number) => (i % 10 === 3 ? 1900 + (i % 60) : 1990 + (i % 36));
	const recordsHeight = (i: number) => i % 3 !== 0;

	async function measure(url: string): Promise<{ read: number; returned: number; plan: string }> {
		const { sql, binds } = listQuery(new URL(url, BASE));
		const result = await env.DATA.prepare(sql).bind(...binds).all();
		const plan = await env.DATA.prepare(`EXPLAIN QUERY PLAN ${sql}`).bind(...binds).all<{ detail: string }>();
		return { read: result.meta.rows_read, returned: result.results.length, plan: plan.results.map((r) => r.detail).join(' | ') };
	}

	beforeAll(async () => {
		await resetData();
		const statements = [];
		for (let start = 1; start <= N; start += 400) {
			const values = [];
			for (let i = start; i < start + 400; i++) {
				const height = recordsHeight(i) ? String(((i * 104729) % 15000) / 100) : 'NULL';
				const y = year(i);
				values.push(`(${i}, 1, 1, '${type(i)}', 'operating', 'GB', 'attraction ${String((i * 7919) % N).padStart(5, '0')}', 1,
					'2026-10-12T00:00:00Z', '{}', '{}', ${height}, ${park(i) ?? 'NULL'}, '${kind(i)}', ${state(i)}, '${y}-01-01', '${y}-12-31')`);
			}
			statements.push(env.DATA.prepare(
				`INSERT INTO attractions (id, in_latest, in_settled, type, status, country, sort_name, revision, updated_at, summary, record, height,
					park, location_kind, state, opened_earliest, opened_latest) VALUES ${values.join(',')}`,
			));
		}
		await env.DATA.batch(statements);
	});

	const mid = encodeCursor('name', 'attraction 03000', 0);
	it.each([
		// A park has about 30 here (the wiki's large ones a few hundred): its index, then a key read each.
		['a park', '/v1/attractions?limit=100&park=1007', /_by_park/, 100],
		['a park, by height, with a type', '/v1/attractions?limit=100&park=1007&type=ride&sort=-height', /_by_park/, 100],
		['one kind, by name', `/v1/attractions?limit=100&location_kind=travelling&cursor=${mid}`, /_by_location_kind/, 250],
		// The common kind stays out of a stat sort, which walks the stat's index.
		['the common kind, by height', '/v1/attractions?limit=100&location_kind=park&sort=-height', /_by_height/, 250],
		['a state', '/v1/attractions?limit=100&state=State%208', /_by_state/, 600],
		// About 600 opened in the 1950s: the range, then a sort, not the name index's 6,000.
		['a rare decade, by name', '/v1/attractions?limit=100&opened_from=1950&opened_to=1959', /_by_opened/, 1300],
		['opened by 1920', '/v1/attractions?limit=100&opened_to=1920', /_by_opened_latest/, 600],
		['by opening, deep', `/v1/attractions?limit=100&sort=opened&cursor=${encodeCursor('opened', '2000-01-01', 0)}`, /_by_opened/, 250],
	])('%s', async (_, url, index, bound) => {
		const { read, plan } = await measure(url);
		expect(plan).toMatch(index);
		expect(read).toBeLessThan(bound);
	});

	it('writes an index entry per column recorded, per view, and nothing for an edit that moves none', async () => {
		const insert = (id: number, cols: string) => env.DATA.prepare(
			`INSERT INTO attractions (id, in_latest, in_settled, type, status, country, sort_name, revision, updated_at, summary, record${cols ? ', park, location_kind, state, opened_earliest, opened_latest' : ''})
			 VALUES (?1, 1, 1, 'ride', 'operating', 'GB', 'zz', 1, '2026-10-12T00:00:00Z', '{}', '{}'${cols})`,
		).bind(id).run();
		const located = ", 2999, 'park', 'State 8', '1994-01-01', '1994-12-31'";
		// The first of each opens its facet cell; the second finds it.
		await insert(99996, '');
		await insert(99997, located);
		const bare = await insert(99998, '');
		const again = await insert(99999, located);
		// park, state, opened and opened_latest: an entry each in both views the row is in. The kind index
		// holds every row, recorded or not, so it costs a bare row the same.
		expect(again.meta.rows_written - bare.meta.rows_written).toBe(8);
		expect((await env.DATA.prepare('UPDATE attractions SET revision = 2 WHERE id = 99999').run()).meta.rows_written).toBe(1);
	});

	it("counts a park's facets from its own rows", async () => {
		const q = facetQueries(new URL('/v1/attractions/facets?park=1007', BASE));
		const cells = await env.DATA.prepare(q.cells.sql).bind(...q.cells.binds).all<{ n: number }>();
		expect(cells.results.reduce((sum, c) => sum + c.n, 0)).toBe(40);
		expect(cells.meta.rows_read).toBeLessThan(150);
		const height = await env.DATA.prepare(q.ranges.height.sql).bind(...q.ranges.height.binds).all();
		expect(height.meta.rows_read).toBeLessThan(150);
	});
});
