import { env, exports } from 'cloudflare:workers';
import { beforeAll, describe, expect, it } from 'vitest';
import { encodeCursor } from '../src/attractions';
import { sha256Hex } from '../src/auth';
import { productListQuery } from '../src/products';
import { resetData } from './reset';
// Written by the Projector's GoldenBatchTests: the real wire shape, not a hand copy.
import batch from './fixtures/projector-product-batch.json';
import attractions from './fixtures/projector-batch.json';
import manufacturers from './fixtures/projector-manufacturer-batch.json';

const BASE = 'https://api.coasterpedia.net';
const KEY = 'cpk_products';
// The refusals' own account, so the two blocks stay under the per-account limit (60 a minute).
const REFUSED_KEY = 'cpk_productsrefused';

const get = (path: string, key = KEY) =>
	exports.default.fetch(new Request(BASE + path, { redirect: 'manual', headers: { authorization: `Bearer ${key}` } }));

const ingest = (body: unknown) => exports.default.fetch(new Request(`${BASE}/internal/ingest`, {
	method: 'POST',
	headers: { 'content-type': 'application/json', 'x-dev-ingest-token': 'test-ingest-token' },
	body: JSON.stringify(body),
}));

const ids = async (path: string) => (await (await get(path)).json<any>()).data.map((p: any) => p.id);

const upsert = (id: number) => batch.upsert.find((u) => u.id === id)!;

describe('the Projector product batch', () => {
	beforeAll(async () => {
		await resetData();
		await env.KEYS.put(`key:${await sha256Hex(KEY)}`, JSON.stringify({ account: 'products', tier: 'beta', status: 'active' }));
		expect(await (await ingest(batch)).json()).toEqual({ upserted: 8, deleted: 1, promoted: 1, discarded: 1, merged: 0 });
		// Attractions carry the vocabularies and link two of these products: Example Twin (pending) is a
		// Boomerang, Élan is partly an AquaPlay. Manufacturers make /manufacturers/{id}/products answer.
		// Vekoma (548) is one; Zamperla is not.
		expect((await ingest(attractions)).status).toBe(200);
		expect((await ingest(manufacturers)).status).toBe(200);
		// Another manufacturer page merged into Vekoma.
		expect((await ingest({ as_of: '2026-10-12T12:00:00Z', upsert: [], delete: [{ kind: 'manufacturer', id: 99102, merged_into: 548 }] })).status).toBe(200);
	});

	it('lists products in name order, case-blind, in each view', async () => {
		const body = await (await get('/v1/products?view=latest')).json<any>();
		expect(body.data.map((p: any) => p.name)).toEqual([
			'AquaPlay', 'Boomerang', "Disk'O", 'Family Twister', 'Mini Mouse', 'Sit Down Thrill Coaster', 'Swiss Bob', 'Vertigo',
		]);
		// Mini Mouse waits to settle.
		expect(await ids('/v1/products')).toEqual([82994, 3213, 18364, 84000, 68541, 54013, 52106]);
	});

	it('lists the summary the Projector built, with the installations the edge counted', async () => {
		const body = await (await get('/v1/products?view=latest&type=coaster&limit=1')).json<any>();
		expect(body.data).toEqual([{ ...upsert(3213).summary, installations: 1 }]);
		expect(body.data[0]).not.toHaveProperty('variants');
		// Example Twin waits to settle, so the settled view counts no Boomerang yet.
		const settled = await (await get('/v1/products?type=coaster&limit=1')).json<any>();
		expect(settled.data[0].installations).toBe(0);
		// Under a manufacturer too, and a product nothing links is 0, never absent.
		const made = await (await get('/v1/manufacturers/548/products?view=latest')).json<any>();
		expect(made.data.map((p: any) => [p.id, p.installations])).toEqual([[3213, 1], [68541, 0]]);
	});

	it('filters by type and status', async () => {
		expect(await ids('/v1/products?view=latest&type=water_slide')).toEqual([82994, 84000]);
		expect(await ids('/v1/products?view=latest&status=discontinued')).toEqual([3213, 86315, 54013]);
		expect(await ids('/v1/products?status=discontinued')).toEqual([3213, 54013]);
		expect(await ids('/v1/products?view=latest&type=ride,water_slide&status=in_production')).toEqual([82994, 18364, 84000, 52106]);
		// A status a product never has.
		expect(await ids('/v1/products?view=latest&status=operating')).toEqual([]);
	});

	it('filters by any manufacturer a product names, in each view', async () => {
		expect(await ids('/v1/products?manufacturer=548')).toEqual([3213, 68541]);
		// Swiss Bob is a co-build: listed under both.
		expect(await ids('/v1/products?manufacturer=2416')).toEqual([54013]);
		expect(await ids('/v1/products?manufacturer=546')).toEqual([54013]);
		expect(await ids('/v1/products?view=latest&manufacturer=2399')).toEqual([18364, 86315]);
		expect(await ids('/v1/products?manufacturer=2399')).toEqual([18364]);
		expect(await ids('/v1/products?view=latest&manufacturer=2399&type=coaster')).toEqual([86315]);
		expect(await ids('/v1/products?view=latest&manufacturer=2399&sort=-name')).toEqual([86315, 18364]);
	});

	it('follows a merged manufacturer in the filter', async () => {
		expect(await ids('/v1/products?manufacturer=99102')).toEqual([3213, 68541]);
	});

	it('pages by name with a cursor, both ways, with and without a manufacturer', async () => {
		const first = await (await get('/v1/products?view=latest&limit=3')).json<any>();
		expect(first.data.map((p: any) => p.id)).toEqual([82994, 3213, 18364]);
		const second = await (await get(`/v1/products?view=latest&limit=3&cursor=${first.meta.next_cursor}`)).json<any>();
		expect(second.data.map((p: any) => p.id)).toEqual([84000, 86315, 68541]);

		const made = await (await get('/v1/products?manufacturer=548&limit=1')).json<any>();
		expect(made.data.map((p: any) => p.id)).toEqual([3213]);
		const rest = await (await get(`/v1/products?manufacturer=548&limit=1&cursor=${made.meta.next_cursor}`)).json<any>();
		expect(rest.data.map((p: any) => p.id)).toEqual([68541]);
		expect(rest.meta.next_cursor).toBeNull();
	});

	it("lists a manufacturer's products under the manufacturer", async () => {
		expect(await ids('/v1/manufacturers/548/products')).toEqual([3213, 68541]);
		expect(await ids('/v1/manufacturers/548/products?type=coaster&sort=-name')).toEqual([68541, 3213]);
		// B&M is held, and makes none of these.
		const none = await get('/v1/manufacturers/543/products');
		expect(none.status).toBe(200);
		expect((await none.json<any>()).data).toEqual([]);
	});

	it('redirects a merged manufacturer, and refuses one it does not hold', async () => {
		const res = await get('/v1/manufacturers/99102/products?type=coaster');
		expect(res.status).toBe(301);
		expect(res.headers.get('location')).toBe(`${BASE}/v1/manufacturers/548/products?type=coaster`);
		// Zamperla makes two of these, but is not a manufacturer the API holds yet.
		expect((await get('/v1/manufacturers/2399/products')).status).toBe(404);
	});

	it('serves the record as sent, with its settlement and its variants', async () => {
		const boomerang = await (await get('/v1/products/3213')).json<any>();
		expect(boomerang.data).toEqual({
			...upsert(3213).record,
			installations: 0,
			settlement: { settled_revision: 701, pending_revision: null, settles_at: null },
		});
		expect((await (await get('/v1/products/3213?view=latest')).json<any>()).data.installations).toBe(1);
		// The infobox's own specs: one variant, unnamed.
		expect(boomerang.data.variants).toHaveLength(1);
		expect(boomerang.data.variants[0].name).toBeNull();
		expect(boomerang.data.offered).toEqual({ track_material: ['steel'], support_material: ['steel'], types: ['Shuttle'], lift_launch: ['Chain lift'] });

		const disko = await (await get('/v1/products/18364')).json<any>();
		expect(disko.data.variants.map((v: any) => [v.name, v.group])).toEqual([["Disk'O 16", 'Simple'], ["Disk'O Coaster (40)", 'Coaster']]);
		expect(disko.data.variants[1].stats.footprint.length.value).toBe(81.5);

		const vertigo = await (await get('/v1/products/52106')).json<any>();
		expect(vertigo.data.variants[0].stats).toMatchObject({ diameter: { value: 23.2, unit: 'm', entered: { value: 76, unit: 'ft' } }, sweep: 12 });

		// No specs anywhere: no variants at all.
		expect((await (await get('/v1/products/54013')).json<any>()).data).not.toHaveProperty('variants');
	});

	it('answers a product that has not settled with a pointer to view=latest', async () => {
		const res = await get('/v1/products/86315');
		expect(res.status).toBe(404);
		expect((await res.json<any>()).type).toMatch(/not-settled$/);
		expect((await get('/v1/products/86315?view=latest')).status).toBe(200);
	});

	it('redirects the merged product, and keeps the kinds apart', async () => {
		const res = await get('/v1/products/99201?view=latest');
		expect(res.status).toBe(301);
		expect(res.headers.get('location')).toBe(`${BASE}/v1/products/3213?view=latest`);

		// 3213 is a product; 171 an attraction and 548 a manufacturer.
		expect((await get('/v1/attractions/3213?view=latest')).status).toBe(404);
		expect((await get('/v1/manufacturers/3213?view=latest')).status).toBe(404);
		expect((await get('/v1/products/171?view=latest')).status).toBe(404);
		expect((await get('/v1/products/548?view=latest')).status).toBe(404);
	});

	it("lists a product's installations, in each view, as the attractions list does", async () => {
		const aquaplay = await (await get('/v1/products/82994/installations')).json<any>();
		expect(aquaplay.data).toEqual([attractions.upsert.find((u) => u.id === 9102)!.summary]);
		expect(aquaplay.data[0].products.map((p: any) => p.product.id)).toEqual([82994, 58307]);
		// Example Twin is a Boomerang waiting to settle; built, but nothing settled says so yet.
		expect(await ids('/v1/products/3213/installations?view=latest')).toEqual([9101]);
		const none = await get('/v1/products/3213/installations');
		expect(none.status).toBe(200);
		expect((await none.json<any>()).data).toEqual([]);
		expect(await ids('/v1/attractions?view=latest&product=3213')).toEqual([9101]);
	});

	it('redirects installations of a merged product, and refuses a product it does not hold', async () => {
		const res = await get('/v1/products/99201/installations?view=latest');
		expect(res.status).toBe(301);
		expect(res.headers.get('location')).toBe(`${BASE}/v1/products/3213/installations?view=latest`);
		// TWISTER is linked by Élan but is not a product the API holds yet.
		expect((await get('/v1/products/58307/installations')).status).toBe(404);
		expect((await get('/v1/products/171/installations')).status).toBe(404);
	});

	it('counts products in /v1/meta, per view', async () => {
		const latest = await (await exports.default.fetch(new Request(`${BASE}/v1/meta?view=latest`))).json<any>();
		expect(latest.counts).toEqual({ attractions: 7, parks: 0, manufacturers: 7, products: 8, park_companies: 0 });
		expect(latest.schema_version).toBe('1.0.0-beta.24');
		const settled = await (await exports.default.fetch(new Request(`${BASE}/v1/meta`))).json<any>();
		expect(settled.counts.products).toBe(7);
	});

	it('logs product changes for /v1/changes, by kind and view', async () => {
		const made = await (await get('/v1/changes?view=latest&kinds=product&since=2026-01-01T00:00:00Z')).json<any>();
		expect(made.data).toHaveLength(9);
		expect(made.data.every((c: any) => c.kind === 'product')).toBe(true);
		expect(made.data.at(-1)).toMatchObject({ kind: 'product', id: 99201, op: 'delete', merged_into: 3213 });

		const settled = await (await get('/v1/changes?kinds=product&since=2026-01-01T00:00:00Z')).json<any>();
		expect(settled.data.map((c: any) => c.id)).not.toContain(86315);
	});

	it('lists the product statuses and capability vocabularies in /v1/vocabularies', async () => {
		const body = await (await exports.default.fetch(new Request(`${BASE}/v1/vocabularies`))).json<any>();
		const status = Object.fromEntries(body.data.status.map((s: any) => [s.value, s.applies_to]));
		expect(status.in_production).toEqual(['product']);
		expect(status.cancelled).toContain('product');
		expect(status.operating).not.toContain('product');

		expect(body.data.restraints[0]).toEqual({ value: 'lap_bars', label: 'Lap-bars', applies_to: ['coaster', 'ride', 'product'] });
		// Multi is offered by the slide infobox only.
		expect(body.data.classes.map((c: any) => c.value)).toEqual(['body', 'mat', 'tube', 'sled', 'multi']);
		expect(Object.keys(body.data)).toEqual(['type', 'status', 'restraints', 'propulsion', 'track_material', 'support_material',
			'classes', 'environment', 'containment', 'exit', 'slide_material']);
	});

	it('promotes a product, and its manufacturer links follow it into the settled view', async () => {
		await ingest({ as_of: '2026-10-19T12:00:00Z', upsert: [], delete: [], promote: [{ kind: 'product', id: 86315 }] });
		expect((await get('/v1/products/86315')).status).toBe(200);
		expect(await ids('/v1/products?manufacturer=2399')).toEqual([18364, 86315]);

		// An untrusted change to Boomerang's manufacturer, then the revert back to what settled.
		const b = upsert(3213);
		const manufacturersNow = [{ id: 2399, name: 'Zamperla' }];
		await ingest({
			as_of: '2026-10-19T13:00:00Z', delete: [],
			upsert: [{
				...b, revision: 801, settled: false, settles_at: '2026-10-26T13:00:00Z',
				summary: { ...b.summary, manufacturers: manufacturersNow }, record: { ...b.record, manufacturers: manufacturersNow, revision: 801 },
			}],
		});
		expect(await ids('/v1/products?view=latest&manufacturer=2399')).toEqual([3213, 18364, 86315]);
		expect(await ids('/v1/products?manufacturer=548')).toEqual([3213, 68541]);
		expect(await ids('/v1/products?view=latest&manufacturer=548')).toEqual([68541]);

		await ingest({ as_of: '2026-10-19T14:00:00Z', upsert: [], delete: [], discard: [{ kind: 'product', id: 3213 }] });
		expect(await ids('/v1/products?view=latest&manufacturer=548')).toEqual([3213, 68541]);
		expect(await ids('/v1/products?view=latest&manufacturer=2399')).toEqual([18364, 86315]);
	});

	it('drops a deleted product from the manufacturer links', async () => {
		await ingest({ as_of: '2026-10-19T15:00:00Z', upsert: [], delete: [{ kind: 'product', id: 54013, merged_into: null }] });
		expect(await ids('/v1/products?manufacturer=546')).toEqual([]);
		const { results } = await env.DATA.prepare('SELECT * FROM product_manufacturers WHERE id = 54013').all();
		expect(results).toEqual([]);
	});
});

describe('what /v1/products refuses', () => {
	beforeAll(async () => {
		await env.KEYS.put(`key:${await sha256Hex(REFUSED_KEY)}`, JSON.stringify({ account: 'products-refused', tier: 'beta', status: 'active' }));
	});

	it.each([
		['a time with no offset', '/v1/products?updated_since=2026-10-01', 400, 'parameter-invalid'],
		['a search with no word of two letters', '/v1/products?q=b', 400, 'parameter-invalid'],
		['an attraction filter', '/v1/products?country=NL', 400, 'parameter-unknown'],
		['a sort products do not have', '/v1/products?sort=height', 400, 'parameter-unavailable'],
		['more than one manufacturer', '/v1/products?manufacturer=548,2399', 400, 'parameter-invalid'],
		['a cursor from a stat sort', `/v1/products?cursor=${encodeCursor('height', 20, 1)}`, 400, 'cursor-invalid'],
		['a product ID that is not a number', '/v1/products/boomerang', 404, 'not-found'],
		['a list filter on the one-record endpoint', '/v1/products/3213?q=boomerang', 400, 'parameter-unknown'],
		['a second manufacturer under a manufacturer', '/v1/manufacturers/548/products?manufacturer=2399', 400, 'parameter-unknown'],
		['a manufacturer ID that is not a number', '/v1/manufacturers/vekoma/products', 404, 'not-found'],
		['a second product under a product', '/v1/products/3213/installations?product=68541', 400, 'parameter-unknown'],
		['a park-only parameter', '/v1/products/3213/installations?include=former', 400, 'parameter-unknown'],
		['a product ID that is not a number', '/v1/products/boomerang/installations', 404, 'not-found'],
		['an empty variant', '/v1/products/3213/installations?variant=', 400, 'parameter-invalid'],
	])('%s', async (_, path, status, type) => {
		const res = await get(path, REFUSED_KEY);
		expect(res.status).toBe(status);
		expect((await res.json<any>()).type).toMatch(new RegExp(`${type}$`));
	});
});

describe('a product list reads no more than the table, and a manufacturer its own range', () => {
	// About three times the 1,092 of 2026-10-12, so the bound holds as the table grows.
	const N = 3000;
	const name = (i: number) => `product ${String((i * 7919) % N).padStart(5, '0')}`;
	// Roughly the wiki's mix: two in three rides, one in four coasters.
	const TYPES = ['ride', 'ride', 'coaster', 'ride', 'ride', 'coaster', 'ride', 'ride', 'coaster', 'ride', 'water_slide', 'ride'];

	beforeAll(async () => {
		await resetData();
		const statements = [];
		// Every product's installation counter, as the attractions' triggers would leave them.
		const counts: string[] = [];
		for (let start = 1; start <= N; start += 250) {
			const values = [];
			for (let i = start; i < start + 250; i++) {
				const status = i % 5 < 3 ? 'in_production' : 'discontinued';
				// 30 manufacturers; the largest holds every tenth product, about 300, Zamperla-sized.
				const maker = i % 10 === 0 ? 1 : 2 + (i % 29);
				const summary = JSON.stringify({ id: i, manufacturers: [{ id: maker, name: `m${maker}` }] });
				counts.push(`(${i}, ${i % 7}, ${i % 7})`);
				values.push(`(${i}, 1, 1, '${TYPES[i % TYPES.length]}', '${status}', '${name(i)}', 1, '2026-10-12T00:00:00Z', '${summary}', '{}')`);
			}
			statements.push(env.DATA.prepare(
				`INSERT INTO products (id, in_latest, in_settled, type, status, sort_name, revision, updated_at, summary, record) VALUES ${values.join(',')}`,
			));
		}
		for (let start = 0; start < counts.length; start += 500) {
			statements.push(env.DATA.prepare(
				`INSERT INTO product_installations (product, latest, settled) VALUES ${counts.slice(start, start + 500).join(',')}`,
			));
		}
		await env.DATA.batch(statements);
	});

	// Each bound includes one key read per row returned for the installations count (0011).
	const mid = encodeCursor('name', 'product 01500', 0);
	it.each([
		['by name', `/v1/products?view=latest&limit=100&cursor=${mid}`, null, 101, 300],
		['by name, descending, settled', `/v1/products?limit=100&sort=-name&cursor=${mid}`, null, 101, 300],
		// No filter index (0009): the name index walked past what it skips (one in twelve is a slide, so about
		// 1,200 rows for a page), bounded by the table: at most about 1,100 rows on the real one.
		['filtered to water slides', `/v1/products?view=latest&limit=100&type=water_slide`, null, 101, 1600],
		['filtered by status', `/v1/products?view=latest&limit=100&status=discontinued&cursor=${mid}`, null, 101, 500],
		// The link index alone, then two key reads per row returned: about 300 for a page.
		['by the largest manufacturer', `/v1/products?view=latest&limit=100`, 1, 101, 400],
		['by the largest manufacturer, deep, settled', `/v1/products?limit=100&sort=-name&cursor=${mid}`, 1, 101, 400],
		['by a manufacturer and a type', `/v1/products?view=latest&limit=100&type=coaster`, 1, 100, 600],
	])('%s', async (_, url, manufacturer, rows, bound) => {
		const { sql, binds } = productListQuery(new URL(url, BASE), manufacturer);
		const result = await env.DATA.prepare(sql).bind(...binds).all();
		expect(result.results.length).toBe(rows);
		expect(result.meta.rows_read).toBeLessThan(bound);
	});
});
