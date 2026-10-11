import { env, exports } from 'cloudflare:workers';
import { beforeAll, expect, it } from 'vitest';
import { sha256Hex } from '../src/auth';
// Written by the Projector's GoldenBatchTests: the real wire shape, not a hand copy.
import batch from './fixtures/projector-batch.json';

const BASE = 'https://api.coasterpedia.net';
const KEY = 'cpk_contract';

const get = (path: string) =>
	exports.default.fetch(new Request(BASE + path, { redirect: 'manual', headers: { authorization: `Bearer ${KEY}` } }));

beforeAll(async () => {
	await env.DATA.batch(['attractions', 'merges', 'changes', 'state'].map((t) => env.DATA.prepare(`DELETE FROM ${t}`)));
	await env.KEYS.put(`key:${await sha256Hex(KEY)}`, JSON.stringify({ account: 'contract', tier: 'beta', status: 'active' }));
	// The batch deletes 170 as merged into 171, so 170 must have existed for the 301 to mean anything.
	const res = await exports.default.fetch(new Request(`${BASE}/internal/ingest`, {
		method: 'POST',
		headers: { 'content-type': 'application/json', 'x-dev-ingest-token': 'test-ingest-token' },
		body: JSON.stringify(batch),
	}));
	expect(await res.json()).toEqual({ upserted: 7, deleted: 1 });
});

it('accepts the Projector batch and serves it in name order, accent-blind', async () => {
	const body = await (await get('/v1/attractions?view=latest')).json<any>();
	expect(body.data.map((a: any) => a.name)).toEqual([
		'Batman The Ride', 'Colossus', 'Élan', 'Example Twin', 'Gold Rusher', 'Over the Hill 2: Spooksville', 'SkyRide',
	]);
});

it('filters on the columns the Projector chose', async () => {
	const ids = async (q: string) => (await (await get(`/v1/attractions?view=latest&${q}`)).json<any>()).data.map((a: any) => a.id);
	expect(await ids('country=XK')).toEqual([9102]);
	expect(await ids('type=water_slide')).toEqual([9102]);
	expect(await ids('status=standing_but_not_operating')).toEqual([9101]);
});

it('serves the record as sent', async () => {
	const body = await (await get('/v1/attractions/9101?view=latest')).json<any>();
	expect(body.data).toEqual(batch.upsert.find((u) => u.id === 9101)!.record);
});

it('redirects the merged page', async () => {
	const res = await get('/v1/attractions/170?view=latest');
	expect(res.status).toBe(301);
	expect(res.headers.get('location')).toBe(`${BASE}/v1/attractions/171?view=latest`);
});
