import { env, exports } from 'cloudflare:workers';
import { beforeEach, describe, expect, it } from 'vitest';
import { sha256Hex } from '../src/auth';

const KEY = 'cpk_testkey0123456789';
const BASE = 'https://api.coasterpedia.net';

// Exactly what the Projector's Wire.Upsert emits (Projector.Tests pins the same shape).
function upsert(id: number, name: string, type: string, status: string | null, country: { code: string; name: string } | null, revision = 1) {
	const summary: Record<string, unknown> = { id, type, name, ...(status ? { status } : {}), ...(country ? { country } : {}), manufacturers: [], updated_at: '2026-10-11T12:00:00Z' };
	return {
		kind: 'attraction',
		id,
		revision,
		updated_at: '2026-10-11T12:00:00Z',
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
	await env.DATA.batch(['attractions', 'merges', 'changes', 'state'].map((t) => env.DATA.prepare(`DELETE FROM ${t}`)));
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
	it('makes the beta client ask for latest', async () => {
		expect((await get('/v1/attractions')).status).toBe(400);
		expect((await get('/v1/attractions?view=settled')).status).toBe(400);
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
		const res = await get('/v1/attractions?view=latest&park=123');
		expect(res.status).toBe(400);
		expect((await res.json<any>()).type).toContain('parameter-unavailable');
		expect((await get('/v1/attractions?view=latest&colour=red')).status).toBe(400);
		expect((await get('/v1/attractions?view=latest&sort=height')).status).toBe(400);
		expect((await get('/v1/attractions?view=latest&limit=101')).status).toBe(400);
		expect((await get('/v1/attractions?view=latest&cursor=garbage')).status).toBe(400);
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
		expect(await res.text()).toBe('{"upserted":0,"deleted":2}');
		const merged = await get('/v1/attractions/169?view=latest');
		expect(merged.status).toBe(301);
		expect(merged.headers.get('location')).toBe(`${BASE}/v1/attractions/171?view=latest`);
		expect((await get('/v1/attractions/165?view=latest')).status).toBe(404);
		expect((await get('/v1/attractions/abc?view=latest')).status).toBe(404);
	});
});
