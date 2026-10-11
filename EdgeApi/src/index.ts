import { getAttraction, listAttractions, meta } from './attractions';
import { authenticate, authenticateInternal, type Caller } from './auth';
import type { Env } from './env';
import { problem, ProblemError } from './http';
import { ingest } from './ingest';

export type { Env } from './env';

interface Handled {
	response: Response;
	rows: number;
	route: string;
}

export default {
	async fetch(request: Request, env: Env): Promise<Response> {
		const url = new URL(request.url);
		const path = url.pathname.replace(/\/+$/, '') || '/';

		try {
			if (path === '/internal/ingest') {
				if (request.method !== 'POST') return problem(405, 'method', 'Method not allowed');
				return (await authenticateInternal(request, env)) ?? (await ingest(request, env));
			}

			if (request.method !== 'GET' && request.method !== 'HEAD') {
				return problem(405, 'method', 'Method not allowed', 'The API is read-only.');
			}

			// Open: no key needed (DR-010 §6).
			if (path === '/v1/meta') {
				return await meta(url, env);
			}

			if (!path.startsWith('/v1/')) {
				return problem(404, 'not-found', 'Not found', 'The API lives under /v1/.');
			}

			const caller = await authenticate(request, env);
			if (caller instanceof Response) return caller;

			if (env.RATE_LIMITER) {
				const { success } = await env.RATE_LIMITER.limit({ key: caller.account });
				if (!success) {
					record(env, caller, 'rate-limited', 429, 0);
					return problem(429, 'rate-limited', 'Too many requests', undefined, { 'retry-after': '60' });
				}
			}

			const handled = await route(path, url, env);
			record(env, caller, handled.route, handled.response.status, handled.rows);
			return handled.response;
		} catch (e) {
			if (e instanceof ProblemError) return e.response;
			console.error(e);
			return problem(500, 'internal-error', 'Internal error');
		}
	},
} satisfies ExportedHandler<Env>;

async function route(path: string, url: URL, env: Env): Promise<Handled> {
	if (path === '/v1/attractions') {
		return { route: '/v1/attractions', ...(await listAttractions(url, env)) };
	}

	const one = /^\/v1\/attractions\/([^/]+)$/.exec(path);
	if (one) {
		return { route: '/v1/attractions/{id}', ...(await getAttraction(url, one[1], env)) };
	}

	return { route: 'unknown', rows: 0, response: problem(404, 'not-found', 'Not found') };
}

/** One Analytics Engine point per keyed request: usage pages and, later, billing (DR-010 §7.3). */
function record(env: Env, caller: Caller, route: string, status: number, rows: number): void {
	env.USAGE?.writeDataPoint({
		indexes: [caller.account],
		blobs: [caller.account, caller.keyPrefix, route, String(status), caller.tier],
		doubles: [rows],
	});
}
