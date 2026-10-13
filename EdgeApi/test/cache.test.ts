import { createExecutionContext, waitOnExecutionContext } from 'cloudflare:test';
import { env } from 'cloudflare:workers';
import { beforeEach, describe, expect, it } from 'vitest';
import { sha256Hex } from '../src/auth';
import { cacheKey } from '../src/cache';
import worker from '../src/index';
import { resetData } from './reset';

// DR-010 §7.3 step 5: the edge cache, and the RateLimit headers.

const BETA = 'cpk_cachebeta';
const FREE = 'cpk_cachefree';

function upsert(id: number, name: string, revision = 1) {
	const summary = { id, type: 'ride', name, manufacturers: [], updated_at: '2026-10-11T12:00:00Z' };
	return {
		kind: 'attraction', id, revision, updated_at: '2026-10-11T12:00:00Z', settled: true, settles_at: null,
		sort_name: name.toLowerCase(), filters: { type: 'ride', status: null, country: null }, summary, record: { ...summary, revision },
	};
}

// The cache outlives resetData, so each test gets its own host and so its own keys.
let host = 0;
let base = '';
let points: { blobs?: unknown[]; doubles?: number[] }[] = [];
let limited = false;

/** The test env with the cache on, a usage sink to read back, and a limiter the test can trip. */
function withCache(): Cloudflare.Env {
	return {
		...env,
		CACHE_SECONDS: '30',
		USAGE: { writeDataPoint: (p?: AnalyticsEngineDataPoint) => points.push(p ?? {}) },
		RATE_LIMITER: { limit: async () => ({ success: !limited }) },
	};
}

async function call(path: string, init: { key?: string | null; method?: string; body?: unknown; cache?: boolean } = {}) {
	const { key = BETA, method = 'GET', body, cache = true } = init;
	const headers: Record<string, string> = key ? { authorization: `Bearer ${key}` } : {};
	if (body) Object.assign(headers, { 'content-type': 'application/json', 'x-dev-ingest-token': 'test-ingest-token' });
	const ctx = createExecutionContext();
	const res = await worker.fetch(
		new Request(base + path, { method, headers, redirect: 'manual', body: body ? JSON.stringify(body) : undefined }),
		cache ? withCache() : env,
		ctx,
	);
	await waitOnExecutionContext(ctx);
	return res;
}

const ingest = (upserts: ReturnType<typeof upsert>[]) =>
	call('/internal/ingest', { key: null, method: 'POST', body: { as_of: '2026-10-11T12:00:00Z', upsert: upserts, delete: [] } });

async function names(path: string) {
	const res = await call(path);
	expect(res.status).toBe(200);
	return { res, names: (await res.json<{ data: { name: string }[] }>()).data.map((a) => a.name) };
}

beforeEach(async () => {
	base = `https://t${++host}.api.coasterpedia.net`;
	points = [];
	limited = false;
	await resetData();
	await env.KEYS.put(`key:${await sha256Hex(BETA)}`, JSON.stringify({ account: 'cache-beta', tier: 'beta', status: 'active' }));
	await env.KEYS.put(`key:${await sha256Hex(FREE)}`, JSON.stringify({ account: 'cache-free', tier: 'free', status: 'active' }));
	expect((await ingest([upsert(1, 'Alpha'), upsert(2, 'Bravo')])).status).toBe(200);
});

describe('edge cache', () => {
	it('is off in the other test files', async () => {
		expect(env.CACHE_SECONDS).toBe('0');
		const res = await call('/v1/attractions', { cache: false });
		expect(res.headers.get('cache-control')).toBeNull();
	});

	it('answers a repeat from the cache, up to CACHE_SECONDS behind D1', async () => {
		const first = await names('/v1/attractions');
		expect(first.names).toEqual(['Alpha', 'Bravo']);
		expect(first.res.headers.get('cache-control')).toBe('private, max-age=30');
		expect(first.res.headers.get('age')).toBe('0');

		await ingest([upsert(3, 'Charlie')]);
		const second = await names('/v1/attractions');
		expect(second.names).toEqual(['Alpha', 'Bravo']);
		expect(Number(second.res.headers.get('age'))).toBeLessThanOrEqual(30);

		// Internal bookkeeping never reaches the client.
		expect(second.res.headers.get('cp-rows')).toBeNull();
		expect(second.res.headers.get('cp-stored-at')).toBeNull();
	});

	it('records a hit with the rows it served', async () => {
		await call('/v1/attractions');
		await call('/v1/attractions');
		expect(points.map((p) => [p.blobs?.[2], p.blobs?.[5], p.doubles?.[0]])).toEqual([
			['/v1/attractions', 'miss', 2],
			['/v1/attractions', 'hit', 2],
		]);
	});

	it('shares an entry across parameter order and the default view', async () => {
		await names('/v1/attractions?limit=5&type=ride');
		await ingest([upsert(3, 'Charlie')]);
		expect((await names('/v1/attractions?type=ride&view=settled&limit=5')).names).toEqual(['Alpha', 'Bravo']);
		// latest is a different answer, so a different entry.
		expect((await names('/v1/attractions?type=ride&view=latest&limit=5')).names).toEqual(['Alpha', 'Bravo', 'Charlie']);
	});

	it('keeps one record by id', async () => {
		expect((await call('/v1/attractions/1')).status).toBe(200);
		await ingest([upsert(1, 'Alpha renamed', 2)]);
		const res = await call('/v1/attractions/1');
		expect((await res.json<{ data: { name: string } }>()).data.name).toBe('Alpha');
	});

	it('keeps no errors: a 404 is gone once the record arrives', async () => {
		expect((await call('/v1/attractions/3')).status).toBe(404);
		await ingest([upsert(3, 'Charlie')]);
		expect((await call('/v1/attractions/3')).status).toBe(200);
	});

	it('keys /v1/changes by the tier delay', async () => {
		const since = '/v1/changes?since=2026-01-01T00:00:00Z&view=latest';
		const beta = await (await call(since)).json<any>();
		const free = await (await call(since, { key: FREE })).json<any>();
		// Both tiers are real time (DR-010 §9), so they share an entry; a delayed tier would key its own.
		expect(beta.meta.delay).toBe('PT0H');
		expect(free.meta.delay).toBe('PT0H');
		expect(free.data).toEqual(beta.data);
		expect(free.data).toHaveLength(2);
	});

	it('serves the open endpoints publicly', async () => {
		const res = await call('/v1/meta', { key: null });
		expect(res.status).toBe(200);
		expect(res.headers.get('cache-control')).toBe('public, max-age=30');
		expect(res.headers.get('ratelimit-policy')).toBeNull();
	});
});

describe('cacheKey', () => {
	const key = (u: string, view = true, extra?: Record<string, string>) =>
		cacheKey(new URL(u), new URL(u).pathname.replace(/\/+$/, ''), view, extra)?.url ?? null;

	it('ignores parameter order and the trailing slash, and writes in the default view', () => {
		expect(key('https://a.test/v1/attractions/?type=ride&limit=5')).toBe('https://a.test/v1/attractions?limit=5&type=ride&view=settled');
	});

	it('keeps a repeated parameter in order, as the handlers read the first', () => {
		expect(key('https://a.test/v1/attractions?view=latest&view=settled')).toBe('https://a.test/v1/attractions?view=latest&view=settled');
	});

	it('adds no view to a route without one', () => {
		expect(key('https://a.test/v1/vocabularies', false)).toBe('https://a.test/v1/vocabularies');
	});

	it('refuses to cache a request that names an extra itself', () => {
		expect(key('https://a.test/v1/changes?since=x', true, { delay: 'PT0H' })).toBe('https://a.test/v1/changes?delay=PT0H&since=x&view=settled');
		expect(key('https://a.test/v1/changes?since=x&delay=PT0H', true, { delay: 'PT24H' })).toBeNull();
	});
});

describe('rate limit headers', () => {
	it('every keyed answer states the policy, errors included', async () => {
		for (const path of ['/v1/attractions', '/v1/attractions?sort=park', '/v1/attractions/999', '/v1/nowhere']) {
			const res = await call(path);
			expect(res.headers.get('ratelimit-policy'), path).toBe('"burst";q=60;w=60');
			expect(res.headers.get('ratelimit'), path).toBeNull();
		}
	});

	it('records a refused parameter, which used to skip the usage point', async () => {
		expect((await call('/v1/attractions?sort=park')).status).toBe(400);
		expect(points.map((p) => [p.blobs?.[2], p.blobs?.[3]])).toEqual([['/v1/attractions', '400']]);
	});

	it('a 429 says nothing is left and when to come back', async () => {
		limited = true;
		const res = await call('/v1/attractions');
		expect(res.status).toBe(429);
		expect(res.headers.get('ratelimit-policy')).toBe('"burst";q=60;w=60');
		expect(res.headers.get('ratelimit')).toBe('"burst";r=0;t=60');
		expect(res.headers.get('retry-after')).toBe('60');
	});

	it('limits a cached answer too', async () => {
		expect((await call('/v1/attractions')).status).toBe(200);
		limited = true;
		expect((await call('/v1/attractions')).status).toBe(429);
	});

	it('no key, no limit, no policy', async () => {
		const res = await call('/v1/attractions', { key: null });
		expect(res.status).toBe(401);
		expect(res.headers.get('ratelimit-policy')).toBeNull();
	});
});
