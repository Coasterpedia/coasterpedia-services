// Usage from Analytics Engine (DR-010 §13.10). The API writes one point per
// request: blob1 account, blob2 key prefix, blob3 route, blob4 status. It can
// only be read through the SQL API, so the public pages read a daily rollup
// in D1 and only Your keys asks live.
import type { Env } from './env';

/** The kind of record each route answers with, as "Uses" names it (§13.4). */
const ROUTE_KINDS: Record<string, string> = {
	'/v1/attractions': 'attractions',
	'/v1/attractions/facets': 'attractions',
	'/v1/attractions/{id}': 'attractions',
	'/v1/parks/{id}/attractions': 'attractions',
	'/v1/products/{id}/installations': 'attractions',
	'/v1/parks': 'parks',
	'/v1/parks/{id}': 'parks',
	'/v1/park-companies/{id}/parks': 'parks',
	'/v1/manufacturers': 'manufacturers',
	'/v1/manufacturers/{id}': 'manufacturers',
	'/v1/products': 'products',
	'/v1/products/{id}': 'products',
	'/v1/manufacturers/{id}/products': 'products',
	'/v1/park-companies': 'park companies',
	'/v1/park-companies/{id}': 'park companies',
	'/v1/search': 'search',
	'/v1/changes': 'change feed',
};

const KIND_ORDER = ['attractions', 'parks', 'manufacturers', 'products', 'park companies', 'search', 'change feed'];

/** Kinds in a fixed order, so "Uses" reads the same way on every card. */
export function usesFromRoutes(routes: Iterable<string>): string[] {
	const kinds = new Set<string>();
	for (const route of routes) {
		const kind = ROUTE_KINDS[route];
		if (kind) kinds.add(kind);
	}
	return KIND_ORDER.filter((k) => kinds.has(k));
}

/** "2026-10-16 04:45:00" (Analytics Engine) → "2026-10-16T04:45:00Z". */
export function aeTime(value: string): string {
	return value.includes('T') ? value : `${value.replace(' ', 'T')}Z`;
}

export class UsageUnavailable extends Error {}

async function sql<T>(env: Env, query: string): Promise<T[]> {
	if (!env.ANALYTICS_TOKEN || !env.CF_ACCOUNT_ID) throw new UsageUnavailable('ANALYTICS_TOKEN or CF_ACCOUNT_ID is not set');
	const res = await fetch(`https://api.cloudflare.com/client/v4/accounts/${env.CF_ACCOUNT_ID}/analytics_engine/sql`, {
		method: 'POST',
		headers: { authorization: `Bearer ${env.ANALYTICS_TOKEN}` },
		body: `${query} FORMAT JSON`,
	});
	if (!res.ok) throw new UsageUnavailable(`Analytics Engine ${res.status}: ${await res.text()}`);
	return ((await res.json()) as { data: T[] }).data;
}

/** Accounts are u<wiki user ID>; nothing else is interpolated into SQL. */
function accountLiteral(account: string): string {
	if (!/^u\d+$/.test(account)) throw new Error(`not a portal account: ${account}`);
	return `'${account}'`;
}

export interface LiveUsage {
	today: number;
	/** Key prefix → last use, ISO. */
	lastUsed: Map<string, string>;
}

/** For Your keys: today's requests (UTC) and each key's last use. */
export async function liveUsage(env: Env, account: string, now = new Date()): Promise<LiveUsage> {
	const midnight = `${now.toISOString().slice(0, 10)} 00:00:00`;
	const [today, last] = await Promise.all([
		sql<{ requests: number | string }>(env,
			`SELECT SUM(_sample_interval) AS requests FROM ${env.USAGE_DATASET} WHERE blob1 = ${accountLiteral(account)} AND timestamp >= toDateTime('${midnight}')`),
		sql<{ prefix: string; last: string }>(env,
			`SELECT blob2 AS prefix, MAX(timestamp) AS last FROM ${env.USAGE_DATASET} WHERE blob1 = ${accountLiteral(account)} AND timestamp > NOW() - INTERVAL '90' DAY GROUP BY prefix`),
	]);
	return {
		today: Number(today[0]?.requests ?? 0),
		lastUsed: new Map(last.map((r) => [r.prefix, aeTime(r.last)])),
	};
}

/**
 * Any account the API has seen: u<id> from the portal, a name from the
 * closed beta's hand-made keys, or "browser". Quoted only if it can't break
 * out of the literal.
 */
export function anyAccountLiteral(account: string): string {
	if (!/^[A-Za-z0-9_.-]{1,64}$/.test(account)) throw new Error(`not an account name: ${account}`);
	return `'${account}'`;
}

export interface UsageRow {
	account: string;
	/** "2026-10-26": a UTC day. */
	day: string;
	status: string;
	requests: number;
	/** ISO. */
	last: string;
}

/** For the accounts page: every account's requests by UTC day and status. */
export async function accountsUsage(env: Env, days: number): Promise<UsageRow[]> {
	const rows = await sql<{ account: string; day: string; status: string; requests: number | string; last: string }>(env,
		`SELECT blob1 AS account, toStartOfInterval(timestamp, INTERVAL '1' DAY) AS day, blob4 AS status,
			SUM(_sample_interval) AS requests, MAX(timestamp) AS last
		FROM ${env.USAGE_DATASET} WHERE timestamp > NOW() - INTERVAL '${days}' DAY
		GROUP BY account, day, status LIMIT 10000`);
	return rows.map((r) => ({ account: r.account, day: aeTime(r.day).slice(0, 10), status: r.status, requests: Number(r.requests), last: aeTime(r.last) }));
}

export interface KeyRouteRow {
	prefix: string;
	route: string;
	status: string;
	requests: number;
}

/** For one account: each key's requests by route and status. */
export async function accountRoutes(env: Env, account: string, days: number): Promise<KeyRouteRow[]> {
	const rows = await sql<{ prefix: string; route: string; status: string; requests: number | string }>(env,
		`SELECT blob2 AS prefix, blob3 AS route, blob4 AS status, SUM(_sample_interval) AS requests
		FROM ${env.USAGE_DATASET} WHERE blob1 = ${anyAccountLiteral(account)} AND timestamp > NOW() - INTERVAL '${days}' DAY
		GROUP BY prefix, route, status LIMIT 10000`);
	return rows.map((r) => ({ ...r, requests: Number(r.requests) }));
}

/** For one account: its requests by UTC day and status. */
export async function accountDays(env: Env, account: string, days: number): Promise<UsageRow[]> {
	const rows = await sql<{ day: string; status: string; requests: number | string; last: string }>(env,
		`SELECT toStartOfInterval(timestamp, INTERVAL '1' DAY) AS day, blob4 AS status, SUM(_sample_interval) AS requests, MAX(timestamp) AS last
		FROM ${env.USAGE_DATASET} WHERE blob1 = ${anyAccountLiteral(account)} AND timestamp > NOW() - INTERVAL '${days}' DAY
		GROUP BY day, status LIMIT 10000`);
	return rows.map((r) => ({ account, day: aeTime(r.day).slice(0, 10), status: r.status, requests: Number(r.requests), last: aeTime(r.last) }));
}

/**
 * The daily rollup: each key's last use (kept if older than Analytics
 * Engine's reach) and the kinds it read with a 200 over 30 days.
 */
export async function rollUp(env: Env): Promise<number> {
	const [routes, last] = await Promise.all([
		sql<{ account: string; prefix: string; route: string }>(env,
			`SELECT blob1 AS account, blob2 AS prefix, blob3 AS route FROM ${env.USAGE_DATASET} WHERE timestamp > NOW() - INTERVAL '30' DAY AND blob4 = '200' GROUP BY account, prefix, route`),
		sql<{ account: string; prefix: string; last: string }>(env,
			`SELECT blob1 AS account, blob2 AS prefix, MAX(timestamp) AS last FROM ${env.USAGE_DATASET} WHERE timestamp > NOW() - INTERVAL '90' DAY GROUP BY account, prefix`),
	]);

	const byKey = new Map<string, { routes: Set<string>; last?: string }>();
	const entry = (account: string, prefix: string) => {
		const id = `${account}\n${prefix}`;
		let found = byKey.get(id);
		if (!found) byKey.set(id, (found = { routes: new Set() }));
		return found;
	};
	for (const r of routes) entry(r.account, r.prefix).routes.add(r.route);
	for (const r of last) entry(r.account, r.prefix).last = aeTime(r.last);

	const statements: D1PreparedStatement[] = [];
	for (const [id, { routes: seen, last: lastUsed }] of byKey) {
		const [account, prefix] = id.split('\n');
		const match = /^u(\d+)$/.exec(account);
		if (!match || !prefix.startsWith('cpk_')) continue;
		statements.push(env.ACCOUNTS.prepare(
			`UPDATE keys SET uses = ?3,
				last_used_at = CASE WHEN last_used_at IS NULL OR last_used_at < ?4 THEN ?4 ELSE last_used_at END
			WHERE account_id = ?1 AND prefix = ?2`,
		).bind(Number(match[1]), prefix, JSON.stringify(usesFromRoutes(seen)), lastUsed ?? null));
	}
	// A key with no 200 in 30 days uses nothing now.
	statements.push(env.ACCOUNTS.prepare(`UPDATE keys SET uses = '[]' WHERE uses IS NOT NULL AND (last_used_at IS NULL OR last_used_at < ?)`)
		.bind(new Date(Date.now() - 30 * 86400_000).toISOString()));
	await env.ACCOUNTS.batch(statements);
	return statements.length - 1;
}

/** Requests to every Worker on the account over one UTC day. */
export async function workerRequests(env: Env, day: string): Promise<number> {
	if (!env.ANALYTICS_TOKEN || !env.CF_ACCOUNT_ID) throw new UsageUnavailable('ANALYTICS_TOKEN or CF_ACCOUNT_ID is not set');
	const start = `${day}T00:00:00Z`;
	const end = new Date(Date.parse(start) + 86400_000).toISOString();
	const res = await fetch('https://api.cloudflare.com/client/v4/graphql', {
		method: 'POST',
		headers: { authorization: `Bearer ${env.ANALYTICS_TOKEN}`, 'content-type': 'application/json' },
		body: JSON.stringify({
			query: `query ($account: string!, $start: Time!, $end: Time!) {
				viewer { accounts(filter: { accountTag: $account }) {
					workersInvocationsAdaptive(limit: 1000, filter: { datetime_geq: $start, datetime_lt: $end }) {
						sum { requests }
						dimensions { scriptName }
					}
				} }
			}`,
			variables: { account: env.CF_ACCOUNT_ID, start, end },
		}),
	});
	if (!res.ok) throw new UsageUnavailable(`GraphQL ${res.status}: ${await res.text()}`);
	const body = (await res.json()) as {
		data?: { viewer: { accounts: { workersInvocationsAdaptive: { sum: { requests: number } }[] }[] } };
		errors?: { message: string }[];
	};
	if (body.errors?.length) throw new UsageUnavailable(`GraphQL: ${body.errors.map((e) => e.message).join('; ')}`);
	return (body.data?.viewer.accounts[0]?.workersInvocationsAdaptive ?? []).reduce((sum, row) => sum + row.sum.requests, 0);
}
