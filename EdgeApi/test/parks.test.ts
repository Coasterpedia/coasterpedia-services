import { env, exports } from 'cloudflare:workers';
import { beforeAll, describe, expect, it } from 'vitest';
import { encodeCursor } from '../src/attractions';
import { sha256Hex } from '../src/auth';
import { parkListQuery } from '../src/parks';
import { resetData } from './reset';
// Written by the Projector's GoldenBatchTests: the real wire shape, not a hand copy.
import batch from './fixtures/projector-park-batch.json';
import attractions from './fixtures/projector-batch.json';

const BASE = 'https://api.coasterpedia.net';
const KEY = 'cpk_parks';

const get = (path: string) =>
	exports.default.fetch(new Request(BASE + path, { redirect: 'manual', headers: { authorization: `Bearer ${KEY}` } }));

const ingest = (body: unknown) => exports.default.fetch(new Request(`${BASE}/internal/ingest`, {
	method: 'POST',
	headers: { 'content-type': 'application/json', 'x-dev-ingest-token': 'test-ingest-token' },
	body: JSON.stringify(body),
}));

const ids = async (q: string) => (await (await get(`/v1/parks?${q}`)).json<any>()).data.map((p: any) => p.id);

describe('the Projector park batch', () => {
	beforeAll(async () => {
		await resetData();
		await env.KEYS.put(`key:${await sha256Hex(KEY)}`, JSON.stringify({ account: 'parks', tier: 'beta', status: 'active' }));
		expect(await (await ingest(batch)).json()).toEqual({ upserted: 7, deleted: 1, promoted: 1, discarded: 1 });
		// Attractions beside them, to show the kinds never touch each other's rows.
		expect((await ingest(attractions)).status).toBe(200);
	});

	it('lists parks in name order, accent- and case-blind, in each view', async () => {
		const body = await (await get('/v1/parks?view=latest')).json<any>();
		expect(body.data.map((p: any) => p.name)).toEqual([
			'Acushnet Park', "Anna's Hoeve", 'Bibi & Tina Freizeitpark', 'Cacau Park', 'Carnival Horizon', 'Cedar Point', 'Dollywood',
		]);
		// Acushnet Park waits to settle.
		expect(await ids('')).toEqual([26438, 76098, 66765, 67009, 182, 2156]);
	});

	it('serves the summary the Projector built', async () => {
		const body = await (await get('/v1/parks?view=latest&state=Ohio')).json<any>();
		expect(body.data).toEqual([batch.upsert.find((u) => u.id === 182)!.summary]);
	});

	it('filters by status, country and state', async () => {
		expect(await ids('view=latest&country=us')).toEqual([65834, 182, 2156]);
		expect(await ids('view=latest&state=Ohio&state=Tennessee')).toEqual([182, 2156]);
		// One name per parameter: the infobox allows "Washington, D.C.".
		expect(await ids('view=latest&state=Ohio,Tennessee')).toEqual([]);
		expect(await ids('view=latest&status=under_construction')).toEqual([76098, 66765]);
		expect(await ids('view=latest&status=defunct&country=US')).toEqual([65834]);
		// The resort's ID is its wrapper template's page ID (Template:Cedar Point resort).
		expect(await ids('view=latest&resort=58420')).toEqual([182]);
		expect(await ids('view=latest&resort=58420,57454')).toEqual([182, 2156]);
		// A cruise ship has no country, so no country filter selects it.
		expect(await ids('view=latest&country=BR,DE,NL,US')).not.toContain(67009);
	});

	it('pages by name with a cursor, both ways', async () => {
		const first = await (await get('/v1/parks?view=latest&limit=3')).json<any>();
		expect(first.data.map((p: any) => p.id)).toEqual([65834, 26438, 76098]);
		const second = await (await get(`/v1/parks?view=latest&limit=3&cursor=${first.meta.next_cursor}`)).json<any>();
		expect(second.data.map((p: any) => p.id)).toEqual([66765, 67009, 182]);
		const third = await (await get(`/v1/parks?view=latest&limit=3&cursor=${second.meta.next_cursor}`)).json<any>();
		expect(third.data.map((p: any) => p.id)).toEqual([2156]);
		expect(third.meta.next_cursor).toBeNull();

		expect(await ids('view=latest&sort=-name&limit=2')).toEqual([2156, 182]);
	});

	it('serves the record as sent, with its settlement and open-ended dates intact', async () => {
		const pending = await (await get('/v1/parks/65834?view=latest')).json<any>();
		expect(pending.data).toEqual({
			...batch.upsert.find((u) => u.id === 65834)!.record,
			settlement: { settled_revision: null, pending_revision: 504, settles_at: '2026-10-19T12:00:00Z' },
		});

		const anna = await (await get('/v1/parks/26438')).json<any>();
		expect(anna.data.dates).toEqual({
			opened: { edtf: '[..1961]', earliest: null, latest: '1961-12-31' },
			closed: { edtf: '[1979..]', earliest: '1979-01-01', latest: null },
		});
		expect(anna.data.settlement).toEqual({ settled_revision: 503, pending_revision: null, settles_at: null });
	});

	it('answers a park that has not settled with a pointer to view=latest', async () => {
		const res = await get('/v1/parks/65834');
		expect(res.status).toBe(404);
		expect((await res.json<any>()).type).toMatch(/not-settled$/);
	});

	it('redirects the merged park, and keeps the kinds apart', async () => {
		const res = await get('/v1/parks/99001?view=latest');
		expect(res.status).toBe(301);
		expect(res.headers.get('location')).toBe(`${BASE}/v1/parks/182?view=latest`);

		// 182 is a park, not an attraction; 170 is a merged attraction, not a park.
		expect((await get('/v1/attractions/182?view=latest')).status).toBe(404);
		expect((await get('/v1/parks/170?view=latest')).status).toBe(404);
		expect((await get('/v1/attractions/170?view=latest')).status).toBe(301);
	});

	it('counts parks in /v1/meta, per view', async () => {
		const latest = await (await exports.default.fetch(new Request(`${BASE}/v1/meta?view=latest`))).json<any>();
		expect(latest.counts).toEqual({ attractions: 7, parks: 7, manufacturers: 0, products: 0 });
		expect(latest.schema_version).toBe('1.0.0-beta.11');
		const settled = await (await exports.default.fetch(new Request(`${BASE}/v1/meta`))).json<any>();
		expect(settled.counts).toEqual({ attractions: 6, parks: 6, manufacturers: 0, products: 0 });
	});

	it('logs park changes for /v1/changes, by kind and view', async () => {
		const parks = await (await get('/v1/changes?view=latest&kinds=park&since=2026-01-01T00:00:00Z')).json<any>();
		expect(parks.data).toHaveLength(8);
		expect(parks.data.every((c: any) => c.kind === 'park')).toBe(true);
		expect(parks.data.at(-1)).toMatchObject({ kind: 'park', id: 99001, op: 'delete', merged_into: 182 });

		const settled = await (await get('/v1/changes?kinds=park&since=2026-01-01T00:00:00Z')).json<any>();
		expect(settled.data.map((c: any) => c.id)).not.toContain(65834);

		const both = await (await get('/v1/changes?view=latest&kinds=attraction,park&since=2026-01-01T00:00:00Z')).json<any>();
		expect(both.data).toHaveLength(16);
	});

	it('promotes and discards a park like an attraction', async () => {
		await ingest({ as_of: '2026-10-19T12:00:00Z', upsert: [], delete: [], promote: [{ kind: 'park', id: 65834 }] });
		expect((await get('/v1/parks/65834')).status).toBe(200);
		expect(await ids('')).toContain(65834);

		// An untrusted change to Cedar Point, then the revert back to what settled.
		const cedar = batch.upsert.find((u) => u.id === 182)!;
		await ingest({
			as_of: '2026-10-19T13:00:00Z', delete: [],
			upsert: [{ ...cedar, revision: 601, settled: false, settles_at: '2026-10-26T13:00:00Z', record: { ...cedar.record, name: 'Cedar Pointe', revision: 601 } }],
		});
		expect((await (await get('/v1/parks/182?view=latest')).json<any>()).data.name).toBe('Cedar Pointe');
		expect((await (await get('/v1/parks/182')).json<any>()).data.name).toBe('Cedar Point');

		await ingest({ as_of: '2026-10-19T14:00:00Z', upsert: [], delete: [], discard: [{ kind: 'park', id: 182 }] });
		const back = await (await get('/v1/parks/182?view=latest')).json<any>();
		expect(back.data.name).toBe('Cedar Point');
		expect(back.data.settlement).toEqual({ settled_revision: 501, pending_revision: null, settles_at: null });
	});
});

describe('what /v1/parks refuses', () => {
	it.each([
		['a contract filter whose field is not published', '/v1/parks?bbox=0,0,1,1', 400, 'parameter-unavailable'],
		['a resort that is not an ID', '/v1/parks?resort=Cedar%20Point%20Resort', 400, 'parameter-invalid'],
		['the full-text search, not yet built', '/v1/parks?q=cedar', 400, 'parameter-unavailable'],
		['an attraction filter', '/v1/parks?type=coaster', 400, 'parameter-unknown'],
		['a sort parks do not have', '/v1/parks?sort=height', 400, 'parameter-unavailable'],
		['a cursor from a stat sort', `/v1/parks?cursor=${encodeCursor('height', 20, 1)}`, 400, 'cursor-invalid'],
		['a park ID that is not a number', '/v1/parks/cedar-point', 404, 'not-found'],
		['attractions at a park, until attractions carry their park', '/v1/parks/182/attractions', 404, 'not-available'],
	])('%s', async (_, path, status, type) => {
		const res = await get(path);
		expect(res.status).toBe(status);
		expect((await res.json<any>()).type).toMatch(new RegExp(`${type}$`));
	});

	it('refuses an ingest item of a kind it does not store', async () => {
		const res = await ingest({ as_of: '2026-10-12T12:00:00Z', upsert: [], delete: [{ kind: 'park_company', id: 1, merged_into: null }] });
		expect(res.status).toBe(400);
	});
});

describe('a page deep in the park list reads its page, not the table before it', () => {
	const N = 2500;
	const name = (i: number) => `park ${String((i * 7919) % N).padStart(5, '0')}`;
	const STATUSES = ['operating', 'operating', 'operating', 'defunct', 'planned'];

	beforeAll(async () => {
		await resetData();
		const statements = [];
		for (let start = 1; start <= N; start += 500) {
			const values = [];
			for (let i = start; i < start + 500; i++) {
				const country = i % 3 === 0 ? 'US' : 'GB';
				const state = country === 'US' ? `'Ohio'` : 'NULL';
				// 88 of 2,166 parks are in a resort, one to six each.
				const resort = i % 25 === 0 ? 10000 + (i % 1000) : 'NULL';
				values.push(`(${i}, 1, 1, '${STATUSES[i % STATUSES.length]}', '${country}', ${state}, ${resort}, '${name(i)}', 1, '2026-10-12T00:00:00Z', '{}', '{}')`);
			}
			statements.push(env.DATA.prepare(
				`INSERT INTO parks (id, in_latest, in_settled, status, country, state, resort, sort_name, revision, updated_at, summary, record) VALUES ${values.join(',')}`,
			));
		}
		await env.DATA.batch(statements);
	});

	const mid = encodeCursor('name', 'park 01250', 0);
	it.each([
		['by name', `/v1/parks?view=latest&limit=100&cursor=${mid}`, 300],
		['by name, descending', `/v1/parks?limit=100&sort=-name&cursor=${mid}`, 300],
		['filtered by country', `/v1/parks?view=latest&limit=100&country=US&cursor=${mid}`, 300],
		// No status or state index (0006): the filter walks the name index, bounded by the table.
		['filtered by status', `/v1/parks?view=latest&limit=100&status=defunct&cursor=${mid}`, 1300],
		['filtered by state', `/v1/parks?view=latest&limit=100&state=Ohio&cursor=${mid}`, 1300],
	])('%s', async (_, url, bound) => {
		const { sql, binds } = parkListQuery(new URL(url, BASE));
		const result = await env.DATA.prepare(sql).bind(...binds).all();
		expect(result.results.length).toBe(101);
		expect(result.meta.rows_read).toBeLessThan(bound);
	});

	it('reads only a resort\'s parks, not the table', async () => {
		// Resort 10000 holds ids 1000 and 2000; 10500 holds 500, 1500 and 2500.
		const { sql, binds } = parkListQuery(new URL('/v1/parks?view=latest&resort=10000,10500', BASE));
		const result = await env.DATA.prepare(sql).bind(...binds).all<{ id: number }>();
		expect(result.results.map((r) => r.id).sort((a, b) => a - b)).toEqual([500, 1000, 1500, 2000, 2500]);
		expect(result.meta.rows_read).toBeLessThan(20);
	});
});
