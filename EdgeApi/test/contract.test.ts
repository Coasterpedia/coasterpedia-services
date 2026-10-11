import { env, exports } from 'cloudflare:workers';
import { beforeAll, expect, it } from 'vitest';
import { sha256Hex } from '../src/auth';
import { resetData } from './reset';
// Written by the Projector's GoldenBatchTests: the real wire shape, not a hand copy.
import batch from './fixtures/projector-batch.json';

const BASE = 'https://api.coasterpedia.net';
const KEY = 'cpk_contract';

const get = (path: string) =>
	exports.default.fetch(new Request(BASE + path, { redirect: 'manual', headers: { authorization: `Bearer ${KEY}` } }));

beforeAll(async () => {
	await resetData();
	await env.KEYS.put(`key:${await sha256Hex(KEY)}`, JSON.stringify({ account: 'contract', tier: 'beta', status: 'active' }));
	// The batch deletes 170 as merged into 171, so 170 must have existed for the 301 to mean anything.
	const res = await exports.default.fetch(new Request(`${BASE}/internal/ingest`, {
		method: 'POST',
		headers: { 'content-type': 'application/json', 'x-dev-ingest-token': 'test-ingest-token' },
		body: JSON.stringify(batch),
	}));
	expect(await res.json()).toEqual({ upserted: 7, deleted: 1, promoted: 1, discarded: 1 });
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

it('serves the record as sent, with its settlement', async () => {
	const body = await (await get('/v1/attractions/9101?view=latest')).json<any>();
	expect(body.data).toEqual({
		...batch.upsert.find((u) => u.id === 9101)!.record,
		settlement: { settled_revision: null, pending_revision: 404, settles_at: '2026-10-18T12:00:00Z' },
	});
});

it('keeps the pending upsert out of the settled view', async () => {
	const ids = async (view: string) => (await (await get(`/v1/attractions?view=${view}&type=coaster`)).json<any>()).data.map((a: any) => a.id);
	expect(await ids('latest')).toContain(9101);
	expect(await ids('settled')).not.toContain(9101);
	expect((await get('/v1/attractions/171?view=settled')).status).toBe(200);
});

it('redirects the merged page', async () => {
	const res = await get('/v1/attractions/170?view=latest');
	expect(res.status).toBe(301);
	expect(res.headers.get('location')).toBe(`${BASE}/v1/attractions/171?view=latest`);
});

it('ranks by a headline stat, leaving out what does not record it, and shows the figure', async () => {
	const body = await (await get('/v1/attractions?view=latest&sort=-height')).json<any>();
	// 9101 and SkyRide record no height, so they cannot be ranked.
	expect(body.data.map((a: any) => [a.id, a.stats.height.value])).toEqual([
		[19643, 73.8], [169, 38.1], [9102, 36.6], [165, 32], [171, 21.3],
	]);
	expect(body.data[0].stats.height.entered).toEqual({ value: 242, unit: 'ft' });

	const slides = await (await get('/v1/attractions?view=latest&sort=length&type=water_slide')).json<any>();
	expect(slides.data.map((a: any) => a.id)).toEqual([9102]);
});

it('pages a stat sort, and refuses a cursor from another sort', async () => {
	const first = await (await get('/v1/attractions?view=latest&sort=speed&limit=2')).json<any>();
	expect(first.data.map((a: any) => a.stats.speed.value)).toEqual([56.3, 56.3]);
	const second = await (await get(`/v1/attractions?view=latest&sort=speed&limit=2&cursor=${first.meta.next_cursor}`)).json<any>();
	expect(second.data.map((a: any) => a.stats.speed.value)).toEqual([80.5, 99.8]);
	expect(second.meta.next_cursor).toBeNull();

	const wrong = await get(`/v1/attractions?view=latest&sort=height&cursor=${first.meta.next_cursor}`);
	expect(wrong.status).toBe(400);
});

it('carries the full stats on the record', async () => {
	const body = await (await get('/v1/attractions/169?view=latest')).json<any>();
	expect(body.data.stats).toMatchObject({ tracks: 2, inversions: { track: 0 }, duration: { value: 150, unit: 's' } });
});

it('serves the vocabularies the batch carried, in the infobox order', async () => {
	const body = await (await exports.default.fetch(new Request(`${BASE}/v1/vocabularies`))).json<any>();
	expect(body.data).toEqual(batch.vocabularies);
	expect(body.data.status.map((s: any) => s.value)).toContain('standing_but_not_operating');
	expect(body.meta.data_as_of).toBe(batch.as_of);
});

it('logs the batch for /v1/changes, with the merge as a tombstone', async () => {
	const body = await (await get('/v1/changes?view=latest&since=2026-01-01T00:00:00Z')).json<any>();
	expect(body.data).toHaveLength(8);
	expect(body.data.at(-1)).toMatchObject({ kind: 'attraction', id: 170, op: 'delete', merged_into: 171 });
	// 9101 waits to settle, so the settled feed leaves it out.
	const settled = await (await get('/v1/changes?since=2026-01-01T00:00:00Z')).json<any>();
	expect(settled.data.map((c: any) => c.id)).not.toContain(9101);
});
