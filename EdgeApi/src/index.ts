import { getAttraction, listAttractions, meta } from './attractions';
import { authenticate, authenticateInternal, type Caller } from './auth';
import { cacheKey, serveCached, type Served } from './cache';
import { delayHours, listChanges, pruneChanges } from './changes';
import type { Env } from './env';
import { attractionFacets } from './facets';
import { problem, ProblemError } from './http';
import { ingest } from './ingest';
import { getManufacturer, listManufacturers } from './manufacturers';
import { getPark, listParks, parkAttractions } from './parks';
import { getParkCompany, listParkCompanies, parkCompanyParks } from './park-companies';
import { getProduct, listProducts, manufacturerProducts, productInstallations } from './products';
import { search } from './search';
import { LIMITED_HEADERS, withPolicy } from './ratelimit';
import { createSession, sessionScope } from './session';
import { vocabularies } from './vocabularies';

export type { Env } from './env';

/**
 * The browser's same-origin door (DR-010 §7.4): coasterpedia.net/api/* is
 * routed here, so the gadget needs no CORS. The prefix is stripped and the
 * request read as the API host's, so both hosts share one cache entry.
 */
const SAME_ORIGIN_PREFIX = '/api';
const API_ORIGIN = 'https://api.coasterpedia.net';

/** A matched endpoint: its template, whether it takes view=, and how to answer it. */
interface Route {
	name: string;
	view: boolean;
	/** What changes the answer beyond the URL, for the cache key. */
	extra?: Record<string, string>;
	run: () => Promise<Served>;
}

export default {
	async fetch(request: Request, env: Env, ctx: ExecutionContext): Promise<Response> {
		let url = new URL(request.url);
		// Only /v1: /internal stays on the API host, behind Access.
		if (url.pathname.startsWith(`${SAME_ORIGIN_PREFIX}/v1/`)) {
			url = new URL(url.pathname.slice(SAME_ORIGIN_PREFIX.length) + url.search, API_ORIGIN);
		}
		const path = url.pathname.replace(/\/+$/, '') || '/';

		try {
			if (path === '/internal/ingest') {
				if (request.method !== 'POST') return problem(405, 'method', 'Method not allowed');
				return (await authenticateInternal(request, env)) ?? (await ingest(request, env));
			}

			// No key and no limit: each session costs a Turnstile solve, which is the limit.
			if (path === '/v1/session') {
				if (request.method !== 'POST') return problem(405, 'method', 'Method not allowed');
				const res = await createSession(request, env);
				record(env, { account: 'browser', tier: 'browser', keyPrefix: '' }, '/v1/session', res.status, 0, false);
				return res;
			}

			if (request.method !== 'GET' && request.method !== 'HEAD') {
				return problem(405, 'method', 'Method not allowed', 'The API is read-only.');
			}

			// Open: no key needed (DR-010 §6), and no limit, so the cache is what
			// keeps a client hammering them off D1.
			if (path === '/v1/meta') {
				return (await serveCached(cacheKey(url, path, true), env, ctx, false, async () => ({ response: await meta(url, env), rows: 0 }))).response;
			}
			if (path === '/v1/vocabularies') {
				return (await serveCached(cacheKey(url, path, false), env, ctx, false, async () => ({ response: await vocabularies(url, env), rows: 0 }))).response;
			}

			if (!path.startsWith('/v1/')) {
				return problem(404, 'not-found', 'Not found', 'The API lives under /v1/.');
			}

			const caller = await authenticate(request, env);
			if (caller instanceof Response) return caller;

			// Before the cache: a hit is still a request, and counts against the limit.
			// Every session is the one account, so a session is limited on its own.
			if (env.RATE_LIMITER) {
				const { success } = await env.RATE_LIMITER.limit({ key: caller.session ? `session:${caller.session}` : caller.account });
				if (!success) {
					record(env, caller, 'rate-limited', 429, 0, false);
					return problem(429, 'rate-limited', 'Too many requests', undefined, LIMITED_HEADERS);
				}
			}

			const matched = route(path, url, env, caller);
			if (!matched) {
				record(env, caller, 'unknown', 404, 0, false);
				return withPolicy(problem(404, 'not-found', 'Not found'));
			}

			// Before the cache too: a session must not read a keyed answer someone cached.
			const outOfScope = caller.session ? sessionScope(matched.name, url) : null;
			if (outOfScope) {
				record(env, caller, matched.name, outOfScope.status, 0, false);
				return withPolicy(outOfScope);
			}

			let served: Served & { hit: boolean };
			try {
				served = await serveCached(cacheKey(url, path, matched.view, matched.extra), env, ctx, true, matched.run);
			} catch (e) {
				if (!(e instanceof ProblemError)) throw e;
				served = { response: e.response, rows: 0, hit: false };
			}
			record(env, caller, matched.name, served.response.status, served.rows, served.hit);
			return withPolicy(served.response);
		} catch (e) {
			if (e instanceof ProblemError) return e.response;
			console.error(e);
			return problem(500, 'internal-error', 'Internal error');
		}
	},

	/** Daily (wrangler.toml): keeps the change log to its window. */
	async scheduled(controller: ScheduledController, env: Env): Promise<void> {
		const deleted = await pruneChanges(env, new Date(controller.scheduledTime));
		console.log(`Pruned ${deleted} change log lines`);
	},
} satisfies ExportedHandler<Env>;

function route(path: string, url: URL, env: Env, caller: Caller): Route | null {
	if (path === '/v1/changes') {
		return {
			name: '/v1/changes', view: true,
			// The tier's delay changes the answer; keyed by the delay, tiers that share one share entries.
			extra: { delay: `PT${delayHours(caller.tier)}H` },
			run: () => listChanges(url, env, delayHours(caller.tier)),
		};
	}

	if (path === '/v1/search') {
		return { name: '/v1/search', view: true, run: () => search(url, env) };
	}

	if (path === '/v1/attractions') {
		return { name: '/v1/attractions', view: true, run: () => listAttractions(url, env) };
	}

	// Before {id}, which would otherwise take "facets" for an ID.
	if (path === '/v1/attractions/facets') {
		return { name: '/v1/attractions/facets', view: true, run: () => attractionFacets(url, env) };
	}

	const one = /^\/v1\/attractions\/([^/]+)$/.exec(path);
	if (one) {
		return { name: '/v1/attractions/{id}', view: true, run: () => getAttraction(url, one[1], env) };
	}

	if (path === '/v1/parks') {
		return { name: '/v1/parks', view: true, run: () => listParks(url, env) };
	}

	const at = /^\/v1\/parks\/([^/]+)\/attractions$/.exec(path);
	if (at) {
		return { name: '/v1/parks/{id}/attractions', view: true, run: () => parkAttractions(url, at[1], env) };
	}

	const park = /^\/v1\/parks\/([^/]+)$/.exec(path);
	if (park) {
		return { name: '/v1/parks/{id}', view: true, run: () => getPark(url, park[1], env) };
	}

	if (path === '/v1/manufacturers') {
		return { name: '/v1/manufacturers', view: true, run: () => listManufacturers(url, env) };
	}

	const made = /^\/v1\/manufacturers\/([^/]+)\/products$/.exec(path);
	if (made) {
		return { name: '/v1/manufacturers/{id}/products', view: true, run: () => manufacturerProducts(url, made[1], env) };
	}

	const manufacturer = /^\/v1\/manufacturers\/([^/]+)$/.exec(path);
	if (manufacturer) {
		return { name: '/v1/manufacturers/{id}', view: true, run: () => getManufacturer(url, manufacturer[1], env) };
	}

	if (path === '/v1/park-companies') {
		return { name: '/v1/park-companies', view: true, run: () => listParkCompanies(url, env) };
	}

	const owned = /^\/v1\/park-companies\/([^/]+)\/parks$/.exec(path);
	if (owned) {
		return { name: '/v1/park-companies/{id}/parks', view: true, run: () => parkCompanyParks(url, owned[1], env) };
	}

	const company = /^\/v1\/park-companies\/([^/]+)$/.exec(path);
	if (company) {
		return { name: '/v1/park-companies/{id}', view: true, run: () => getParkCompany(url, company[1], env) };
	}

	if (path === '/v1/products') {
		return { name: '/v1/products', view: true, run: () => listProducts(url, env) };
	}

	const built = /^\/v1\/products\/([^/]+)\/installations$/.exec(path);
	if (built) {
		return { name: '/v1/products/{id}/installations', view: true, run: () => productInstallations(url, built[1], env) };
	}

	const product = /^\/v1\/products\/([^/]+)$/.exec(path);
	if (product) {
		return { name: '/v1/products/{id}', view: true, run: () => getProduct(url, product[1], env) };
	}

	return null;
}

/** One Analytics Engine point per keyed request: usage pages and, later, billing (DR-010 §7.3). */
function record(env: Env, caller: Caller, route: string, status: number, rows: number, hit: boolean): void {
	env.USAGE?.writeDataPoint({
		indexes: [caller.account],
		blobs: [caller.account, caller.keyPrefix, route, String(status), caller.tier, hit ? 'hit' : 'miss'],
		doubles: [rows],
	});
}
