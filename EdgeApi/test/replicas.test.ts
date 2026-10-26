import { createExecutionContext, waitOnExecutionContext } from 'cloudflare:test';
import { env } from 'cloudflare:workers';
import { beforeEach, describe, expect, it } from 'vitest';
import { sha256Hex } from '../src/auth';
import worker from '../src/index';
import { resetData } from './reset';

// DR-010 §12.27: reads go through a D1 session, so read replicas can answer
// them; the change feed and ingest stay on the primary. Locally a session is
// the primary too, so this pins where each request reads, not what it gets.

const KEY = 'cpk_replicatest';

/** Statements prepared or batched on the binding (the primary) and on sessions, and each session's constraint. */
let used: { primary: number; session: number; constraints: string[] };

function trackedEnv(): Cloudflare.Env {
	const db = env.DATA;
	const data = {
		prepare: (sql: string) => {
			used.primary++;
			return db.prepare(sql);
		},
		batch: (statements: D1PreparedStatement[]) => {
			used.primary++;
			return db.batch(statements);
		},
		withSession: (constraint?: D1SessionConstraint | D1SessionBookmark) => {
			used.constraints.push(String(constraint));
			const session = db.withSession(constraint);
			return {
				prepare: (sql: string) => {
					used.session++;
					return session.prepare(sql);
				},
				batch: (statements: D1PreparedStatement[]) => {
					used.session++;
					return session.batch(statements);
				},
				getBookmark: () => session.getBookmark(),
			};
		},
	};
	return { ...env, DATA: data as unknown as D1Database };
}

async function call(path: string, init: { key?: string | null; method?: string; body?: unknown } = {}) {
	const { key = KEY, method = 'GET', body } = init;
	const headers: Record<string, string> = key ? { authorization: `Bearer ${key}` } : {};
	if (body) Object.assign(headers, { 'content-type': 'application/json', 'x-dev-ingest-token': 'test-ingest-token' });
	used = { primary: 0, session: 0, constraints: [] };
	const ctx = createExecutionContext();
	const res = await worker.fetch(
		new Request(`https://api.coasterpedia.net${path}`, { method, headers, redirect: 'manual', body: body ? JSON.stringify(body) : undefined }),
		trackedEnv(),
		ctx,
	);
	await waitOnExecutionContext(ctx);
	return res;
}

const summary = { id: 1, type: 'ride', name: 'Alpha', manufacturers: [], updated_at: '2026-10-11T12:00:00Z' };
const batch = {
	as_of: '2026-10-11T12:00:00Z',
	upsert: [{
		kind: 'attraction', id: 1, revision: 1, updated_at: '2026-10-11T12:00:00Z', settled: true, settles_at: null,
		sort_name: 'alpha', filters: { type: 'ride', status: null, country: null }, summary, record: { ...summary, revision: 1 },
	}],
	delete: [],
};

beforeEach(async () => {
	await resetData();
	await env.KEYS.put(`key:${await sha256Hex(KEY)}`, JSON.stringify({ account: 'replica-test', tier: 'beta', status: 'active' }));
});

describe('where each request reads', () => {
	it('ingest writes to the primary', async () => {
		expect((await call('/internal/ingest', { key: null, method: 'POST', body: batch })).status).toBe(200);
		expect(used.primary).toBeGreaterThan(0);
		expect(used.session).toBe(0);
	});

	describe('with data', () => {
		beforeEach(async () => {
			expect((await call('/internal/ingest', { key: null, method: 'POST', body: batch })).status).toBe(200);
		});

		it.each([
			['/v1/meta', null],
			['/v1/attractions', KEY],
			['/v1/attractions/1', KEY],
			['/v1/attractions?ids=1', KEY],
			['/v1/attractions/facets', KEY],
			['/v1/parks', KEY],
			['/v1/search?q=alpha', KEY],
		])('%s reads through one unconstrained session', async (path, key) => {
			const res = await call(path, { key });
			expect(res.status).toBe(200);
			expect(used.session).toBeGreaterThan(0);
			expect(used.primary).toBe(0);
			expect(used.constraints).toEqual(['first-unconstrained']);
		});

		it('/v1/changes reads the primary, so a cursor is never ahead of the log it reads', async () => {
			const res = await call('/v1/changes?since=2026-10-01T00:00:00Z');
			expect(res.status).toBe(200);
			expect(used.primary).toBeGreaterThan(0);
			expect(used.session).toBe(0);
		});
	});
});
