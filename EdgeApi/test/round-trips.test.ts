import { env } from 'cloudflare:workers';
import { beforeAll, describe, expect, it } from 'vitest';
import { getAttraction, getAttractions, listAttractions, meta } from '../src/attractions';
import { listParks } from '../src/parks';
import { search } from '../src/search';
import type { Env } from '../src/env';
import { resetData } from './reset';

// Every D1 call is a round trip to the database's region, about 60ms even from
// London (2026-10-26), and a cache miss pays each one in turn. These pin the
// common answers to one trip, data_as_of included.

/** env with a DATA that counts the calls reaching D1: a batch is one, however many statements it holds. */
function counting(): { env: Env; trips: () => number } {
	let trips = 0;
	const db = env.DATA;
	const watch = (statement: D1PreparedStatement): D1PreparedStatement => {
		const bind = statement.bind.bind(statement);
		statement.bind = (...values: unknown[]) => watch(bind(...values));
		for (const name of ['all', 'first', 'run', 'raw'] as const) {
			const call = (statement[name] as (...args: unknown[]) => Promise<unknown>).bind(statement);
			(statement as any)[name] = (...args: unknown[]) => {
				trips++;
				return call(...args);
			};
		}
		return statement;
	};
	const data = Object.create(db) as D1Database;
	data.prepare = (sql: string) => watch(db.prepare(sql));
	data.batch = (<T>(statements: D1PreparedStatement[]) => {
		trips++;
		return db.batch<T>(statements);
	}) as D1Database['batch'];
	return { env: { ...env, DATA: data } as Env, trips: () => trips };
}

const url = (path: string) => new URL(path, 'https://api.coasterpedia.net');

beforeAll(async () => {
	await resetData();
	await env.DATA.batch([
		env.DATA.prepare(`INSERT INTO attractions (id, in_latest, in_settled, type, status, country, sort_name, revision, updated_at, summary, record)
			VALUES (1, 1, 1, 'coaster', 'operating', 'GB', 'colossus', 1, '2026-10-11T00:00:00Z', '{"id":1}', '{"id":1}')`),
		env.DATA.prepare(`INSERT INTO state (key, value) VALUES ('data_as_of', '2026-10-11T12:00:00Z')
			ON CONFLICT (key) DO UPDATE SET value = excluded.value`),
	]);
});

describe('one round trip to D1', () => {
	it('/v1/meta', async () => {
		const c = counting();
		const body = await (await meta(url('/v1/meta'), c.env)).json<{ data_as_of: string; counts: { attractions: number } }>();
		expect(body).toMatchObject({ data_as_of: '2026-10-11T12:00:00Z', counts: { attractions: 1 } });
		expect(c.trips()).toBe(1);
	});

	it.each([
		['the attraction list', (e: Env) => listAttractions(url('/v1/attractions'), e)],
		['an attraction', (e: Env) => getAttraction(url('/v1/attractions/1'), '1', e)],
		['attractions by ID', (e: Env) => getAttractions(url('/v1/attractions?ids=1'), e)],
		['the park list', (e: Env) => listParks(url('/v1/parks'), e)],
		['search', (e: Env) => search(url('/v1/search?q=colossus'), e)],
	])('%s, with data_as_of', async (_, run) => {
		const c = counting();
		const { response } = await run(c.env);
		expect(response.status).toBe(200);
		expect((await response.json<{ meta: { data_as_of: string } }>()).meta.data_as_of).toBe('2026-10-11T12:00:00Z');
		expect(c.trips()).toBe(1);
	});
});
