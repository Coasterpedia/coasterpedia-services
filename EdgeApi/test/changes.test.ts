import { env, exports } from 'cloudflare:workers';
import { beforeEach, describe, expect, it } from 'vitest';
import { sha256Hex } from '../src/auth';
import { pruneChanges } from '../src/changes';
import { encodeOpaque } from '../src/http';
import { resetData } from './reset';

const BASE = 'https://api.coasterpedia.net';
const BETA = 'cpk_changesbeta';
const FREE = 'cpk_changesfree';
const ODD = 'cpk_changesodd';
const START = '2026-01-01T00:00:00Z';
const DAY = 86400_000;

function upsert(id: number, revision: number, settlesAt: string | null = null) {
	const summary = { id, type: 'ride', name: `Ride ${id}`, manufacturers: [], updated_at: '2026-10-11T12:00:00Z' };
	return {
		kind: 'attraction', id, revision, updated_at: '2026-10-11T12:00:00Z', settled: settlesAt == null, settles_at: settlesAt,
		sort_name: `ride ${id}`, filters: { type: 'ride', status: null, country: null }, summary, record: { ...summary, revision },
	};
}

async function batch(parts: Record<string, unknown>) {
	return exports.default.fetch(new Request(`${BASE}/internal/ingest`, {
		method: 'POST',
		headers: { 'content-type': 'application/json', 'x-dev-ingest-token': 'test-ingest-token' },
		body: JSON.stringify({ as_of: '2026-10-11T12:00:00Z', upsert: [], delete: [], ...parts }),
	}));
}

function get(path: string, key: string | null = BETA) {
	return exports.default.fetch(new Request(BASE + path, { headers: key ? { authorization: `Bearer ${key}` } : {} }));
}

async function feed(query: string, key = BETA) {
	const res = await get(`/v1/changes?${query}`, key);
	expect(res.status).toBe(200);
	return res.json<{ data: any[]; meta: any }>();
}

/** Every line from a starting point, following next_cursor while has_more. */
async function follow(query: string, key = BETA) {
	const seen = [];
	let page = await feed(query, key);
	seen.push(...page.data);
	while (page.meta.has_more) {
		page = await feed(`${query.replace(/since=[^&]*/, '')}&since=${page.meta.next_cursor}`, key);
		seen.push(...page.data);
	}
	return { seen, cursor: page.meta.next_cursor as string };
}

/** Log lines at chosen times, which ingest cannot backdate. */
async function lines(ats: string[]) {
	await env.DATA.batch(ats.map((at, i) => env.DATA.prepare(
		`INSERT INTO changes (kind, id, op, revision, at) VALUES ('attraction', ?1, 'upsert', 1, ?2)`,
	).bind(1000 + i, at)));
}

const daysAgo = (n: number) => new Date(Date.now() - n * DAY).toISOString().replace(/\.\d{3}Z$/, 'Z');

beforeEach(async () => {
	await resetData();
	for (const [key, tier] of [[BETA, 'beta'], [FREE, 'free'], [ODD, 'gold']]) {
		await env.KEYS.put(`key:${await sha256Hex(key)}`, JSON.stringify({ account: tier, tier, status: 'active' }));
	}
});

describe('GET /v1/changes', () => {
	it('needs a key and a starting point', async () => {
		expect((await get('/v1/changes?since=2026-01-01T00:00:00Z', null)).status).toBe(401);
		const res = await get('/v1/changes');
		expect(res.status).toBe(400);
		expect((await res.json<any>()).type).toMatch(/parameter-required$/);
	});

	it('lists every change since a time, in order, with the contract envelope', async () => {
		await batch({ upsert: [upsert(1, 10), upsert(2, 20)] });
		await batch({ delete: [{ kind: 'attraction', id: 2, merged_into: 1 }] });
		const body = await feed(`since=${START}`);
		expect(body.data).toEqual([
			{ kind: 'attraction', id: 1, op: 'upsert', revision: 10, at: expect.any(String) },
			{ kind: 'attraction', id: 2, op: 'upsert', revision: 20, at: expect.any(String) },
			{ kind: 'attraction', id: 2, op: 'delete', merged_into: 1, at: expect.any(String) },
		]);
		expect(body.meta).toMatchObject({ view: 'settled', has_more: false, delay: 'PT0H', data_as_of: '2026-10-11T12:00:00Z' });
		expect(body.meta.next_cursor).toEqual(expect.any(String));
	});

	it('a cursor picks up exactly where the last page ended', async () => {
		await batch({ upsert: [upsert(1, 10)] });
		const first = await feed(`since=${START}`);
		const empty = await feed(`since=${first.meta.next_cursor}`);
		expect(empty.data).toEqual([]);
		expect(empty.meta.next_cursor).toBe(first.meta.next_cursor);

		await batch({ upsert: [upsert(1, 11)] });
		expect((await feed(`since=${first.meta.next_cursor}`)).data.map((c) => c.revision)).toEqual([11]);
	});

	it('a time after everything starts at the head', async () => {
		await batch({ upsert: [upsert(1, 10)] });
		const later = await feed('since=2099-01-01T00:00:00Z');
		expect(later.data).toEqual([]);
		await batch({ upsert: [upsert(1, 11)] });
		expect((await feed(`since=${later.meta.next_cursor}`)).data.map((c) => c.revision)).toEqual([11]);
	});

	it('pages by limit', async () => {
		await batch({ upsert: [1, 2, 3, 4, 5].map((id) => upsert(id, id)) });
		const page = await feed(`since=${START}&limit=2`);
		expect(page.data.map((c) => c.id)).toEqual([1, 2]);
		expect(page.meta.has_more).toBe(true);
		const { seen } = await follow(`since=${START}&limit=2`);
		expect(seen.map((c) => c.id)).toEqual([1, 2, 3, 4, 5]);
	});

	it('settled never hears of a vandal edit and its revert; latest sees both', async () => {
		const week = '2026-10-18T12:00:00Z';
		await batch({ upsert: [upsert(1, 10)] });
		await batch({ upsert: [upsert(1, 11, week)] }); // vandal
		await batch({ discard: [{ kind: 'attraction', id: 1 }] }); // reverted
		await batch({ upsert: [upsert(2, 20, week)] }); // untrusted, stands
		await batch({ promote: [{ kind: 'attraction', id: 2 }] }); // a week later

		const settled = await feed(`since=${START}`);
		expect(settled.data.map((c) => [c.id, c.revision])).toEqual([[1, 10], [2, 20]]);
		const latest = await feed(`since=${START}&view=latest`);
		expect(latest.data.map((c) => [c.id, c.revision])).toEqual([[1, 10], [1, 11], [1, 10], [2, 20]]);
	});

	it('filters by kind and refuses a kind that is not published', async () => {
		await batch({ upsert: [upsert(1, 10)] });
		expect((await feed(`since=${START}&kinds=attraction`)).data).toHaveLength(1);
		expect((await feed(`since=${START}&kinds=park`)).data).toHaveLength(0);
		const res = await get(`/v1/changes?since=${START}&kinds=park_company`);
		expect(res.status).toBe(400);
		expect((await res.json<any>()).type).toMatch(/parameter-unavailable$/);
	});

	it('refuses a cursor it did not make, and an attractions cursor', async () => {
		expect((await get('/v1/changes?since=nonsense')).status).toBe(400);
		expect((await get(`/v1/changes?since=${encodeOpaque(['name', 'a', 1])}`)).status).toBe(400);
		expect((await get(`/v1/changes?since=${START}&cursor=x`)).status).toBe(400);
	});

	it('a settled feed scans a bounded stretch, and an empty page with has_more is not the end', async () => {
		// 1,500 latest-only lines, then one settled one.
		const rows = Array.from({ length: 1500 }, (_, i) => `('attraction', ${i + 1}, 'upsert', 2, '2026-10-11T12:00:00Z', 1, 0)`);
		await env.DATA.prepare(`INSERT INTO changes (kind, id, op, revision, at, in_latest, in_settled) VALUES ${rows.join(',')}`).run();
		await batch({ upsert: [upsert(9, 9)] });

		const first = await feed(`since=${START}`);
		expect(first.data).toEqual([]);
		expect(first.meta.has_more).toBe(true);
		const { seen } = await follow(`since=${START}`);
		expect(seen.map((c) => c.id)).toEqual([9]);
	});
});

describe('the free tier is delayed', () => {
	it('sees nothing newer than 24 hours, and its cursor never steps over it', async () => {
		await lines([daysAgo(3), daysAgo(2), daysAgo(0.5)]);
		const free = await feed(`since=${START}`, FREE);
		expect(free.data.map((c) => c.id)).toEqual([1000, 1001]);
		expect(free.meta).toMatchObject({ delay: 'PT24H', has_more: false });
		expect(free.meta.data_as_of < daysAgo(0.99)).toBe(true);

		// The hidden line is still ahead of the cursor: a key that is not delayed reads it from there.
		expect((await feed(`since=${free.meta.next_cursor}`)).data.map((c) => c.id)).toEqual([1002]);
		expect((await feed(`since=${START}`)).data).toHaveLength(3);
	});

	it('an unknown tier gets the free delay', async () => {
		await lines([daysAgo(0.5)]);
		const body = await feed(`since=${START}`, ODD);
		expect(body.data).toEqual([]);
		expect(body.meta.delay).toBe('PT24H');
	});
});

describe('the window', () => {
	it('is published in meta', async () => {
		expect(await (await get('/v1/meta', null)).json<any>()).toMatchObject({ changes_window: 'P90D' });
	});

	it('the prune drops lines older than 90 days and expires cursors that needed them', async () => {
		await lines([daysAgo(100), daysAgo(95), daysAgo(91), daysAgo(30), daysAgo(1)]);
		const old = await feed(`since=${START}&limit=1`); // cursor after the first line
		const atEdge = await feed(`since=${START}&limit=3`); // cursor after the last line pruned

		expect(await pruneChanges(env)).toBe(3);
		const { results } = await env.DATA.prepare('SELECT id FROM changes ORDER BY seq').all();
		expect(results.map((r) => r.id)).toEqual([1003, 1004]);

		const gone = await get(`/v1/changes?since=${old.meta.next_cursor}`);
		expect(gone.status).toBe(410);
		expect((await gone.json<any>()).detail).toContain('/v1/attractions, /v1/parks');
		expect((await get(`/v1/changes?since=${daysAgo(92)}`)).status).toBe(410);

		expect((await feed(`since=${atEdge.meta.next_cursor}`)).data.map((c) => c.id)).toEqual([1003, 1004]);
		expect((await feed(`since=${daysAgo(89)}`)).data.map((c) => c.id)).toEqual([1003, 1004]);
	});

	it('never empties the log, so numbering never restarts under a cursor', async () => {
		await lines([daysAgo(200), daysAgo(150), daysAgo(120)]);
		const head = await feed(`since=${START}`);

		expect(await pruneChanges(env)).toBe(2);
		expect(await pruneChanges(env)).toBe(0);
		const { results } = await env.DATA.prepare('SELECT seq FROM changes').all<{ seq: number }>();
		expect(results).toHaveLength(1);

		await batch({ upsert: [upsert(1, 10)] });
		const { results: after } = await env.DATA.prepare('SELECT MAX(seq) AS seq FROM changes').all<{ seq: number }>();
		expect(after[0].seq).toBe(results[0].seq + 1);
		expect((await feed(`since=${head.meta.next_cursor}`)).data.map((c) => c.revision)).toEqual([10]);
	});

	it('refuses a cursor from a rebuilt log, or from beyond a restored one', async () => {
		await batch({ upsert: [upsert(1, 10)] });
		expect((await get(`/v1/changes?since=${encodeOpaque(['changes', 'another', 1])}`)).status).toBe(410);

		const { next_cursor } = (await feed(`since=${START}`)).meta;
		await env.DATA.prepare('DELETE FROM changes').run(); // a log that went back in time
		expect((await get(`/v1/changes?since=${next_cursor}`)).status).toBe(410);
	});
});

describe('GET /v1/vocabularies', () => {
	const STATUS = [
		{ value: 'operating', label: 'Operating', applies_to: ['coaster', 'ride', 'water_slide'] },
		{ value: 'defunct', label: 'Defunct', applies_to: ['coaster', 'ride', 'water_slide'] },
	];

	it('is open, and says so plainly before the first push', async () => {
		const res = await get('/v1/vocabularies', null);
		expect(res.status).toBe(503);
		expect(res.headers.get('content-type')).toContain('application/problem+json');
	});

	it('serves what the Projector last sent, with its time', async () => {
		await batch({ vocabularies: { status: STATUS } });
		const body = await (await get('/v1/vocabularies', null)).json<any>();
		expect(body.data.status).toEqual(STATUS);
		expect(body.meta).toMatchObject({ data_as_of: '2026-10-11T12:00:00Z', licence: { name: 'CC BY-SA 4.0' } });
	});

	it('never goes back to an older list from a delayed batch', async () => {
		await batch({ as_of: '2026-10-12T00:00:00Z', vocabularies: { status: STATUS } });
		await batch({ as_of: '2026-10-11T00:00:00Z', vocabularies: { status: STATUS.slice(0, 1) } });
		expect((await (await get('/v1/vocabularies', null)).json<any>()).data.status).toHaveLength(2);
	});

	it('refuses a malformed list and writes nothing', async () => {
		expect((await batch({ vocabularies: { status: [{ value: 'operating' }] } })).status).toBe(400);
		expect((await batch({ vocabularies: [] })).status).toBe(400);
		expect((await get('/v1/vocabularies', null)).status).toBe(503);
	});

	it('takes no parameters', async () => {
		expect((await get('/v1/vocabularies?field=status', null)).status).toBe(400);
	});
});
