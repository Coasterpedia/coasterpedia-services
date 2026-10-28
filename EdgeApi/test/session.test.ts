import { createExecutionContext, waitOnExecutionContext } from 'cloudflare:test';
import { env } from 'cloudflare:workers';
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { sha256Hex } from '../src/auth';
import worker from '../src/index';
import { mintSession, SESSION_SECONDS, TURNSTILE_ACTION, verifySession } from '../src/session';
import { resetData } from './reset';

// DR-010 §7.4: the Attractions Browser's Turnstile sessions.

const API = 'https://api.coasterpedia.net';
const WIKI = 'https://coasterpedia.net/api';
const KEY = 'cpk_sessiontests';

let points: { blobs?: unknown[] }[] = [];
let limitedKeys: string[] = [];

function testEnv(overrides: Partial<Cloudflare.Env> = {}): Cloudflare.Env {
	return {
		...env,
		USAGE: { writeDataPoint: (p?: AnalyticsEngineDataPoint) => points.push(p ?? {}) },
		RATE_LIMITER: { limit: async ({ key }: { key: string }) => (limitedKeys.push(key), { success: true }) },
		...overrides,
	};
}

async function call(url: string, init: RequestInit = {}, e = testEnv()) {
	const ctx = createExecutionContext();
	const res = await worker.fetch(new Request(url, { redirect: 'manual', ...init }), e, ctx);
	await waitOnExecutionContext(ctx);
	return res;
}

const bearer = (token: string) => ({ headers: { authorization: `Bearer ${token}` } });

/** What siteverify answers next; undefined makes it fail at the network. */
let verdict: Record<string, unknown> | undefined;
let verified: FormData[] = [];

function stubSiteverify() {
	const real = globalThis.fetch;
	vi.spyOn(globalThis, 'fetch').mockImplementation(async (input, init) => {
		const url = input instanceof Request ? input.url : String(input);
		if (!url.startsWith('https://challenges.cloudflare.com/')) return real(input, init);
		verified.push(init?.body as FormData);
		if (!verdict) throw new TypeError('network down');
		return Response.json(verdict);
	});
}

const PASS = { success: true, hostname: 'coasterpedia.net', action: TURNSTILE_ACTION };

const open = (body: unknown = { turnstile: 'XXXX.DUMMY.TOKEN' }, headers: Record<string, string> = {}, base = API) =>
	call(`${base}/v1/session`, { method: 'POST', headers: { 'content-type': 'application/json', ...headers }, body: JSON.stringify(body) });

async function session(): Promise<string> {
	verdict = PASS;
	const res = await open();
	expect(res.status).toBe(200);
	return (await res.json<{ token: string }>()).token;
}

function upsert(id: number, name: string) {
	const summary = { id, type: 'ride', name, manufacturers: [], updated_at: '2026-10-13T12:00:00Z' };
	return {
		kind: 'attraction', id, revision: 1, updated_at: '2026-10-13T12:00:00Z', settled: true, settles_at: null,
		sort_name: name.toLowerCase(), filters: { type: 'ride', status: 'operating', country: 'GB' }, summary, record: { ...summary, revision: 1 },
	};
}

beforeAll(async () => {
	await resetData();
	await env.KEYS.put(`key:${await sha256Hex(KEY)}`, JSON.stringify({ account: 'sessions', tier: 'beta', status: 'active' }));
	const res = await call(`${API}/internal/ingest`, {
		method: 'POST',
		headers: { 'content-type': 'application/json', 'x-dev-ingest-token': 'test-ingest-token' },
		body: JSON.stringify({ as_of: '2026-10-13T12:00:00Z', upsert: Array.from({ length: 30 }, (_, i) => upsert(i + 1, `Ride ${String(i + 1).padStart(2, '0')}`)), delete: [] }),
	});
	expect(res.status).toBe(200);
});

beforeEach(() => {
	points = [];
	limitedKeys = [];
	verified = [];
	verdict = PASS;
	stubSiteverify();
});

afterEach(() => vi.restoreAllMocks());

describe('POST /v1/session', () => {
	it('swaps a passing Turnstile token for a 30-minute session, and checks it with the secret and the IP', async () => {
		const before = Date.now();
		const res = await open(undefined, { 'cf-connecting-ip': '203.0.113.9' });
		expect(res.status).toBe(200);
		expect(res.headers.get('cache-control')).toBe('no-store');
		const body = await res.json<{ token: string; expires_at: string }>();
		expect(body.token).toMatch(/^cps_[A-Za-z0-9_-]{22}\.\d{10}\.[A-Za-z0-9_-]{43}$/);
		const expires = Date.parse(body.expires_at);
		expect(expires - before).toBeGreaterThan((SESSION_SECONDS - 5) * 1000);
		expect(expires - before).toBeLessThanOrEqual(SESSION_SECONDS * 1000);

		expect(verified).toHaveLength(1);
		expect(verified[0].get('secret')).toBe('test-turnstile-secret');
		expect(verified[0].get('response')).toBe('XXXX.DUMMY.TOKEN');
		expect(verified[0].get('remoteip')).toBe('203.0.113.9');
		expect(points.map((p) => [p.blobs?.[2], p.blobs?.[3], p.blobs?.[4]])).toEqual([['/v1/session', '200', 'browser']]);
	});

	it('mints a new session each time', async () => {
		expect(await session()).not.toBe(await session());
	});

	it.each([
		['Turnstile failed', { success: false, 'error-codes': ['timeout-or-duplicate'] }],
		['another host', { ...PASS, hostname: 'evil.example' }],
		['another widget', { ...PASS, action: 'createaccount' }],
		['no action', { success: true, hostname: 'coasterpedia.net' }],
	])('refuses a token when %s', async (_, answer) => {
		verdict = answer;
		const res = await open();
		expect(res.status).toBe(403);
		expect((await res.json<any>()).type).toMatch(/turnstile-failed$/);
	});

	it.each([
		['no body', undefined],
		['no token', {}],
		['an empty token', { turnstile: '' }],
		['a token that is not text', { turnstile: 42 }],
		['a token too long to be one', { turnstile: 'x'.repeat(2049) }],
	])('needs a token: %s', async (_, body) => {
		const res = await call(`${API}/v1/session`, { method: 'POST', body: body === undefined ? undefined : JSON.stringify(body) });
		expect(res.status).toBe(400);
		expect(verified).toHaveLength(0);
	});

	it('is a 503 when siteverify cannot be reached, never a session', async () => {
		verdict = undefined;
		const res = await open();
		expect(res.status).toBe(503);
		expect(res.headers.get('retry-after')).toBe('5');
	});

	it('is a 503 until the secrets are set', async () => {
		for (const missing of ['TURNSTILE_SECRET', 'SESSION_SECRET', 'TURNSTILE_HOSTNAME'] as const) {
			const res = await call(`${API}/v1/session`, { method: 'POST', body: JSON.stringify({ turnstile: 'x' }) }, testEnv({ [missing]: undefined }));
			expect(res.status, missing).toBe(503);
		}
		expect(verified).toHaveLength(0);
	});

	it('takes POST only', async () => {
		expect((await call(`${API}/v1/session`)).status).toBe(405);
	});
});

describe('a session', () => {
	it('reads the attractions list, its facets and the parks list, latest, 25 at a time', async () => {
		const token = await session();
		const list = await call(`${API}/v1/attractions?view=latest`, bearer(token));
		expect(list.status).toBe(200);
		const body = await list.json<any>();
		expect(body.data).toHaveLength(25);
		expect((await call(`${API}/v1/attractions?view=latest&limit=25&cursor=${body.meta.next_cursor}`, bearer(token))).status).toBe(200);
		expect((await call(`${API}/v1/attractions/facets?view=latest&type=ride`, bearer(token))).status).toBe(200);
		expect((await call(`${API}/v1/parks?view=latest&limit=10`, bearer(token))).status).toBe(200);
	});

	it.each([
		['/v1/attractions?limit=26&view=latest', 'session-limit'],
		['/v1/parks?view=latest&limit=100', 'session-limit'],
		['/v1/attractions', 'session-view'],
		['/v1/attractions?view=settled', 'session-view'],
		['/v1/attractions/facets', 'session-view'],
		['/v1/parks', 'session-view'],
	])('refuses %s', async (path, slug) => {
		const res = await call(API + path, bearer(await session()));
		expect(res.status).toBe(400);
		expect((await res.json<any>()).type).toMatch(new RegExp(`${slug}$`));
	});

	it.each([
		'/v1/attractions/1?view=latest', '/v1/parks/1?view=latest', '/v1/parks/1/attractions?view=latest', '/v1/manufacturers?view=latest',
		'/v1/products?view=latest', '/v1/products/1/installations?view=latest', '/v1/park-companies?view=latest',
		'/v1/changes?view=latest', '/v1/search?q=ride&view=latest', '/v1/attractions?ids=1&view=latest', '/v1/parks?ids=1&view=latest',
	])('needs a key for %s', async (path) => {
		const res = await call(API + path, bearer(await session()));
		expect(res.status).toBe(403);
		const problem = await res.json<any>();
		expect(problem.type).toMatch(/session-scope$/);
		expect(problem.detail).toContain('https://developer.coasterpedia.net');
	});

	it('leaves a key as it was', async () => {
		expect((await call(`${API}/v1/attractions?limit=100`, bearer(KEY))).status).toBe(200);
		expect((await call(`${API}/v1/attractions/1`, bearer(KEY))).status).toBe(200);
	});

	it('is limited and recorded on its own, as tier browser', async () => {
		const token = await session();
		points = [];
		await call(`${API}/v1/attractions?view=latest`, bearer(token));
		const id = token.slice(4, 26);
		expect(limitedKeys).toEqual([`session:${id}`]);
		expect(points[0].blobs).toEqual(['browser', `cps_${id.slice(0, 8)}`, '/v1/attractions', '200', 'browser', 'miss']);
	});
});

describe('a session token that is not good', () => {
	const now = Date.now();

	it('is a 401 when it has expired, saying so', async () => {
		const { token } = await mintSession('test-session-secret', now - (SESSION_SECONDS + 1) * 1000);
		const res = await call(`${API}/v1/attractions?view=latest`, bearer(token));
		expect(res.status).toBe(401);
		expect((await res.json<any>()).type).toMatch(/session-expired$/);
		expect(res.headers.get('www-authenticate')).toContain('invalid_token');
	});

	it.each([
		['signed with another secret', async () => (await mintSession('another-secret', now)).token],
		['with its expiry moved', async () => {
			const [head, , sig] = (await mintSession('test-session-secret', now)).token.split('.');
			return `${head}.${Math.floor(now / 1000) + 600}.${sig}`;
		}],
		['with another ID', async () => {
			const t = (await mintSession('test-session-secret', now)).token;
			return `cps_${'A'.repeat(22)}${t.slice(26)}`;
		}],
		['malformed', async () => 'cps_nothing'],
		['empty', async () => 'cps_'],
	])('is a 401 when %s, never anonymous', async (_, make) => {
		const res = await call(`${API}/v1/attractions?view=latest`, bearer(await make()));
		expect(res.status).toBe(401);
		expect((await res.json<any>()).type).toMatch(/session-invalid$/);
	});

	it('is refused when signed to outlive a session', async () => {
		const verified = await verifySession((await mintSession('test-session-secret', now + 3600_000)).token, env, now);
		expect(verified).toBeInstanceOf(Response);
		expect((verified as Response).status).toBe(401);
	});

	it('is refused when the Worker has no secret to check it with', async () => {
		const token = await session();
		const res = await call(`${API}/v1/attractions?view=latest`, bearer(token), testEnv({ SESSION_SECRET: undefined }));
		expect(res.status).toBe(401);
	});
});

describe('the same-origin door, coasterpedia.net/api', () => {
	it('opens a session and serves its answers as the API host does', async () => {
		verdict = PASS;
		const res = await open(undefined, {}, WIKI);
		expect(res.status).toBe(200);
		const token = (await res.json<{ token: string }>()).token;
		const list = await call(`${WIKI}/v1/attractions?view=latest&limit=5`, bearer(token));
		expect(list.status).toBe(200);
		expect((await list.json<any>()).data).toHaveLength(5);
		expect((await call(`${WIKI}/v1/meta`)).status).toBe(200);
	});

	it('shares one cache entry with the API host', async () => {
		const cached = testEnv({ CACHE_SECONDS: '30' });
		// A query no other test asks, as the cache outlives each test.
		const path = '/v1/attractions?view=latest&limit=3&type=ride&country=gb';
		const first = await call(API + path, bearer(KEY), cached);
		expect(first.headers.get('age')).toBe('0');
		points = [];
		const second = await call(WIKI + path, bearer(await session()), cached);
		expect(second.status).toBe(200);
		expect(points.at(-1)?.blobs?.[5]).toBe('hit');
		// Private, as a key's answer is.
		expect(second.headers.get('cache-control')).toBe('private, max-age=30');
	});

	it('reaches /v1 only: ingest stays on the API host, behind Access', async () => {
		const res = await call(`${WIKI}/internal/ingest`, {
			method: 'POST',
			headers: { 'content-type': 'application/json', 'x-dev-ingest-token': 'test-ingest-token' },
			body: JSON.stringify({ as_of: '2026-10-13T12:00:00Z', upsert: [], delete: [] }),
		});
		expect(res.status).toBe(405);
		expect((await call('https://coasterpedia.net/api/anything')).status).toBe(404);
	});
});
