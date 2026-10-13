import { env, exports } from 'cloudflare:workers';
import { beforeAll, describe, expect, it } from 'vitest';
import { sha256Hex } from '../src/auth';
import { facetQueries, linkItems } from '../src/facets';
import { resetData } from './reset';

// DR-010 §7.4: the browser's Manufacturer → Product → Variant cascade, as
// facet dimensions counted from the link tables.

const BASE = 'https://api.coasterpedia.net';
const KEY = 'cpk_linkfacets';

const VEKOMA = 548;
const ZAMPERLA = 3000;
// Linked, but no manufacturer record: counted, with no label.
const ARROW = 1829;
const BOOMERANG = 3213;
const SLC = 3300;
// Another maker's product of the same name.
const Z_BOOMERANG = 3400;

type Ref = { id?: number; name: string };
type Built = { product: Ref; variant: string | null };

function attraction(id: number, name: string, type: string, status: string, country: string, manufacturers: Ref[], products: Built[],
	{ settlesAt = null as string | null, park = null as number | null, kind = null as string | null } = {}) {
	const summary = { id, type, name, status, country: { code: country, name: country }, manufacturers, products, updated_at: '2026-10-13T12:00:00Z' };
	return {
		kind: 'attraction', id, revision: 1, updated_at: '2026-10-13T12:00:00Z', settled: settlesAt == null, settles_at: settlesAt,
		sort_name: name.toLowerCase(), sort_values: { height: null, speed: null, length: null },
		filters: { type, status, country, park, location_kind: kind },
		summary, record: { ...summary, title: name, revision: 1 },
	};
}

function page(kind: 'manufacturer' | 'product', id: number, name: string, makers: Ref[] = []) {
	const summary = { id, name, ...(kind === 'product' ? { type: 'coaster', manufacturers: makers } : {}) };
	return {
		kind, id, revision: 1, updated_at: '2026-10-13T12:00:00Z', settled: true, settles_at: null, sort_name: name.toLowerCase(),
		filters: kind === 'product' ? { type: 'coaster', status: 'active' } : { status: 'active', country: null },
		summary, record: { ...summary, revision: 1 },
	};
}

const made = (id: number, name: string): Ref => ({ id, name });
const built = (id: number, name: string, variant: string | null = null): Built => ({ product: { id, name }, variant });
const V = made(VEKOMA, 'Vekoma');
const Z = made(ZAMPERLA, 'Zamperla');
const A = made(ARROW, 'Arrow');

const ATTRACTIONS = [
	attraction(1, 'Alpha', 'coaster', 'operating', 'US', [V], [built(BOOMERANG, 'Boomerang', 'Standard')], { park: 50, kind: 'park' }),
	attraction(2, 'Bravo', 'coaster', 'defunct', 'GB', [V], [built(BOOMERANG, 'Boomerang')], { park: 50, kind: 'park' }),
	// Two makers and two products; the variant spelt as Alpha's, but lower case.
	attraction(3, 'Charlie', 'coaster', 'operating', 'GB', [V, A], [built(BOOMERANG, 'Boomerang', 'standard'), built(SLC, 'SLC')], { kind: 'park' }),
	attraction(4, 'Delta', 'ride', 'operating', 'US', [Z], [built(Z_BOOMERANG, 'Boomerang', 'Mk2')], { kind: 'travelling' }),
	// Latest only.
	attraction(5, 'Echo', 'ride', 'operating', 'US', [Z], [built(Z_BOOMERANG, 'Boomerang', 'Mk2')], { settlesAt: '2026-10-20T12:00:00Z' }),
	attraction(6, 'Foxtrot', 'coaster', 'operating', 'US', [V], [built(SLC, 'SLC', 'Long')], { kind: 'park' }),
	// Unlinked text: in the total, under no manufacturer or product.
	attraction(7, 'Golf', 'ride', 'operating', 'GB', [{ name: 'Self-built' }], [{ product: { name: 'One-off' }, variant: null }]),
];

const PAGES = [
	page('manufacturer', VEKOMA, 'Vekoma'), page('manufacturer', ZAMPERLA, 'Zamperla'),
	page('product', BOOMERANG, 'Boomerang', [V]), page('product', SLC, 'SLC', [V]), page('product', Z_BOOMERANG, 'Boomerang', [Z]),
];

const ingest = (upsert: unknown[]) => exports.default.fetch(new Request(`${BASE}/internal/ingest`, {
	method: 'POST',
	headers: { 'content-type': 'application/json', 'x-dev-ingest-token': 'test-ingest-token' },
	body: JSON.stringify({ as_of: '2026-10-13T12:00:00Z', upsert, delete: [] }),
}));

async function facets(query: string) {
	const res = await exports.default.fetch(new Request(`${BASE}/v1/attractions/facets?${query}`, { headers: { authorization: `Bearer ${KEY}` } }));
	expect(res.status).toBe(200);
	return (await res.json<any>()).data;
}

describe('manufacturer, product and variant facets', () => {
	beforeAll(async () => {
		await resetData();
		await env.KEYS.put(`key:${await sha256Hex(KEY)}`, JSON.stringify({ account: 'linkfacets', tier: 'beta', status: 'active' }));
		expect((await ingest(PAGES)).status).toBe(200);
		expect((await ingest(ATTRACTIONS)).status).toBe(200);
	});

	it('counts every linked manufacturer and product, labelled, and no variant without a product', async () => {
		const data = await facets('view=latest');
		expect(data.total).toBe(7);
		expect(data.dimensions.manufacturer).toEqual([
			{ value: String(VEKOMA), label: 'Vekoma', count: 4 },
			{ value: String(ZAMPERLA), label: 'Zamperla', count: 2 },
			{ value: String(ARROW), count: 1 },
		]);
		// Two makers' Boomerangs are told apart by their makers.
		expect(data.dimensions.product).toEqual([
			{ value: String(BOOMERANG), label: 'Boomerang (Vekoma)', count: 3 },
			{ value: String(Z_BOOMERANG), label: 'Boomerang (Zamperla)', count: 2 },
			{ value: String(SLC), label: 'SLC', count: 2 },
		]);
		expect(data.dimensions.variant).toBeUndefined();
	});

	it('leaves a manufacturer out of its own list, and narrows the products by it', async () => {
		const data = await facets(`view=latest&manufacturer=${VEKOMA}`);
		expect(data.total).toBe(4);
		expect(data.dimensions.manufacturer.map((i: any) => [i.value, i.count])).toEqual([[String(VEKOMA), 4], [String(ZAMPERLA), 2], [String(ARROW), 1]]);
		// Only Vekoma's Boomerang is left, so it needs no maker.
		expect(data.dimensions.product).toEqual([
			{ value: String(BOOMERANG), label: 'Boomerang', count: 3 },
			{ value: String(SLC), label: 'SLC', count: 2 },
		]);
		expect(data.dimensions.type).toEqual([{ value: 'coaster', count: 4 }]);
	});

	it('lists one product\'s variants, case-blind as the filter matches, and narrows the makers by the product', async () => {
		const data = await facets(`view=latest&product=${BOOMERANG}`);
		expect(data.total).toBe(3);
		// Bravo names no variant: in the total, not listed.
		expect(data.dimensions.variant).toHaveLength(1);
		expect(data.dimensions.variant[0].value.toLowerCase()).toBe('standard');
		expect(data.dimensions.variant[0].count).toBe(2);
		expect(data.dimensions.manufacturer.map((i: any) => [i.value, i.count])).toEqual([[String(VEKOMA), 3], [String(ARROW), 1]]);
		// The product's own list leaves it out.
		expect(data.dimensions.product.map((i: any) => i.value)).toEqual([String(BOOMERANG), String(Z_BOOMERANG), String(SLC)]);

		const variant = await facets(`view=latest&product=${BOOMERANG}&variant=${data.dimensions.variant[0].value}`);
		expect(variant.total).toBe(2);
		expect(variant.dimensions.variant).toEqual(data.dimensions.variant);
		expect(variant.dimensions.manufacturer.map((i: any) => [i.value, i.count])).toEqual([[String(VEKOMA), 2], [String(ARROW), 1]]);
		// Changing the product clears the variant, so the product list ignores it too.
		expect(variant.dimensions.product).toEqual(data.dimensions.product);
	});

	it('takes a manufacturer and a product together', async () => {
		const data = await facets(`view=latest&manufacturer=${ARROW}&product=${SLC}`);
		expect(data.total).toBe(1);
		expect(data.dimensions.manufacturer.map((i: any) => [i.value, i.count])).toEqual([[String(VEKOMA), 2], [String(ARROW), 1]]);
		expect(data.dimensions.product.map((i: any) => [i.value, i.count])).toEqual([[String(BOOMERANG), 1], [String(SLC), 1]]);
		// Charlie names no SLC variant.
		expect(data.dimensions.variant).toEqual([]);
	});

	it('applies the other dimensions: from the link table alone, or with the row for the kind', async () => {
		const us = await facets('view=latest&country=US&type=coaster,ride');
		expect(us.dimensions.manufacturer.map((i: any) => [i.value, i.count])).toEqual([[String(VEKOMA), 2], [String(ZAMPERLA), 2]]);
		expect(us.dimensions.product.map((i: any) => [i.label, i.count])).toEqual([['Boomerang (Zamperla)', 2], ['Boomerang (Vekoma)', 1], ['SLC', 1]]);

		const travelling = await facets('view=latest&location_kind=travelling');
		expect(travelling.dimensions.manufacturer.map((i: any) => [i.value, i.count])).toEqual([[String(ZAMPERLA), 1]]);
	});

	it('counts under a narrowing filter: a park, a name', async () => {
		const park = await facets('view=latest&park=50');
		expect(park.total).toBe(2);
		expect(park.dimensions.manufacturer.map((i: any) => [i.value, i.count])).toEqual([[String(VEKOMA), 2]]);
		expect(park.dimensions.product.map((i: any) => [i.value, i.count])).toEqual([[String(BOOMERANG), 2]]);
		const parkProduct = await facets(`view=latest&park=50&product=${BOOMERANG}`);
		expect(parkProduct.dimensions.variant.map((i: any) => i.count)).toEqual([1]);

		const named = await facets('view=latest&q=foxtrot');
		expect(named.dimensions.product.map((i: any) => [i.value, i.count])).toEqual([[String(SLC), 1]]);
	});

	it('counts the settled view on its own', async () => {
		const data = await facets('');
		expect(data.total).toBe(6);
		expect(data.dimensions.manufacturer.map((i: any) => [i.value, i.count])).toEqual([[String(VEKOMA), 4], [String(ARROW), 1], [String(ZAMPERLA), 1]]);
	});

	it('follows a merged manufacturer into the dimensions', async () => {
		await exports.default.fetch(new Request(`${BASE}/internal/ingest`, {
			method: 'POST',
			headers: { 'content-type': 'application/json', 'x-dev-ingest-token': 'test-ingest-token' },
			body: JSON.stringify({ as_of: '2026-10-13T12:00:00Z', upsert: [], delete: [{ kind: 'manufacturer', id: 99101, merged_into: VEKOMA }] }),
		}));
		const data = await facets('view=latest&manufacturer=99101');
		expect(data.total).toBe(4);
		expect(data.dimensions.product.map((i: any) => i.value)).toEqual([String(BOOMERANG), String(SLC)]);
	});
});

describe('linkItems', () => {
	it('adds the makers only where names collide, and sorts by count then label', () => {
		const label = (name: string, maker: string) => JSON.stringify([name, [{ id: 1, name: maker }]]);
		expect(linkItems([
			{ value: 9, n: 2, label: label('Wave Swinger', 'Zierer') },
			{ value: 8, n: 2, label: label('wave swinger', 'Zamperla') },
			{ value: 7, n: 5, label: label('Disk\'O', 'Zamperla') },
			{ value: 6, n: 2, label: null },
		])).toEqual([
			{ value: '7', label: 'Disk\'O', count: 5 },
			{ value: '6', count: 2 },
			{ value: '9', label: 'Wave Swinger (Zierer)', count: 2 },
			{ value: '8', label: 'wave swinger (Zamperla)', count: 2 },
		]);
	});
});

describe('the link dimensions read what drives them, never the whole link table when something narrower does', () => {
	const N = 6000;
	const BIG = 7;
	const SMALL = 8;
	const PRODUCT = 9;
	const PARK = 70;
	const TYPES = ['coaster', 'ride', 'ride', 'ride', 'water_slide'];

	beforeAll(async () => {
		await resetData();
		const statements = [];
		for (let start = 1; start <= N; start += 500) {
			const values = [];
			for (let i = start; i < start + 500; i++) {
				// One in three is the big maker's; one in thirty also links a small one;
				// one in twenty names the product; one in a hundred is at the park.
				const makers = [i % 3 === 0 ? `{"id":${BIG},"name":"Big"}` : `{"id":${1000 + (i % 200)},"name":"M"}`,
					...(i % 30 === 0 ? [`{"id":${SMALL},"name":"Small"}`] : [])];
				const products = i % 20 === 0 ? `[{"product":{"id":${PRODUCT},"name":"P"},"variant":"v${i % 4}"}]` : '[]';
				values.push(`(${i}, 1, 1, '${TYPES[i % TYPES.length]}', 'operating', 'GB', 'a ${String(i).padStart(5, '0')}', 1, '2026-10-13T00:00:00Z',
					'{"manufacturers":[${makers.join(',')}],"products":${products}}', '{}', ${i % 100 === 0 ? PARK : 'NULL'})`);
			}
			statements.push(env.DATA.prepare(
				`INSERT INTO attractions (id, in_latest, in_settled, type, status, country, sort_name, revision, updated_at, summary, record, park)
				 VALUES ${values.join(',')}`,
			));
		}
		await env.DATA.batch(statements);
	});

	const read = async (query: string, dim: 'manufacturer' | 'product' | 'variant') => {
		const q = facetQueries(new URL(`/v1/attractions/facets?view=latest&${query}`, BASE));
		const result = await env.DATA.prepare(q.links[dim]!.sql).bind(...q.links[dim]!.binds).all<{ value: number; n: number }>();
		return { rows: result.results, read: result.meta.rows_read };
	};

	it.each([
		// The whole link table, walked in its value order (6,200 links): no sort, plus a name per value.
		['every manufacturer, unfiltered', '', 'manufacturer', 6200, 7000],
		['every manufacturer of a type', 'type=coaster', 'manufacturer', 1400, 7000],
		// The product's 300 rows, each joined to its links, then the GROUP BY's sort.
		['the makers of one product', `product=${PRODUCT}`, 'manufacturer', 400, 1500],
		// The big maker's 2,000 rows, each looked up in the product links.
		['the products of one maker', `manufacturer=${BIG}`, 'product', 100, 2600],
		['one product\'s variants', `product=${PRODUCT}`, 'variant', 300, 700],
		// The park's 60 rows, then their links.
		['the makers at one park', `park=${PARK}`, 'manufacturer', 80, 400],
	])('%s', async (_, query, dim, links, bound) => {
		const { rows, read: n } = await read(query, dim as 'manufacturer');
		expect(rows.reduce((sum, r) => sum + r.n, 0)).toBe(links);
		expect(n).toBeLessThan(bound);
	});
});
