import { env, exports } from 'cloudflare:workers';
import { beforeAll, describe, expect, it } from 'vitest';
import { encodeCursor } from '../src/attractions';
import { sha256Hex } from '../src/auth';
import { manufacturerListQuery } from '../src/manufacturers';
import { resetData } from './reset';
// Written by the Projector's GoldenBatchTests: the real wire shape, not a hand copy.
import batch from './fixtures/projector-manufacturer-batch.json';
import attractions from './fixtures/projector-batch.json';
import parks from './fixtures/projector-park-batch.json';

const BASE = 'https://api.coasterpedia.net';
const KEY = 'cpk_manufacturers';

const get = (path: string) =>
	exports.default.fetch(new Request(BASE + path, { redirect: 'manual', headers: { authorization: `Bearer ${KEY}` } }));

const ingest = (body: unknown) => exports.default.fetch(new Request(`${BASE}/internal/ingest`, {
	method: 'POST',
	headers: { 'content-type': 'application/json', 'x-dev-ingest-token': 'test-ingest-token' },
	body: JSON.stringify(body),
}));

const ids = async (q: string) => (await (await get(`/v1/manufacturers?${q}`)).json<any>()).data.map((m: any) => m.id);

describe('the Projector manufacturer batch', () => {
	beforeAll(async () => {
		await resetData();
		await env.KEYS.put(`key:${await sha256Hex(KEY)}`, JSON.stringify({ account: 'manufacturers', tier: 'beta', status: 'active' }));
		expect(await (await ingest(batch)).json()).toEqual({ upserted: 7, deleted: 1, promoted: 1, discarded: 1 });
		// The other kinds beside them, to show none touches another's rows.
		expect((await ingest(attractions)).status).toBe(200);
		expect((await ingest(parks)).status).toBe(200);
	});

	it('lists manufacturers in name order, case-blind, in each view', async () => {
		const body = await (await get('/v1/manufacturers?view=latest')).json<any>();
		expect(body.data.map((m: any) => m.name)).toEqual([
			'Adesko', 'Arrow Dynamics', 'Bolliger & Mabillard', 'Mack Rides', 'ProSlide Technology', 'The Gravity Group, LLC', 'Vekoma',
		]);
		// Adesko waits to settle.
		expect(await ids('')).toEqual([1829, 543, 2393, 55205, 3551, 548]);
	});

	it('lists the record as the Projector built it, as the contract lists full manufacturers', async () => {
		const body = await (await get('/v1/manufacturers?view=latest&country=CH')).json<any>();
		expect(body.data).toEqual([batch.upsert.find((u) => u.id === 543)!.summary]);
		expect(body.data[0]).toMatchObject({ headquarters: 'Monthey, Valais, Switzerland', website: 'https://bolliger-mabillard.com/' });
	});

	it('filters by status and country', async () => {
		expect(await ids('view=latest&country=us')).toEqual([1829, 3551]);
		expect(await ids('view=latest&status=defunct')).toEqual([1829]);
		expect(await ids('view=latest&status=active&country=DE,CA')).toEqual([2393, 55205]);
		// Vekoma records no country, so no country filter selects it.
		expect(await ids('view=latest&country=BE,CA,CH,DE,US')).not.toContain(548);
		// An operating status is an attraction's or a park's; no manufacturer has one.
		expect(await ids('view=latest&status=operating')).toEqual([]);
	});

	it('pages by name with a cursor, both ways', async () => {
		const first = await (await get('/v1/manufacturers?view=latest&limit=3')).json<any>();
		expect(first.data.map((m: any) => m.id)).toEqual([17469, 1829, 543]);
		const second = await (await get(`/v1/manufacturers?view=latest&limit=3&cursor=${first.meta.next_cursor}`)).json<any>();
		expect(second.data.map((m: any) => m.id)).toEqual([2393, 55205, 3551]);
		const third = await (await get(`/v1/manufacturers?view=latest&limit=3&cursor=${second.meta.next_cursor}`)).json<any>();
		expect(third.data.map((m: any) => m.id)).toEqual([548]);
		expect(third.meta.next_cursor).toBeNull();

		expect(await ids('view=latest&sort=-name&limit=2')).toEqual([548, 3551]);
	});

	it('serves the record as sent, with its settlement and its founding date at its precision', async () => {
		const pending = await (await get('/v1/manufacturers/17469?view=latest')).json<any>();
		expect(pending.data).toEqual({
			...batch.upsert.find((u) => u.id === 17469)!.record,
			settlement: { settled_revision: null, pending_revision: 604, settles_at: '2026-10-19T12:00:00Z' },
		});
		expect(pending.data.founded).toEqual({ edtf: '1991-10', earliest: '1991-10-01', latest: '1991-10-31' });

		const arrow = await (await get('/v1/manufacturers/1829')).json<any>();
		expect(arrow.data).not.toHaveProperty('founded');
		expect(arrow.data.settlement).toEqual({ settled_revision: 602, pending_revision: null, settles_at: null });
	});

	it('answers a manufacturer that has not settled with a pointer to view=latest', async () => {
		const res = await get('/v1/manufacturers/17469');
		expect(res.status).toBe(404);
		expect((await res.json<any>()).type).toMatch(/not-settled$/);
	});

	it('redirects the merged manufacturer, and keeps the kinds apart', async () => {
		const res = await get('/v1/manufacturers/99101?view=latest');
		expect(res.status).toBe(301);
		expect(res.headers.get('location')).toBe(`${BASE}/v1/manufacturers/543?view=latest`);

		// 543 is a manufacturer; 171 an attraction and 182 a park.
		expect((await get('/v1/attractions/543?view=latest')).status).toBe(404);
		expect((await get('/v1/parks/543?view=latest')).status).toBe(404);
		expect((await get('/v1/manufacturers/171?view=latest')).status).toBe(404);
		expect((await get('/v1/manufacturers/182?view=latest')).status).toBe(404);
	});

	it('counts manufacturers in /v1/meta, per view', async () => {
		const latest = await (await exports.default.fetch(new Request(`${BASE}/v1/meta?view=latest`))).json<any>();
		expect(latest.counts).toEqual({ attractions: 7, parks: 7, manufacturers: 7, products: 0 });
		expect(latest.schema_version).toBe('1.0.0-beta.11');
		const settled = await (await exports.default.fetch(new Request(`${BASE}/v1/meta`))).json<any>();
		expect(settled.counts).toEqual({ attractions: 6, parks: 6, manufacturers: 6, products: 0 });
	});

	it('logs manufacturer changes for /v1/changes, by kind and view', async () => {
		const made = await (await get('/v1/changes?view=latest&kinds=manufacturer&since=2026-01-01T00:00:00Z')).json<any>();
		expect(made.data).toHaveLength(8);
		expect(made.data.every((c: any) => c.kind === 'manufacturer')).toBe(true);
		expect(made.data.at(-1)).toMatchObject({ kind: 'manufacturer', id: 99101, op: 'delete', merged_into: 543 });

		const settled = await (await get('/v1/changes?kinds=manufacturer&since=2026-01-01T00:00:00Z')).json<any>();
		expect(settled.data.map((c: any) => c.id)).not.toContain(17469);
	});

	it('lists the manufacturer statuses in /v1/vocabularies', async () => {
		const body = await (await exports.default.fetch(new Request(`${BASE}/v1/vocabularies`))).json<any>();
		const status = Object.fromEntries(body.data.status.map((s: any) => [s.value, s.applies_to]));
		expect(status.active).toEqual(['manufacturer']);
		expect(status.defunct).toContain('manufacturer');
		expect(status.operating).not.toContain('manufacturer');
	});

	it('promotes and discards a manufacturer like the other kinds', async () => {
		await ingest({ as_of: '2026-10-19T12:00:00Z', upsert: [], delete: [], promote: [{ kind: 'manufacturer', id: 17469 }] });
		expect((await get('/v1/manufacturers/17469')).status).toBe(200);
		expect(await ids('')).toContain(17469);

		// An untrusted change to Vekoma, then the revert back to what settled.
		const vekoma = batch.upsert.find((u) => u.id === 548)!;
		await ingest({
			as_of: '2026-10-19T13:00:00Z', delete: [],
			upsert: [{ ...vekoma, revision: 701, settled: false, settles_at: '2026-10-26T13:00:00Z', record: { ...vekoma.record, name: 'Vekoma Rides', revision: 701 } }],
		});
		expect((await (await get('/v1/manufacturers/548?view=latest')).json<any>()).data.name).toBe('Vekoma Rides');
		expect((await (await get('/v1/manufacturers/548')).json<any>()).data.name).toBe('Vekoma');

		await ingest({ as_of: '2026-10-19T14:00:00Z', upsert: [], delete: [], discard: [{ kind: 'manufacturer', id: 548 }] });
		const back = await (await get('/v1/manufacturers/548?view=latest')).json<any>();
		expect(back.data.name).toBe('Vekoma');
		expect(back.data.settlement).toEqual({ settled_revision: 607, pending_revision: null, settles_at: null });
	});
});

describe('what /v1/manufacturers refuses', () => {
	it.each([
		['a contract filter whose field is not published', '/v1/manufacturers?updated_since=2026-10-01T00:00:00Z', 400, 'parameter-unavailable'],
		['the full-text search, not yet built', '/v1/manufacturers?q=arrow', 400, 'parameter-unavailable'],
		['a park filter', '/v1/manufacturers?state=Ohio', 400, 'parameter-unknown'],
		['a sort manufacturers do not have', '/v1/manufacturers?sort=founded', 400, 'parameter-unavailable'],
		['a cursor from a stat sort', `/v1/manufacturers?cursor=${encodeCursor('height', 20, 1)}`, 400, 'cursor-invalid'],
		['a manufacturer ID that is not a number', '/v1/manufacturers/arrow-dynamics', 404, 'not-found'],
		['the products of a manufacturer it does not hold', '/v1/manufacturers/424242/products', 404, 'not-found'],
		['an unpublished filter on the one-record endpoint', '/v1/manufacturers/543?q=arrow', 400, 'parameter-unavailable'],
	])('%s', async (_, path, status, type) => {
		const res = await get(path);
		expect(res.status).toBe(status);
		expect((await res.json<any>()).type).toMatch(new RegExp(`${type}$`));
	});
});

describe('a filtered manufacturer list reads no more than the table', () => {
	// Several times the 261 of 2026-10-12, so the bound still holds as the table grows.
	const N = 1500;
	const name = (i: number) => `maker ${String((i * 7919) % N).padStart(5, '0')}`;
	const COUNTRIES = ['US', 'US', 'DE', 'IT', 'GB', 'CN', 'CH', 'NL', 'JP', 'CA'];

	beforeAll(async () => {
		await resetData();
		const statements = [];
		for (let start = 1; start <= N; start += 500) {
			const values = [];
			for (let i = start; i < start + 500; i++) {
				// About three in five active, as on the wiki.
				const status = i % 5 < 3 ? 'active' : 'defunct';
				values.push(`(${i}, 1, 1, '${status}', '${COUNTRIES[i % COUNTRIES.length]}', '${name(i)}', 1, '2026-10-12T00:00:00Z', '{}', '{}')`);
			}
			statements.push(env.DATA.prepare(
				`INSERT INTO manufacturers (id, in_latest, in_settled, status, country, sort_name, revision, updated_at, summary, record) VALUES ${values.join(',')}`,
			));
		}
		await env.DATA.batch(statements);
	});

	const mid = encodeCursor('name', 'maker 00750', 0);
	it.each([
		['by name', `/v1/manufacturers?view=latest&limit=100&cursor=${mid}`, 300],
		['by name, descending, settled', `/v1/manufacturers?limit=100&sort=-name&cursor=${mid}`, 300],
		// No filter index (0007): each walks the name index past the rows it skips
		// (one in five is US, two in five defunct), bounded by the table.
		['filtered by status', `/v1/manufacturers?view=latest&limit=100&status=defunct&cursor=${mid}`, 800],
		['filtered by country', `/v1/manufacturers?view=latest&limit=100&country=US&cursor=${mid}`, 800],
	])('%s', async (_, url, bound) => {
		const { sql, binds } = manufacturerListQuery(new URL(url, BASE));
		const result = await env.DATA.prepare(sql).bind(...binds).all();
		expect(result.results.length).toBe(101);
		expect(result.meta.rows_read).toBeLessThan(bound);
	});
});
