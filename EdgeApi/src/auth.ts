import { createRemoteJWKSet, jwtVerify } from 'jose';
import type { Env, KeyEntry } from './env';
import { problem } from './http';
import { verifySession } from './session';

export interface Caller {
	account: string;
	tier: string;
	keyPrefix: string;
	/** A browser session's ID (DR-010 §7.4); absent for a key. */
	session?: string;
}

export async function sha256Hex(text: string): Promise<string> {
	const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(text));
	return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, '0')).join('');
}

/**
 * Bearer cpk_… → the account behind it (DR-010 §7.3). KV holds only the
 * key's hash, so a leaked KV dump leaks no keys. Revocation reaches every
 * location within KV's propagation, about a minute.
 *
 * Bearer cps_… is a browser session (§7.4), checked by its signature alone.
 * Anything else is a 401: nothing falls back to anonymous.
 */
export async function authenticate(request: Request, env: Env): Promise<Caller | Response> {
	const header = request.headers.get('authorization') ?? '';
	const session = /^Bearer\s+(cps_\S*)$/.exec(header);
	if (session) return verifySession(session[1], env);

	const match = /^Bearer\s+(cpk_[A-Za-z0-9]+)$/.exec(header);
	if (!match) {
		return problem(401, 'key-required', 'An API key is required',
			'Send Authorization: Bearer cpk_… with a key from the developer portal. During the closed beta, keys are issued by hand.',
			{ 'www-authenticate': 'Bearer' });
	}

	const key = match[1];
	const entry = await env.KEYS.get<KeyEntry>(`key:${await sha256Hex(key)}`, { type: 'json', cacheTtl: 60 });
	if (!entry) {
		return problem(401, 'key-unknown', 'Unknown API key', undefined, { 'www-authenticate': 'Bearer' });
	}
	if (entry.status !== 'active') {
		return problem(403, 'key-revoked', 'This API key has been revoked');
	}

	return { account: entry.account, tier: entry.tier, keyPrefix: key.slice(0, 8) };
}

let jwks: ReturnType<typeof createRemoteJWKSet> | undefined;
let jwksTeam = '';

/**
 * /internal/* is behind a Cloudflare Access application; the Worker checks
 * Access's JWT as well, so a route or DNS mistake that bypasses Access
 * still cannot write (DR-010 §7.3). Fails closed when unconfigured.
 */
export async function authenticateInternal(request: Request, env: Env): Promise<Response | null> {
	if (env.DEV_INGEST_TOKEN && request.headers.get('x-dev-ingest-token') === env.DEV_INGEST_TOKEN) {
		return null;
	}

	const token = request.headers.get('cf-access-jwt-assertion');
	if (!env.ACCESS_TEAM_DOMAIN || !env.ACCESS_AUD || !token) {
		return problem(403, 'internal', 'Forbidden');
	}

	if (!jwks || jwksTeam !== env.ACCESS_TEAM_DOMAIN) {
		jwks = createRemoteJWKSet(new URL(`https://${env.ACCESS_TEAM_DOMAIN}/cdn-cgi/access/certs`));
		jwksTeam = env.ACCESS_TEAM_DOMAIN;
	}

	try {
		await jwtVerify(token, jwks, { issuer: `https://${env.ACCESS_TEAM_DOMAIN}`, audience: env.ACCESS_AUD });
		return null;
	} catch {
		return problem(403, 'internal', 'Forbidden');
	}
}
