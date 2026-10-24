import { env, exports } from 'cloudflare:workers';
import { beforeAll, describe, expect, it } from 'vitest';
import { listQuery } from '../src/attractions';
import { sha256Hex } from '../src/auth';
import { boxCells } from '../src/filters';
import { searchQuery } from '../src/search';
import { resetData } from './reset';
// Written by the Projector's GoldenBatchTests: the real wire shape, not a hand copy.
import attractions from './fixtures/projector-batch.json';
import manufacturers from './fixtures/projector-manufacturer-batch.json';
import parkCompanies from './fixtures/projector-park-company-batch.json';
import parks from './fixtures/projector-park-batch.json';
import products from './fixtures/projector-product-batch.json';

/** DR-010 §12.19: q and /v1/search, updated_since and sort=updated, bbox, and a park's former attractions. */

const BASE = 'https://api.coasterpedia.net';
const KEY = 'cpk_search';

const get = (path: string) =>
	exports.default.fetch(new Request(BASE + path, { redirect: 'manual', headers: { authorization: `Bearer ${KEY}` } }));

const ingest = (body: unknown) => exports.default.fetch(new Request(`${BASE}/internal/ingest`, {
	method: 'POST',
	headers: { 'content-type': 'application/json', 'x-dev-ingest-token': 'test-ingest-token' },
	body: JSON.stringify(body),
}));

const ids = async (path: string) => (await (await get(path)).json<any>()).data.map((r: any) => r.id);
const found = async (q: string) => (await (await get(`/v1/search?view=latest&q=${encodeURIComponent(q)}`)).json<any>()).data;

beforeAll(async () => {
	await resetData();
	await env.KEYS.put(`key:${await sha256Hex(KEY)}`, JSON.stringify({ account: 'search', tier: 'beta', status: 'active' }));
	for (const batch of [attractions, parks, manufacturers, products, parkCompanies]) {
		expect((await ingest(batch)).status).toBe(200);
	}
});

describe('GET /v1/search', () => {
	it('finds every kind by the start of a word, case- and accent-blind', async () => {
		expect((await found('bat')).map((r: any) => [r.kind, r.id])).toEqual([['attraction', 165]]);
		expect((await found('elan')).map((r: any) => r.name)).toEqual(['Élan']);
		expect((await found('cedar')).map((r: any) => [r.kind, r.name])).toEqual(
			expect.arrayContaining([['park', 'Cedar Point'], ['park_company', 'Cedar Fair']]));
		expect((await found('ARROW')).map((r: any) => r.kind)).toEqual(['manufacturer']);
	});

	it('needs every word, and finds a record by a past name, once', async () => {
		const [twin] = await found('twin racer');
		expect(twin).toEqual({
			kind: 'attraction', id: 9101, name: 'Example Twin', matched: 'Twin Racer',
			url: 'https://coasterpedia.net/wiki/Example_Twin_(Example_Park)',
		});
		// Both its names start with "twin" or contain the word: one result, by its own name.
		const all = await found('twin');
		expect(all.filter((r: any) => r.id === 9101)).toEqual([expect.objectContaining({ matched: 'Example Twin' })]);
		expect(await found('batman racer')).toEqual([]);
	});

	it('finds a park by a past name', async () => {
		expect(await found('silver dollar')).toEqual([{
			kind: 'park', id: 2156, name: 'Dollywood', matched: 'Silver Dollar City',
			url: 'https://coasterpedia.net/wiki/Dollywood',
		}]);
		expect(await ids('/v1/parks?view=latest&q=goldrush')).toEqual([2156]);
	});

	it('answers in the view asked for, and by kind', async () => {
		// Example Twin waits to settle, so the settled view doesn't know it.
		expect((await (await get('/v1/search?q=twin+racer')).json<any>()).data).toEqual([]);
		expect((await (await get('/v1/search?view=latest&q=cedar&types=park')).json<any>()).data.map((r: any) => r.kind))
			.toEqual(['park']);
	});

	it('refuses what it cannot answer', async () => {
		expect((await get('/v1/search')).status).toBe(400);
		expect((await get('/v1/search?q=a')).status).toBe(400);
		expect((await get('/v1/search?q=bat&types=person')).status).toBe(400);
		expect((await get('/v1/search?q=bat&sort=name')).status).toBe(400);
	});

	it('finds initials written with full stops, as the letters in a row', async () => {
		const name = 'Fami P.A.R.C Express';
		const summary = { id: 95001, type: 'coaster', name, name_is_placeholder: false, manufacturers: [], products: [], updated_at: '2026-10-11T12:00:00Z' };
		expect((await ingest({
			as_of: '2026-10-24T12:00:00Z', delete: [],
			upsert: [{
				kind: 'attraction', id: 95001, revision: 1, updated_at: '2026-10-11T12:00:00Z', settled: true, settles_at: null,
				sort_name: name.toLowerCase(), filters: { type: 'coaster', status: null, country: null }, summary,
				record: { ...summary, title: name, url: 'https://coasterpedia.net/wiki/Fami', location: {}, revision: 1 },
			}],
		})).status).toBe(200);
		const has = async (q: string) => (await found(q)).some((r: any) => r.id === 95001);
		expect(await has('P.A.R.C')).toBe(true);
		expect(await has('fami p.a.r.c.')).toBe(true);
		expect(await has('P.A.R')).toBe(true); // the last letter is a prefix, as a word's is
		expect(await has('P.R.A')).toBe(false); // in a row, in order
		expect(await has('fami p.a.r.c batman')).toBe(false); // every word still counts
		await ingest({ as_of: '2026-10-24T12:00:00Z', upsert: [], delete: [{ kind: 'attraction', id: 95001, merged_into: null }] });
	});

	it('reads the matches, not the names', async () => {
		const { sql, binds } = searchQuery(new URL(`${BASE}/v1/search?q=bat`));
		const plan = (await env.DATA.prepare(`EXPLAIN QUERY PLAN ${sql}`).bind(...binds).all<{ detail: string }>()).results
			.map((r) => r.detail).join('\n');
		expect(plan).toMatch(/SCAN name_fts VIRTUAL TABLE INDEX/);
		expect(plan).not.toMatch(/SCAN n\b/);
	});
});

describe('q on the lists', () => {
	it('filters every list by name, past names included for attractions and parks', async () => {
		expect(await ids('/v1/attractions?view=latest&q=colossus')).toEqual([169]);
		expect(await ids('/v1/attractions?view=latest&q=racer')).toEqual([9101]);
		// "the" is a word in Batman The Ride and Over the Hill 2; by height, both record one.
		expect(await ids('/v1/attractions?view=latest&q=the&sort=height')).toEqual([165, 19643]);
		expect(await ids('/v1/parks?q=cedar')).toEqual([182]);
		expect(await ids('/v1/manufacturers?q=bolliger')).toEqual([543]);
		expect(await ids('/v1/products?view=latest&q=boom')).toEqual([3213]);
		expect(await ids('/v1/park-companies?q=cedar')).toEqual([823]);
	});

	it('narrows the facets the same way', async () => {
		const body = await (await get('/v1/attractions/facets?view=latest&q=colossus')).json<any>();
		expect(body.data.total).toBe(1);
		expect(body.data.dimensions.type).toEqual([{ value: 'coaster', label: 'Roller coaster', count: 1 }]);
	});

	it('drives from the matches, not the name index', async () => {
		const { sql, binds } = listQuery(new URL(`${BASE}/v1/attractions?q=colossus`));
		const plan = (await env.DATA.prepare(`EXPLAIN QUERY PLAN ${sql}`).bind(...binds).all<{ detail: string }>()).results
			.map((r) => r.detail).join('\n');
		expect(plan).toMatch(/SEARCH attractions USING (PRIMARY KEY|INDEX sqlite_autoindex_attractions_1) \(id=\? AND in_latest=\?\)/);
		expect(plan).not.toMatch(/SCAN attractions/);
	});
});

describe('updated_since and sort=updated', () => {
	it('lists what changed since a time, and sorts by when', async () => {
		expect(await ids('/v1/attractions?view=latest&updated_since=2026-10-11T12:00:01Z')).toEqual([]);
		expect((await ids('/v1/attractions?view=latest&updated_since=2026-10-11T13:00:00%2B01:00')).length).toBe(7);

		// Gold Rusher changes later.
		const later = structuredClone(attractions.upsert.find((u) => u.id === 171)!) as any;
		later.updated_at = '2026-10-12T09:00:00Z';
		later.revision = 999;
		expect((await ingest({ as_of: '2026-10-12T09:00:00Z', upsert: [later], delete: [] })).status).toBe(200);
		expect(await ids('/v1/attractions?updated_since=2026-10-12T00:00:00Z')).toEqual([171]);
		expect((await ids('/v1/attractions?sort=-updated'))[0]).toBe(171);
		// The park batch is stamped 2026-10-12T12:00:00Z.
		expect(await ids('/v1/parks?updated_since=2026-10-12T12:00:01Z')).toEqual([]);
		expect((await ids('/v1/parks?updated_since=2026-10-12T12:00:00Z')).length).toBe(6);
	});

	it('pages sort=updated with its own cursor', async () => {
		const first = await (await get('/v1/attractions?sort=updated&limit=4')).json<any>();
		const next = await (await get(`/v1/attractions?sort=updated&limit=4&cursor=${first.meta.next_cursor}`)).json<any>();
		const all = [...first.data, ...next.data].map((r: any) => r.id);
		expect(new Set(all).size).toBe(6);
		expect(all[all.length - 1]).toBe(171);
	});

	it('reads the changes, not the table', async () => {
		const { sql, binds } = listQuery(new URL(`${BASE}/v1/attractions?updated_since=2026-10-12T00:00:00Z`));
		const plan = (await env.DATA.prepare(`EXPLAIN QUERY PLAN ${sql}`).bind(...binds).all<{ detail: string }>()).results
			.map((r) => r.detail).join('\n');
		expect(plan).toMatch(/USING INDEX attractions_settled_by_updated \(updated_at>\?\)/);
	});
});

describe('bbox', () => {
	it('lists what is inside the box, on either side of the meridian', async () => {
		expect(await ids('/v1/attractions?view=latest&bbox=34,-119,35,-118')).toEqual([165, 169, 171]);
		expect(await ids('/v1/attractions?view=latest&bbox=34.426,-119,35,-118')).toEqual([169]);
		expect(await ids('/v1/attractions?view=latest&bbox=51,-1,52,1')).toEqual([19643]);
		expect(await ids('/v1/attractions?view=latest&bbox=34,170,35,-170')).toEqual([]);
		expect(await ids('/v1/parks?bbox=41,-83,42,-82')).toEqual([182]);
	});

	it('refuses a box it would have to read a continent for', async () => {
		expect((await get('/v1/attractions?bbox=30,-120,55,-100')).status).toBe(400);
		expect((await get('/v1/attractions?bbox=35,-118,34,-119')).status).toBe(400);
	});

	it('names the grid squares the box touches, across the antimeridian', () => {
		expect(boxCells({ south: 34.5, west: -118.6, north: 34.6, east: -118.5 })).toEqual([124 * 360 + 61]);
		expect(boxCells({ south: 0, west: 179.5, north: 0.5, east: -179.5 })).toEqual([90 * 360 + 359, 90 * 360 + 0]);
	});

	it('reads its squares from their index', async () => {
		const { sql, binds } = listQuery(new URL(`${BASE}/v1/attractions?bbox=34,-119,35,-118`));
		const plan = (await env.DATA.prepare(`EXPLAIN QUERY PLAN ${sql}`).bind(...binds).all<{ detail: string }>()).results
			.map((r) => r.detail).join('\n');
		expect(plan).toMatch(/USING INDEX attractions_settled_by_cell \(cell=\?\)/);
	});
});

describe('/v1/parks/{id}/attractions?include', () => {
	it('reaches back through past stints', async () => {
		// Example Twin played Cedar Point before it went travelling; nothing stands there now.
		expect(await ids('/v1/parks/182/attractions?view=latest')).toEqual([]);
		expect(await ids('/v1/parks/182/attractions?view=latest&include=former')).toEqual([9101]);
		expect(await ids('/v1/parks/182/attractions?view=latest&include=all')).toEqual([9101]);
		// SkyRide is still at Carnival Horizon: current, never former.
		expect(await ids('/v1/parks/67009/attractions?view=latest&include=former')).toEqual([]);
		expect(await ids('/v1/parks/67009/attractions?view=latest&include=all')).toEqual([66486]);
	});

	it('reads the park\'s own stints', async () => {
		const url = new URL(`${BASE}/v1/attractions?park=182`);
		const { sql, binds } = listQuery(url, 'all');
		const plan = (await env.DATA.prepare(`EXPLAIN QUERY PLAN ${sql}`).bind(...binds).all<{ detail: string }>()).results
			.map((r) => r.detail).join('\n');
		expect(plan).toMatch(/SEARCH pp USING PRIMARY KEY \(park=\?\)/);
		expect(plan).toMatch(/SEARCH x USING INDEX attractions_settled_by_park \(park=\?\)/);
		expect(plan).toMatch(/SEARCH attractions USING (PRIMARY KEY|INDEX sqlite_autoindex_attractions_1) \(id=\? AND in_latest=\?\)/);
		expect(plan).not.toMatch(/SCAN attractions/);
	});
});
