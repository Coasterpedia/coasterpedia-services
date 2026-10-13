import { env, exports } from 'cloudflare:workers';
import { beforeAll, beforeEach, describe, expect, it } from 'vitest';
import { encodeCursor, listQuery } from '../src/attractions';
import { sha256Hex } from '../src/auth';
import { facetQueries } from '../src/facets';
import { resetData } from './reset';

const BASE = 'https://api.coasterpedia.net';
const KEY = 'cpk_installations';

const BOOMERANG = 3213;
const SLC = 3300;
const VEKOMA = 548;
const ARROW = 1829;

type Ref = { id?: number; name: string };
type Built = { product: Ref; variant: string | null };

// Wire.Upsert's shape (Projector.Tests pins it), with manufacturers, products and a height.
function upsert(id: number, name: string, type: string, status: string, products: Built[], manufacturers: Ref[] = [],
	height: number | null = null, revision = 1, settlesAt: string | null = null) {
	const summary: Record<string, unknown> = {
		id, type, name, status, country: { code: 'GB', name: 'UK' }, manufacturers, products,
		...(height != null ? { stats: { height: { value: height, unit: 'm' } } } : {}),
		updated_at: '2026-10-12T12:00:00Z',
	};
	return {
		kind: 'attraction', id, revision, updated_at: '2026-10-12T12:00:00Z',
		settled: settlesAt == null, settles_at: settlesAt,
		sort_name: name.toLowerCase(),
		sort_values: { height, speed: null, length: null },
		filters: { type, status, country: 'GB' },
		summary,
		record: { ...summary, title: name, revision },
	};
}

const product = (id: number, name: string, variant: string | null = null): Built => ({ product: { id, name }, variant });

const ingest = (body: Record<string, unknown>) => exports.default.fetch(new Request(`${BASE}/internal/ingest`, {
	method: 'POST',
	headers: { 'content-type': 'application/json', 'x-dev-ingest-token': 'test-ingest-token' },
	body: JSON.stringify({ as_of: '2026-10-12T12:00:00Z', upsert: [], delete: [], ...body }),
}));

const get = (path: string) =>
	exports.default.fetch(new Request(BASE + path, { redirect: 'manual', headers: { authorization: `Bearer ${KEY}` } }));

const ids = async (path: string) => {
	const res = await get(path);
	expect(res.status).toBe(200);
	return (await res.json<any>()).data.map((a: any) => a.id);
};

const count = async (id: number, view = 'latest') =>
	(await (await get(`/v1/products/${id}?view=${view}`)).json<any>()).data.installations;

const BOOMERANG_PRODUCT = {
	kind: 'product', id: BOOMERANG, revision: 1, updated_at: '2026-10-12T12:00:00Z', settled: true, settles_at: null,
	sort_name: 'boomerang', filters: { type: 'coaster', status: 'discontinued' },
	summary: { id: BOOMERANG, type: 'coaster', name: 'Boomerang', manufacturers: [{ id: VEKOMA, name: 'Vekoma' }] },
	record: { id: BOOMERANG, type: 'coaster', name: 'Boomerang', manufacturers: [{ id: VEKOMA, name: 'Vekoma' }], revision: 1 },
};
const SLC_PRODUCT = {
	...BOOMERANG_PRODUCT, id: SLC, sort_name: 'slc',
	summary: { ...BOOMERANG_PRODUCT.summary, id: SLC, name: 'SLC' }, record: { ...BOOMERANG_PRODUCT.record, id: SLC, name: 'SLC' },
};

const ALPHA = upsert(1, 'Alpha', 'coaster', 'operating', [product(BOOMERANG, 'Boomerang', 'Standard')], [{ id: VEKOMA, name: 'Vekoma' }], 35);
// Two links that resolve to one page are one installation.
const BRAVO = upsert(2, 'Bravo', 'coaster', 'defunct', [product(BOOMERANG, 'Boomerang'), product(BOOMERANG, 'Boomerang (old title)')],
	[{ id: VEKOMA, name: 'Vekoma' }]);
// A complex of two products, with the other manufacturer.
const CHARLIE = upsert(3, 'Charlie', 'coaster', 'operating', [product(BOOMERANG, 'Boomerang'), product(SLC, 'SLC', null)],
	[{ id: ARROW, name: 'Arrow' }], 20);
// Unlinked text has no ID, so it is no installation.
const DELTA = upsert(4, 'Delta', 'ride', 'operating', [{ product: { name: 'Play Structure Type A02' }, variant: null }]);
// Never settled: latest only.
const ECHO = upsert(5, 'Echo', 'coaster', 'operating', [product(BOOMERANG, 'Boomerang', 'standard')], [{ id: VEKOMA, name: 'Vekoma' }], 40, 1,
	'2026-10-19T12:00:00Z');

describe('/v1/products/{id}/installations and ?product=', () => {
	beforeEach(async () => {
		await resetData();
		await env.KEYS.put(`key:${await sha256Hex(KEY)}`, JSON.stringify({ account: 'installations', tier: 'beta', status: 'active' }));
		expect((await ingest({ upsert: [BOOMERANG_PRODUCT, SLC_PRODUCT, ALPHA, BRAVO, CHARLIE, DELTA, ECHO] })).status).toBe(200);
	});

	it("lists every attraction built as the product, once each, in each view", async () => {
		expect(await ids(`/v1/products/${BOOMERANG}/installations?view=latest`)).toEqual([1, 2, 3, 5]);
		expect(await ids(`/v1/products/${BOOMERANG}/installations`)).toEqual([1, 2, 3]);
		expect(await ids(`/v1/products/${SLC}/installations`)).toEqual([3]);
		// The same list from /v1/attractions.
		expect(await ids(`/v1/attractions?view=latest&product=${BOOMERANG}`)).toEqual([1, 2, 3, 5]);
		expect(await ids('/v1/attractions?view=latest&product=77')).toEqual([]);
	});

	it('counts installations on the product, per view, as the list has them', async () => {
		expect(await count(BOOMERANG)).toBe(4);
		expect(await count(BOOMERANG, 'settled')).toBe(3);
		expect(await count(SLC)).toBe(1);
		const list = await (await get('/v1/products?view=latest')).json<any>();
		expect(list.data.map((p: any) => [p.id, p.installations])).toEqual([[BOOMERANG, 4], [SLC, 1]]);
	});

	it('narrows to a variant, case-blind', async () => {
		expect(await ids(`/v1/products/${BOOMERANG}/installations?view=latest&variant=Standard`)).toEqual([1, 5]);
		expect(await ids(`/v1/products/${BOOMERANG}/installations?variant=STANDARD`)).toEqual([1]);
		expect(await ids(`/v1/products/${BOOMERANG}/installations?variant=Batman`)).toEqual([]);
	});

	it('combines with the other filters, a manufacturer included, and sorts by a stat', async () => {
		expect(await ids(`/v1/products/${BOOMERANG}/installations?view=latest&status=operating`)).toEqual([1, 3, 5]);
		expect(await ids(`/v1/products/${BOOMERANG}/installations?view=latest&manufacturer=${VEKOMA}`)).toEqual([1, 2, 5]);
		expect(await ids(`/v1/attractions?view=latest&product=${BOOMERANG}&manufacturer=${ARROW}`)).toEqual([3]);
		expect(await ids(`/v1/products/${BOOMERANG}/installations?view=latest&sort=-height`)).toEqual([5, 1, 3]);
	});

	it('pages with a cursor', async () => {
		const first = await (await get(`/v1/products/${BOOMERANG}/installations?view=latest&limit=2`)).json<any>();
		expect(first.data.map((a: any) => a.id)).toEqual([1, 2]);
		const second = await (await get(`/v1/products/${BOOMERANG}/installations?view=latest&limit=2&cursor=${first.meta.next_cursor}`)).json<any>();
		expect(second.data.map((a: any) => a.id)).toEqual([3, 5]);
		expect(second.meta.next_cursor).toBeNull();
	});

	it('follows an edit: a product changed, a variant changed, a status changed', async () => {
		await ingest({ upsert: [upsert(1, 'Alpha', 'coaster', 'defunct', [product(SLC, 'SLC', 'Junior')], [], 35, 2)] });
		expect(await ids(`/v1/products/${BOOMERANG}/installations?view=latest`)).toEqual([2, 3, 5]);
		expect(await ids(`/v1/products/${SLC}/installations?view=latest&variant=junior&status=defunct`)).toEqual([1]);
		expect(await count(BOOMERANG)).toBe(3);
		expect(await count(SLC)).toBe(2);
	});

	it('keeps a pending change out of the settled count until it settles, and back on a discard', async () => {
		// An untrusted edit moves Alpha to the SLC.
		await ingest({ upsert: [upsert(1, 'Alpha', 'coaster', 'operating', [product(SLC, 'SLC')], [], 35, 2, '2026-10-19T12:00:00Z')] });
		expect([await count(BOOMERANG), await count(BOOMERANG, 'settled')]).toEqual([3, 3]);
		expect([await count(SLC), await count(SLC, 'settled')]).toEqual([2, 1]);

		await ingest({ discard: [{ kind: 'attraction', id: 1 }] });
		expect([await count(BOOMERANG), await count(SLC)]).toEqual([4, 1]);

		// Echo settles.
		await ingest({ promote: [{ kind: 'attraction', id: 5 }] });
		expect(await count(BOOMERANG, 'settled')).toBe(4);
		expect(await ids(`/v1/products/${BOOMERANG}/installations`)).toEqual([1, 2, 3, 5]);
	});

	it('drops a deleted attraction from the list and the count', async () => {
		await ingest({ delete: [{ kind: 'attraction', id: 3, merged_into: null }] });
		expect(await ids(`/v1/products/${BOOMERANG}/installations?view=latest`)).toEqual([1, 2, 5]);
		expect(await count(SLC)).toBe(0);
		const { results } = await env.DATA.prepare('SELECT COUNT(*) AS n FROM attraction_products WHERE id = 3').all<{ n: number }>();
		expect(results[0].n).toBe(0);
	});

	it('follows a merged product in the filter, and redirects its installations', async () => {
		await ingest({ delete: [{ kind: 'product', id: 99201, merged_into: BOOMERANG }] });
		expect(await ids('/v1/attractions?view=latest&product=99201')).toEqual([1, 2, 3, 5]);
		const res = await get('/v1/products/99201/installations?variant=standard');
		expect(res.status).toBe(301);
		expect(res.headers.get('location')).toBe(`${BASE}/v1/products/${BOOMERANG}/installations?variant=standard`);
	});

	it('narrows the facets to the product', async () => {
		const body = await (await get(`/v1/attractions/facets?view=latest&product=${BOOMERANG}&status=operating`)).json<any>();
		expect(body.data.total).toBe(3);
		// status is left out of its own list.
		expect(body.data.dimensions.status).toEqual([{ value: 'operating', count: 3 }, { value: 'defunct', count: 1 }]);
		expect(body.data.ranges.height).toMatchObject({ unit: 'm', min: 20, max: 40 });

		const variant = await (await get(`/v1/attractions/facets?view=latest&product=${BOOMERANG}&variant=standard`)).json<any>();
		expect(variant.data.total).toBe(2);
	});

	it.each([
		['a variant with no product', '/v1/attractions?variant=Standard', 400, 'parameter-invalid'],
		['two products', `/v1/attractions?product=${BOOMERANG},${SLC}`, 400, 'parameter-invalid'],
		['a product that is not an ID', '/v1/attractions?product=boomerang', 400, 'parameter-invalid'],
		['a product the API does not hold', '/v1/products/77/installations', 404, 'not-found'],
	])('refuses %s', async (_, path, status, type) => {
		const res = await get(path);
		expect(res.status).toBe(status);
		expect((await res.json<any>()).type).toMatch(new RegExp(`${type}$`));
	});
});

describe('a product-filtered read is bounded by the product, not the table', () => {
	const N = 6000;
	const BIG = 7;
	const SMALL = 8;
	const TYPES = ['coaster', 'ride', 'ride', 'ride', 'water_slide'];
	const name = (i: number) => `attraction ${String((i * 7919) % N).padStart(5, '0')}`;

	beforeAll(async () => {
		await resetData();
		const statements = [];
		for (let start = 1; start <= N; start += 500) {
			const values = [];
			for (let i = start; i < start + 500; i++) {
				// One in twenty is the big product's (300, twice the Wave Swinger's 135);
				// one in three hundred also the small one's.
				const built = [i % 20 === 0 ? `{"product":{"id":${BIG},"name":"Big"},"variant":"${i % 40 === 0 ? 'Mk2' : 'Mk1'}"}` : '{"product":{"name":"Other"},"variant":null}',
					...(i % 300 === 0 ? [`{"product":{"id":${SMALL},"name":"Small"},"variant":null}`] : [])];
				const height = i % 5 < 3 ? String(((i * 104729) % 15000) / 100) : 'NULL';
				values.push(`(${i}, 1, 1, '${TYPES[i % TYPES.length]}', 'operating', 'GB', '${name(i)}', 1, '2026-10-12T00:00:00Z', '{"manufacturers":[],"products":[${built.join(',')}]}', '{}', ${height})`);
			}
			statements.push(env.DATA.prepare(
				`INSERT INTO attractions (id, in_latest, in_settled, type, status, country, sort_name, revision, updated_at, summary, record, height) VALUES ${values.join(',')}`,
			));
		}
		await env.DATA.batch(statements);
	});

	it('fills the links and the counts from the summaries by trigger', async () => {
		const { results } = await env.DATA.prepare('SELECT product, COUNT(*) AS n FROM attraction_products GROUP BY product ORDER BY product').all();
		expect(results).toEqual([{ product: BIG, n: 300 }, { product: SMALL, n: 20 }]);
		const counts = await env.DATA.prepare('SELECT product, latest, settled FROM product_installations ORDER BY product').all();
		expect(counts.results).toEqual([{ product: BIG, latest: 300, settled: 300 }, { product: SMALL, latest: 20, settled: 20 }]);
	});

	it("builds the same links and counts in the migration's backfill as the triggers do", async () => {
		const migration = env.TEST_MIGRATIONS.find((m) => m.name.startsWith('0011_'))!;
		const links = migration.queries.find((q) => q.includes('FROM attractions a, json_each'))!;
		const counts = migration.queries.find((q) => q.includes('INSERT INTO product_installations (product, latest, settled)'))!;
		const linkRows = `SELECT id, in_latest, product, in_settled, variant, type, status, country, sort_name, height FROM attraction_products ORDER BY id, product`;
		const countRows = 'SELECT product, latest, settled FROM product_installations ORDER BY product';
		const byTrigger = [(await env.DATA.prepare(linkRows).all()).results, (await env.DATA.prepare(countRows).all()).results];
		// Straight to the tables: through the triggers, the delete would count down.
		await env.DATA.batch([
			env.DATA.prepare('DROP TRIGGER attraction_products_count_delete'),
			env.DATA.prepare('DELETE FROM attraction_products'),
			env.DATA.prepare('DELETE FROM product_installations'),
			env.DATA.prepare('DROP TRIGGER attraction_products_count_insert'),
			env.DATA.prepare(links),
			env.DATA.prepare(counts),
		]);
		expect([(await env.DATA.prepare(linkRows).all()).results, (await env.DATA.prepare(countRows).all()).results]).toEqual(byTrigger);
		// Put the triggers back for the tests after this one.
		const triggers = migration.queries.filter((q) => /CREATE TRIGGER attraction_products_count_/.test(q));
		await env.DATA.batch(triggers.map((q) => env.DATA.prepare(q)));
	});

	const mid = encodeCursor('name', 'attraction 03000', 0);
	it.each([
		// The product's whole range is under a page and a half, and each row joins back once.
		['by name', `/v1/attractions?view=latest&limit=100&product=${BIG}&cursor=${mid}`, 101, 350],
		['by name, settled, descending', `/v1/attractions?limit=100&sort=-name&product=${BIG}`, 101, 350],
		// A stat sort reads the product's range and sorts it: bounded by the product (300 here).
		['by height', `/v1/attractions?view=latest&limit=100&sort=-height&product=${BIG}`, 101, 1000],
		['a variant', `/v1/attractions?view=latest&limit=100&product=${BIG}&variant=mk2`, 101, 500],
		['a small product', `/v1/attractions?view=latest&limit=100&product=${SMALL}`, 20, 100],
	])('%s', async (_, url, returned, bound) => {
		const { sql, binds } = listQuery(new URL(url, BASE));
		const result = await env.DATA.prepare(sql).bind(...binds).all();
		expect(result.results.length).toBe(returned);
		expect(result.meta.rows_read).toBeLessThan(bound);
	});

	it('writes a link, an index entry and a count per product, and nothing for an edit that moves none', async () => {
		const insert = (id: number, products: string) => env.DATA.prepare(
			`INSERT INTO attractions (id, in_latest, in_settled, type, status, country, sort_name, revision, updated_at, summary, record)
			 VALUES (?1, 1, 1, 'coaster', 'operating', 'GB', 'zz', 1, '2026-10-12T00:00:00Z', ?2, '{}')`,
		).bind(id, `{"products":${products}}`).run();
		const defunct = (id: number) => env.DATA.prepare(`UPDATE attractions SET status = 'defunct' WHERE id = ?`).bind(id).run();

		const bare = await insert(99998, '[]');
		const built = await insert(99999, `[{"product":{"id":${SMALL},"name":"Small"},"variant":null}]`);
		expect(built.meta.rows_written - bare.meta.rows_written).toBe(3);
		expect((await env.DATA.prepare('UPDATE attractions SET revision = 2 WHERE id = 99999').run()).meta.rows_written).toBe(1);
		// A status change rewrites the link: out and back in, the count down and up (5 on top of cost.test's 7).
		expect((await defunct(99999)).meta.rows_written - (await defunct(99998)).meta.rows_written).toBe(5);
	});

	it("counts facets from the product's own rows", async () => {
		const q = facetQueries(new URL(`/v1/attractions/facets?view=latest&product=${BIG}`, BASE));
		const cells = await env.DATA.prepare(q.cells.sql).bind(...q.cells.binds).all<{ n: number }>();
		expect(cells.results.reduce((sum, c) => sum + c.n, 0)).toBe(300);
		// The links, the row joined back for its location kind, the GROUP BY.
		expect(cells.meta.rows_read).toBeLessThan(1000);
		const height = await env.DATA.prepare(q.ranges.height.sql).bind(...q.ranges.height.binds).all();
		expect(height.meta.rows_read).toBeLessThan(1000);
	});
});
