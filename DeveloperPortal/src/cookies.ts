// Signed cookies: base64url(JSON).signature, HMAC-SHA256 under
// PORTAL_SESSION_SECRET. Nothing is stored server-side; rotating the secret
// signs everyone out.
import { deleteCookie, getCookie, setCookie } from 'hono/cookie';
import type { Ctx } from './app';

/** Host-only, HTTPS-only, never sent to api. (§13.7). */
export const SESSION_COOKIE = '__Host-portal';
/** The sign-in round trip's state, PKCE verifier and return path. */
export const OAUTH_COOKIE = '__Host-portal-oauth';

export const SESSION_HOURS = 8;
const OAUTH_MINUTES = 10;

export interface Session {
	/** Wiki user ID. */
	sub: number;
	name: string;
	/** Expiry, unix seconds. */
	exp: number;
}

export interface OAuthState {
	state: string;
	verifier: string;
	returnTo: string;
	exp: number;
}

const encoder = new TextEncoder();

export function b64url(bytes: Uint8Array): string {
	return btoa(String.fromCharCode(...bytes)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

function fromB64url(text: string): Uint8Array<ArrayBuffer> {
	const b64 = text.replace(/-/g, '+').replace(/_/g, '/');
	return Uint8Array.from(atob(b64), (c) => c.charCodeAt(0));
}

async function key(secret: string): Promise<CryptoKey> {
	return crypto.subtle.importKey('raw', encoder.encode(secret), { name: 'HMAC', hash: 'SHA-256' }, false, ['sign', 'verify']);
}

export async function seal(value: object, secret: string): Promise<string> {
	const body = b64url(encoder.encode(JSON.stringify(value)));
	const sig = await crypto.subtle.sign('HMAC', await key(secret), encoder.encode(body));
	return `${body}.${b64url(new Uint8Array(sig))}`;
}

/** The value, or null for anything unsigned, tampered with or expired. */
export async function unseal<T extends { exp: number }>(token: string | undefined, secret: string): Promise<T | null> {
	if (!token) return null;
	const [body, sig] = token.split('.');
	if (!body || !sig) return null;
	try {
		const ok = await crypto.subtle.verify('HMAC', await key(secret), fromB64url(sig), encoder.encode(body));
		if (!ok) return null;
		const value = JSON.parse(new TextDecoder().decode(fromB64url(body))) as T;
		return value.exp > Date.now() / 1000 ? value : null;
	} catch {
		return null;
	}
}

function secret(c: Ctx): string {
	const value = c.env.PORTAL_SESSION_SECRET;
	if (!value) throw new Error('PORTAL_SESSION_SECRET is not set');
	return value;
}

const cookieOptions = (maxAge: number) => ({ path: '/', secure: true, httpOnly: true, sameSite: 'Lax' as const, maxAge });

export async function readSession(c: Ctx): Promise<Session | null> {
	return unseal<Session>(getCookie(c, SESSION_COOKIE), secret(c));
}

export async function writeSession(c: Ctx, sub: number, name: string): Promise<void> {
	const session: Session = { sub, name, exp: Math.floor(Date.now() / 1000) + SESSION_HOURS * 3600 };
	setCookie(c, SESSION_COOKIE, await seal(session, secret(c)), cookieOptions(SESSION_HOURS * 3600));
}

export function clearSession(c: Ctx): void {
	deleteCookie(c, SESSION_COOKIE, { path: '/', secure: true });
}

export async function readOAuth(c: Ctx): Promise<OAuthState | null> {
	return unseal<OAuthState>(getCookie(c, OAUTH_COOKIE), secret(c));
}

export async function writeOAuth(c: Ctx, value: Omit<OAuthState, 'exp'>): Promise<void> {
	const state: OAuthState = { ...value, exp: Math.floor(Date.now() / 1000) + OAUTH_MINUTES * 60 };
	setCookie(c, OAUTH_COOKIE, await seal(state, secret(c)), cookieOptions(OAUTH_MINUTES * 60));
}

export function clearOAuth(c: Ctx): void {
	deleteCookie(c, OAUTH_COOKIE, { path: '/', secure: true });
}

export function randomToken(bytes = 32): string {
	return b64url(crypto.getRandomValues(new Uint8Array(bytes)));
}
