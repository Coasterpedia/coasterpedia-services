import type { Env } from './env';

/** What a handler answers: the response, and how many records it holds for the usage point. */
export interface Served {
	response: Response;
	rows: number;
}

// Internal headers on the stored copy, stripped on the way out: the row count,
// so a hit still records its rows, and when it was stored, for Age. Not Date,
// which the edge may rewrite.
const ROWS = 'cp-rows';
const STORED_AT = 'cp-stored-at';

/** CACHE_SECONDS from wrangler.toml; 0 or unset turns the cache off. */
export function cacheSeconds(env: Env): number {
	const n = Number(env.CACHE_SECONDS ?? 0);
	return Number.isFinite(n) && n > 0 ? Math.floor(n) : 0;
}

/**
 * The cache key for a request (DR-010 §7.3): the URL, never the key, which
 * travels in a header, and the path with its trailing slash trimmed.
 * Parameters are sorted, so their order does not split the cache; the sort is
 * stable, so a repeated parameter keeps its first value first, which is the
 * one the handlers read. A route that takes view= gets the
 * default written in, so ?view=settled and no view share an entry (§6.3).
 *
 * `extra` adds what changes the answer without being in the URL: the
 * caller's feed delay on /v1/changes. A request that already carries one of
 * those names is not cached: it would otherwise read another tier's entry.
 */
export function cacheKey(url: URL, path: string, view: boolean, extra: Record<string, string> = {}): Request | null {
	const key = new URL(path, url.origin);
	key.search = url.search;
	if (view && !key.searchParams.has('view')) key.searchParams.set('view', 'settled');
	for (const [name, value] of Object.entries(extra)) {
		if (key.searchParams.has(name)) return null;
		key.searchParams.set(name, value);
	}
	key.searchParams.sort();
	return new Request(key.toString());
}

/**
 * Answer from this location's cache, or run the handler and keep a 200 for
 * CACHE_SECONDS. Nothing else is kept: an error costs D1 little, and a 404
 * in the settled view stops being true when the record settles.
 *
 * The edge cannot purge every location when ingest lands, so a cached answer
 * is up to CACHE_SECONDS older than D1. meta.data_as_of in the body still
 * says when its data left the wiki, and Age says how long it has been held.
 * `keyed` marks an answer that needed a key: private, so no shared cache
 * between the client and us hands it to someone without one.
 */
export async function serveCached(
	key: Request | null,
	env: Env,
	ctx: ExecutionContext,
	keyed: boolean,
	run: () => Promise<Served>,
): Promise<Served & { hit: boolean }> {
	const ttl = cacheSeconds(env);
	if (ttl === 0 || !key) return { ...(await run()), hit: false };

	const cache = caches.default;
	const stored = await cache.match(key);
	if (stored) {
		const age = Math.floor((Date.now() - Number(stored.headers.get(STORED_AT) ?? Date.now())) / 1000);
		return { response: outgoing(stored, ttl, age, keyed), rows: Number(stored.headers.get(ROWS) ?? 0), hit: true };
	}

	const served = await run();
	if (served.response.status !== 200) return { ...served, hit: false };

	const body = await served.response.arrayBuffer();
	const headers = new Headers(served.response.headers);
	headers.set('cache-control', `public, max-age=${ttl}`);
	headers.set(ROWS, String(served.rows));
	headers.set(STORED_AT, String(Date.now()));
	ctx.waitUntil(cache.put(key, new Response(body, { status: 200, headers })));
	return { response: outgoing(new Response(body, { status: 200, headers }), ttl, 0, keyed), rows: served.rows, hit: false };
}

function outgoing(stored: Response, ttl: number, age: number, keyed: boolean): Response {
	const headers = new Headers(stored.headers);
	headers.delete(ROWS);
	headers.delete(STORED_AT);
	// max-age with Age: a client's copy stays fresh only for what the edge's has left.
	headers.set('cache-control', `${keyed ? 'private' : 'public'}, max-age=${ttl}`);
	headers.set('age', String(Math.min(Math.max(age, 0), ttl)));
	return new Response(stored.body, { status: stored.status, headers });
}
