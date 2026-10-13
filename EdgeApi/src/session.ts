import type { Caller } from './auth';
import type { Env } from './env';
import { badRequest, isoSeconds, json, problem } from './http';

/**
 * The Attractions Browser's sessions (DR-010 §7.4). The reader's browser
 * solves Turnstile, POST /v1/session swaps that for a cps_ token, and the
 * token works on the three endpoints the browser draws from. Nothing is
 * stored: the token is its own proof, an HMAC over a random ID and the expiry.
 */

/** How long a session lasts. Not bound to an IP (phones change IP mid-visit), so it is short. */
export const SESSION_SECONDS = 30 * 60;

/** The browser's own Turnstile widget sends this, so another of our widgets' tokens can't be swapped in. */
export const TURNSTILE_ACTION = 'attraction-browser';

const SITEVERIFY = 'https://challenges.cloudflare.com/turnstile/v0/siteverify';

// cps_<id>.<expiry, unix seconds>.<signature>; the ID is 16 random bytes and
// the signature an HMAC-SHA256, both unpadded base64url.
const TOKEN = /^cps_([A-Za-z0-9_-]{22})\.(\d{10})\.([A-Za-z0-9_-]{43})$/;

/**
 * Where a session is valid. Every other endpoint is a 403: a full record, the
 * change feed and settled data are what a key is for. The browser pages 25 at
 * a time, so a session can't ask for more.
 */
const SCOPE: Record<string, { limit: boolean }> = {
	'/v1/attractions': { limit: true },
	'/v1/attractions/facets': { limit: false },
	'/v1/parks': { limit: true },
};
export const SESSION_LIMIT = 25;

const b64url = (bytes: Uint8Array) =>
	btoa(String.fromCharCode(...bytes)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
const fromB64url = (text: string) => Uint8Array.from(atob(text.replace(/-/g, '+').replace(/_/g, '/')), (c) => c.charCodeAt(0));

let hmac: { secret: string; key: Promise<CryptoKey> } | undefined;

function signingKey(secret: string): Promise<CryptoKey> {
	if (hmac?.secret !== secret) {
		hmac = {
			secret,
			key: crypto.subtle.importKey('raw', new TextEncoder().encode(secret), { name: 'HMAC', hash: 'SHA-256' }, false, ['sign', 'verify']),
		};
	}
	return hmac.key;
}

/** A token for a new session, expiring SESSION_SECONDS after `now`. */
export async function mintSession(secret: string, now: number): Promise<{ token: string; expires: number }> {
	const id = b64url(crypto.getRandomValues(new Uint8Array(16)));
	const expires = Math.floor(now / 1000) + SESSION_SECONDS;
	const body = `cps_${id}.${expires}`;
	const sig = await crypto.subtle.sign('HMAC', await signingKey(secret), new TextEncoder().encode(body));
	return { token: `${body}.${b64url(new Uint8Array(sig))}`, expires };
}

const refused = (slug: string, title: string, detail?: string) =>
	problem(401, slug, title, detail, { 'www-authenticate': 'Bearer error="invalid_token"' });

/**
 * A cps_ token → the browser caller. A token that is present must be valid: a
 * malformed, badly signed or expired one is a 401, never anonymous. An
 * expired one says so, so the browser knows to solve Turnstile again.
 */
export async function verifySession(token: string, env: Env, now = Date.now()): Promise<Caller | Response> {
	const match = TOKEN.exec(token);
	if (!match || !env.SESSION_SECRET) return refused('session-invalid', 'Invalid session');

	const [, id, expText, sig] = match;
	const ok = await crypto.subtle.verify('HMAC', await signingKey(env.SESSION_SECRET), fromB64url(sig),
		new TextEncoder().encode(`cps_${id}.${expText}`));
	const expires = Number(expText);
	// A signed expiry further out than a session lasts was never minted by mintSession.
	if (!ok || expires > Math.floor(now / 1000) + SESSION_SECONDS + 60) return refused('session-invalid', 'Invalid session');
	if (expires <= Math.floor(now / 1000)) {
		return refused('session-expired', 'Session expired', 'Solve Turnstile again and POST /v1/session for a new one.');
	}

	return { account: 'browser', tier: 'browser', keyPrefix: `cps_${id.slice(0, 8)}`, session: id };
}

/**
 * Whether a session may ask this: one of SCOPE's routes, the latest view, a
 * browser-sized page. Null when it may. view must be written out: the cache
 * key writes in the default (settled), so a session's request is the same URL
 * a key would send for the same answer.
 */
export function sessionScope(route: string, url: URL): Response | null {
	const scope = SCOPE[route];
	if (!scope) {
		return problem(403, 'session-scope', 'This needs an API key',
			'A browser session reads the attractions list, its facets and the parks list. Records, the change feed and settled data need a key from https://api.coasterpedia.net/portal.');
	}
	if (url.searchParams.get('view') !== 'latest') {
		return badRequest('session-view', 'A session reads view=latest',
			'Pass view=latest. The settled view needs an API key.').response;
	}
	const limit = url.searchParams.get('limit');
	if (scope.limit && limit != null && !(Number(limit) <= SESSION_LIMIT)) {
		return badRequest('session-limit', `A session reads at most ${SESSION_LIMIT} at a time`,
			'Page with next_cursor. Larger pages need an API key.').response;
	}
	return null;
}

interface Siteverify {
	success: boolean;
	hostname?: string;
	action?: string;
	'error-codes'?: string[];
}

/**
 * POST /v1/session: { "turnstile": "<token>" } → { token, expires_at }. The
 * Turnstile token is checked once with siteverify, which also refuses a
 * replay, and must come from the browser's widget on the wiki's host.
 */
export async function createSession(request: Request, env: Env, now = Date.now()): Promise<Response> {
	if (!env.TURNSTILE_SECRET || !env.SESSION_SECRET || !env.TURNSTILE_HOSTNAME) {
		return problem(503, 'sessions-unavailable', 'Sessions are not configured');
	}

	let turnstile: unknown;
	try {
		turnstile = ((await request.json()) as { turnstile?: unknown })?.turnstile;
	} catch {
		turnstile = undefined;
	}
	// Turnstile's tokens are at most 2,048 characters.
	if (typeof turnstile !== 'string' || turnstile.length === 0 || turnstile.length > 2048) {
		return badRequest('parameter-required', 'turnstile is required',
			'POST a JSON body { "turnstile": "<token>" } with the token the Turnstile widget returned.').response;
	}

	const form = new FormData();
	form.append('secret', env.TURNSTILE_SECRET);
	form.append('response', turnstile);
	const ip = request.headers.get('cf-connecting-ip');
	if (ip) form.append('remoteip', ip);

	let verdict: Siteverify;
	try {
		const res = await fetch(SITEVERIFY, { method: 'POST', body: form });
		if (!res.ok) throw new Error(`siteverify ${res.status}`);
		verdict = await res.json<Siteverify>();
	} catch (e) {
		console.error(e);
		return problem(503, 'turnstile-unavailable', 'Turnstile could not be checked', 'Try again shortly.', { 'retry-after': '5' });
	}

	if (!verdict.success || verdict.hostname !== env.TURNSTILE_HOSTNAME || verdict.action !== TURNSTILE_ACTION) {
		return problem(403, 'turnstile-failed', 'Turnstile did not pass',
			'Each Turnstile token works once, for five minutes. Run the widget again for a new one.');
	}

	const { token, expires } = await mintSession(env.SESSION_SECRET, now);
	return json({ token, expires_at: isoSeconds(new Date(expires * 1000)) }, 200, { 'cache-control': 'no-store' });
}
