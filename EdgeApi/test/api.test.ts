import { env, exports } from 'cloudflare:workers';
import { beforeEach, describe, expect, it } from 'vitest';
import { sha256Hex } from '../src/auth';
import { resetData } from './reset';

const KEY = 'cpk_testkey0123456789';
const BASE = 'https://api.coasterpedia.net';

// Exactly what the Projector's Wire.Upsert emits (Projector.Tests pins the same shape).
function upsert(id: number, name: string, type: string, status: string | null, country: { code: string; name: string } | null, revision = 1,
	settlesAt: string | null = null) {
	const summary: Record<string, unknown> = { id, type, name, ...(status ? { status } : {}), ...(country ? { country } : {}), manufacturers: [], updated_at: '2026-10-11T12:00:00Z' };
	return {
		kind: 'attraction',
		id,
		revision,
		updated_at: '2026-10-11T12:00:00Z',
		settled: settlesAt == null,
		settles_at: settlesAt,
		sort_name: name.toLowerCase(),
		filters: { type, status, country: country?.code ?? null },
		summary,
		record: { ...summary, title: name, url: `https://coasterpedia.net/wiki/${name}`, location: country ? { country } : {}, revision },
	};
}

function ingest(body: unknown, headers: Record<string, string> = { 'x-dev-ingest-token': 'test-ingest-token' }) {
	return exports.default.fetch(new Request(`${BASE}/internal/ingest`, {
		method: 'POST',
		headers: { 'content-type': 'application/json', ...headers },
		body: JSON.stringify(body),
	}));
}

function get(path: string, key: string | null = KEY) {
	return exports.default.fetch(new Request(BASE + path, { redirect: 'manual', headers: key ? { authorization: `Bearer ${key}` } : {} }));
}

const UK = { code: 'GB', name: 'UK' };
const USA = { code: 'US', name: 'USA' };

beforeEach(async () => {
	// Storage is shared across tests in this pool version; start each from empty.
	await resetData();
	await env.KEYS.put(`key:${await sha256Hex(KEY)}`, JSON.stringify({ account: 'tester', tier: 'beta', status: 'active' }));
	const res = await ingest({
		as_of: '2026-10-11T12:00:00Z',
		upsert: [
			upsert(171, 'Gold Rusher', 'coaster', 'operating', USA),
			upsert(165, 'Batman The Ride', 'coaster', 'operating', USA),
			upsert(169, 'Colossus', 'coaster', 'defunct', USA),
			upsert(19643, 'Over the Hill 2', 'ride', 'operating', UK),
			upsert(66486, 'SkyRide', 'ride', 'operating', null),
		],
		delete: [],
	});
	expect(res.status).toBe(200);
});

describe('ingest', () => {
	it('refuses a caller without Access or the dev token', async () => {
		const res = await ingest({ as_of: '2026-10-11T12:00:00Z', upsert: [], delete: [] }, {});
		expect(res.status).toBe(403);
	});

	it('rejects a malformed batch and writes nothing', async () => {
		const res = await ingest({ as_of: 'yesterday', upsert: [], delete: [] });
		expect(res.status).toBe(400);
		expect(res.headers.get('content-type')).toContain('application/problem+json');
	});

	it('is idempotent', async () => {
		const again = upsert(171, 'Gold Rusher', 'coaster', 'operating', USA);
		await ingest({ as_of: '2026-10-11T12:00:00Z', upsert: [again], delete: [] });
		const body = await (await get('/v1/meta')).json<{ counts: { attractions: number } }>();
		expect(body.counts.attractions).toBe(5);
	});

	it('never moves data_as_of backwards', async () => {
		await ingest({ as_of: '2026-10-12T00:00:00Z', upsert: [], delete: [] });
		await ingest({ as_of: '2026-10-11T00:00:00Z', upsert: [], delete: [] });
		const body = await (await get('/v1/meta', null)).json<{ data_as_of: string }>();
		expect(body.data_as_of).toBe('2026-10-12T00:00:00Z');
	});

	it('logs every change in order', async () => {
		await ingest({ as_of: '2026-10-11T12:00:00Z', upsert: [], delete: [{ kind: 'attraction', id: 169, merged_into: null }] });
		const { results } = await env.DATA.prepare('SELECT id, op FROM changes ORDER BY seq').all();
		expect(results.at(-1)).toEqual({ id: 169, op: 'delete' });
		expect(results).toHaveLength(6);
	});
});

describe('keys', () => {
	it('meta is open', async () => {
		expect((await get('/v1/meta', null)).status).toBe(200);
	});

	it('everything else needs a key', async () => {
		const res = await get('/v1/attractions?view=latest', null);
		expect(res.status).toBe(401);
		expect(res.headers.get('www-authenticate')).toBe('Bearer');
	});

	it('an unknown key is refused, a revoked one forbidden', async () => {
		expect((await get('/v1/attractions?view=latest', 'cpk_nope')).status).toBe(401);
		await env.KEYS.put(`key:${await sha256Hex('cpk_revoked')}`, JSON.stringify({ account: 'x', tier: 'beta', status: 'revoked' }));
		expect((await get('/v1/attractions?view=latest', 'cpk_revoked')).status).toBe(403);
	});
});

describe('GET /v1/attractions', () => {
	it('serves the settled view unless asked for latest', async () => {
		expect((await (await get('/v1/attractions')).json<any>()).meta.view).toBe('settled');
		expect((await get('/v1/attractions?view=settled')).status).toBe(200);
		expect((await get('/v1/attractions?view=draft')).status).toBe(400);
	});

	it('lists by name with the contract envelope', async () => {
		const body = await (await get('/v1/attractions?view=latest')).json<any>();
		expect(body.data.map((a: any) => a.name)).toEqual(['Batman The Ride', 'Colossus', 'Gold Rusher', 'Over the Hill 2', 'SkyRide']);
		expect(body.meta).toMatchObject({ view: 'latest', next_cursor: null, data_as_of: '2026-10-11T12:00:00Z' });
		expect(body.meta.licence.attribution).toBe('Coasterpedia contributors');
	});

	it('filters by type, status and country, any of a comma list', async () => {
		const names = async (q: string) => (await (await get(`/v1/attractions?view=latest&${q}`)).json<any>()).data.map((a: any) => a.id);
		expect(await names('type=ride')).toEqual([19643, 66486]);
		expect(await names('status=defunct')).toEqual([169]);
		expect(await names('country=gb,US&type=coaster&status=operating')).toEqual([165, 171]);
		// Open vocabulary: an unseen value is a valid filter that matches nothing.
		expect(await names('status=demolished')).toEqual([]);
	});

	it('pages with a cursor, both directions', async () => {
		const first = await (await get('/v1/attractions?view=latest&limit=2&sort=-name')).json<any>();
		expect(first.data.map((a: any) => a.name)).toEqual(['SkyRide', 'Over the Hill 2']);
		const second = await (await get(`/v1/attractions?view=latest&limit=2&sort=-name&cursor=${first.meta.next_cursor}`)).json<any>();
		expect(second.data.map((a: any) => a.name)).toEqual(['Gold Rusher', 'Colossus']);
	});

	it('refuses a filter whose field is not published yet rather than ignoring it', async () => {
		const res = await get('/v1/attractions?view=latest&bbox=50,-1,51,0');
		expect(res.status).toBe(400);
		expect((await res.json<any>()).type).toContain('parameter-unavailable');
		expect((await get('/v1/attractions?view=latest&colour=red')).status).toBe(400);
		expect((await get('/v1/attractions?view=latest&sort=updated')).status).toBe(400);
		expect((await get('/v1/attractions?view=latest&limit=101')).status).toBe(400);
		expect((await get('/v1/attractions?view=latest&cursor=garbage')).status).toBe(400);
	});
});

describe('GET /v1/attractions/facets', () => {
	const facets = async (q = '') => (await (await get(`/v1/attractions/facets?view=latest${q}`)).json<any>()).data;

	it('counts every value, with the wiki country name, and leaves a blank out of the lists but not the total', async () => {
		const body = await (await get('/v1/attractions/facets?view=latest')).json<any>();
		expect(body.meta).toMatchObject({ view: 'latest', data_as_of: '2026-10-11T12:00:00Z' });
		expect(body.data.total).toBe(5);
		expect(body.data.dimensions).toEqual({
			type: [{ value: 'coaster', count: 3 }, { value: 'ride', count: 2 }],
			status: [{ value: 'operating', count: 4 }, { value: 'defunct', count: 1 }],
			// SkyRide has no country: counted in the total, not listed.
			country: [{ value: 'US', label: 'USA', count: 3 }, { value: 'GB', label: 'UK', count: 1 }],
			// These upserts predate location: counted in the total, not listed.
			location_kind: [],
		});
		// No stats in these upserts, so no histograms.
		expect(body.data.ranges).toEqual({});
	});

	it('counts each dimension with its own filter left out', async () => {
		const data = await facets('&country=us&status=operating');
		expect(data.total).toBe(2);
		expect(data.dimensions.type).toEqual([{ value: 'coaster', count: 2 }]);
		// Choosing operating does not shrink the status list...
		expect(data.dimensions.status).toEqual([{ value: 'operating', count: 2 }, { value: 'defunct', count: 1 }]);
		// ...nor US the country list.
		expect(data.dimensions.country).toEqual([{ value: 'US', label: 'USA', count: 2 }, { value: 'GB', label: 'UK', count: 1 }]);
	});

	it('follows ingest: changes, deletions and the settled view', async () => {
		await ingest({
			as_of: '2026-10-11T13:00:00Z',
			upsert: [
				upsert(169, 'Colossus', 'coaster', 'operating', USA, 2),
				// Untrusted: latest only until it settles.
				upsert(19643, 'Over the Hill 2', 'ride', 'defunct', UK, 2, '2026-10-18T12:00:00Z'),
			],
			delete: [{ kind: 'attraction', id: 165, merged_into: null }],
		});
		const latest = await facets();
		expect(latest.total).toBe(4);
		expect(latest.dimensions.status).toEqual([{ value: 'operating', count: 3 }, { value: 'defunct', count: 1 }]);
		const settled = (await (await get('/v1/attractions/facets')).json<any>()).data;
		expect(settled.total).toBe(4);
		expect(settled.dimensions.status).toEqual([{ value: 'operating', count: 4 }]);
	});

	it('refuses what the list would refuse, and the list-only parameters', async () => {
		const res = await get('/v1/attractions/facets?q=batman');
		expect(res.status).toBe(400);
		expect((await res.json<any>()).type).toContain('parameter-unavailable');
		expect((await get('/v1/attractions/facets?sort=name')).status).toBe(400);
		expect((await get('/v1/attractions/facets?cursor=x')).status).toBe(400);
		expect((await get('/v1/attractions/facets?view=draft')).status).toBe(400);
		expect((await get('/v1/attractions/facets', null)).status).toBe(401);
	});
});

describe('GET /v1/attractions/{id}', () => {
	it('returns the full record', async () => {
		const body = await (await get('/v1/attractions/19643?view=latest')).json<any>();
		expect(body.data).toMatchObject({ id: 19643, title: 'Over the Hill 2', location: { country: UK } });
	});

	it('answers 301 for a page merged into another, and 404 for one deleted', async () => {
		const res = await ingest({
			as_of: '2026-10-11T12:00:00Z',
			upsert: [],
			delete: [{ kind: 'attraction', id: 169, merged_into: 171 }, { kind: 'attraction', id: 165, merged_into: null }],
		});
		expect(await res.json()).toEqual({ upserted: 0, deleted: 2, promoted: 0, discarded: 0 });
		const merged = await get('/v1/attractions/169?view=latest');
		expect(merged.status).toBe(301);
		expect(merged.headers.get('location')).toBe(`${BASE}/v1/attractions/171?view=latest`);
		expect((await get('/v1/attractions/165?view=latest')).status).toBe(404);
		expect((await get('/v1/attractions/abc?view=latest')).status).toBe(404);
	});
});

describe('the settled view (DR-010 §6.3)', () => {
	const WEEK_ON = '2026-10-18T12:00:00Z';
	const batch = (parts: Record<string, unknown>) => ingest({ as_of: '2026-10-11T13:00:00Z', upsert: [], delete: [], ...parts });
	const names = async (q: string) => (await (await get(`/v1/attractions?${q}`)).json<any>()).data.map((a: any) => a.name);
	const one = async (id: number, view: string) => (await get(`/v1/attractions/${id}?view=${view}`)).json<any>();

	it('keeps the settled version while a change waits, and says so in both views', async () => {
		await batch({ upsert: [upsert(171, 'Gold Vandal', 'coaster', 'operating', USA, 2, WEEK_ON)] });

		expect(await names('view=latest&type=coaster')).toEqual(['Batman The Ride', 'Colossus', 'Gold Vandal']);
		expect(await names('view=settled&type=coaster')).toEqual(['Batman The Ride', 'Colossus', 'Gold Rusher']);

		const settlement = { settled_revision: 1, pending_revision: 2, settles_at: WEEK_ON };
		expect((await one(171, 'settled')).data).toMatchObject({ name: 'Gold Rusher', revision: 1, settlement });
		expect((await one(171, 'latest')).data).toMatchObject({ name: 'Gold Vandal', revision: 2, settlement });
	});

	it('a second untrusted change replaces the pending one, never the settled one', async () => {
		await batch({ upsert: [upsert(171, 'Gold Vandal', 'coaster', 'operating', USA, 2, WEEK_ON)] });
		await batch({ upsert: [upsert(171, 'Gold Vandal 2', 'coaster', 'operating', USA, 3, '2026-10-19T12:00:00Z')] });
		expect((await one(171, 'settled')).data.name).toBe('Gold Rusher');
		expect((await one(171, 'latest')).data.settlement).toEqual({ settled_revision: 1, pending_revision: 3, settles_at: '2026-10-19T12:00:00Z' });
		const { results } = await env.DATA.prepare('SELECT COUNT(*) AS n FROM attractions WHERE id = 171').all<{ n: number }>();
		expect(results[0].n).toBe(2);
	});

	it('promote makes the pending version the settled one', async () => {
		await batch({ upsert: [upsert(171, 'Gold Rusher II', 'coaster', 'operating', USA, 2, WEEK_ON)] });
		expect(await (await batch({ promote: [{ kind: 'attraction', id: 171 }] })).json()).toMatchObject({ promoted: 1 });

		expect((await one(171, 'settled')).data).toMatchObject({
			name: 'Gold Rusher II',
			settlement: { settled_revision: 2, pending_revision: null, settles_at: null },
		});
		expect(await names('view=settled&type=coaster')).toEqual(['Batman The Ride', 'Colossus', 'Gold Rusher II']);
	});

	it('discard puts the latest view back on the settled version', async () => {
		await batch({ upsert: [upsert(171, 'Gold Vandal', 'coaster', 'operating', USA, 2, WEEK_ON)] });
		await batch({ discard: [{ kind: 'attraction', id: 171 }] });

		expect((await one(171, 'latest')).data).toMatchObject({
			name: 'Gold Rusher',
			settlement: { settled_revision: 1, pending_revision: null, settles_at: null },
		});
		expect(await names('view=latest&type=coaster')).toEqual(['Batman The Ride', 'Colossus', 'Gold Rusher']);
	});

	it('discard never removes a record that has no settled version', async () => {
		await batch({ upsert: [upsert(500, 'Brand New', 'ride', 'operating', UK, 9, WEEK_ON)] });
		await batch({ discard: [{ kind: 'attraction', id: 500 }] });
		expect((await one(500, 'latest')).data.name).toBe('Brand New');
	});

	it('a record that has never settled is a 404 there that points at latest', async () => {
		await batch({ upsert: [upsert(500, 'Brand New', 'ride', 'operating', UK, 9, WEEK_ON)] });
		const res = await get('/v1/attractions/500?view=settled');
		expect(res.status).toBe(404);
		const body = await res.json<any>();
		expect(body.type).toContain('not-settled');
		expect(body.detail).toContain('view=latest');
		expect((await one(500, 'latest')).data.settlement).toEqual({ settled_revision: null, pending_revision: 9, settles_at: WEEK_ON });
		expect(await names('view=settled&type=ride')).toEqual(['Over the Hill 2', 'SkyRide']);
	});

	it('a trusted change after a pending one settles both views at once', async () => {
		await batch({ upsert: [upsert(171, 'Gold Vandal', 'coaster', 'operating', USA, 2, WEEK_ON)] });
		await batch({ upsert: [upsert(171, 'Gold Rusher', 'coaster', 'defunct', USA, 3)] });
		expect((await one(171, 'settled')).data).toMatchObject({ status: 'defunct', settlement: { settled_revision: 3, pending_revision: null } });
		expect((await one(171, 'latest')).data.status).toBe('defunct');
	});

	it('logs each change against the views it reached', async () => {
		await batch({ upsert: [upsert(171, 'Gold Vandal', 'coaster', 'operating', USA, 2, WEEK_ON)] });
		await batch({ discard: [{ kind: 'attraction', id: 171 }] });
		await batch({ upsert: [upsert(165, 'Batman', 'coaster', 'operating', USA, 2, WEEK_ON)] });
		await batch({ promote: [{ kind: 'attraction', id: 165 }] });
		const { results } = await env.DATA.prepare(
			'SELECT id, revision, in_latest, in_settled FROM changes ORDER BY seq LIMIT -1 OFFSET 5',
		).all();
		expect(results).toEqual([
			{ id: 171, revision: 2, in_latest: 1, in_settled: 0 }, // the vandal edit: latest only
			{ id: 171, revision: 1, in_latest: 1, in_settled: 0 }, // its revert: latest only
			{ id: 165, revision: 2, in_latest: 1, in_settled: 0 },
			{ id: 165, revision: 2, in_latest: 0, in_settled: 1 }, // settled a week later
		]);
	});

	it('deletes reach both views', async () => {
		await batch({ upsert: [upsert(171, 'Gold Vandal', 'coaster', 'operating', USA, 2, WEEK_ON)] });
		await batch({ delete: [{ kind: 'attraction', id: 171, merged_into: null }] });
		expect((await get('/v1/attractions/171?view=latest')).status).toBe(404);
		expect((await get('/v1/attractions/171?view=settled')).status).toBe(404);
	});

	it('meta counts the view asked for and publishes the window', async () => {
		await batch({ settle_after: 'P7D', upsert: [upsert(500, 'Brand New', 'ride', 'operating', UK, 9, WEEK_ON)] });
		const settled = await (await get('/v1/meta', null)).json<any>();
		const latest = await (await get('/v1/meta?view=latest', null)).json<any>();
		expect(settled).toMatchObject({ view: 'settled', settle_after: 'P7D', counts: { attractions: 5 } });
		expect(latest.counts.attractions).toBe(6);
	});

	it('reads an upsert with no settled flag as settled, and refuses a pending one with no time', async () => {
		const { settled: _s, settles_at: _a, ...old } = upsert(171, 'Gold Rusher', 'coaster', 'defunct', USA, 2);
		await batch({ upsert: [old] });
		expect((await one(171, 'settled')).data.status).toBe('defunct');
		const res = await batch({ upsert: [{ ...upsert(171, 'X', 'coaster', null, null, 3), settled: false }] });
		expect(res.status).toBe(400);
	});
});
