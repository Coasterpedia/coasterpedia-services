// Responses: JSON bodies, and errors as RFC 9457 problem details.

export const LICENCE = {
	name: 'CC BY-SA 4.0',
	url: 'https://creativecommons.org/licenses/by-sa/4.0/',
	attribution: 'Coasterpedia contributors',
} as const;

export const SCHEMA_VERSION = '1.0.0-beta.12';

/**
 * How far back /v1/changes reaches, published in /v1/meta. A daily prune keeps
 * the log to this, and a cursor older than it is 410 Gone, never a feed with a
 * hole in it.
 */
export const CHANGES_WINDOW_DAYS = 90;
export const CHANGES_WINDOW = `P${CHANGES_WINDOW_DAYS}D`;

/** UTC to the second, "2026-10-11T12:00:00Z": what every timestamp in the API looks like. */
export function isoSeconds(at: Date): string {
	return at.toISOString().replace(/\.\d{3}Z$/, 'Z');
}

const PROBLEM_BASE = 'https://api.coasterpedia.net/problems/';

export function json(body: unknown, status = 200, headers: HeadersInit = {}): Response {
	return new Response(JSON.stringify(body), {
		status,
		headers: { 'content-type': 'application/json; charset=utf-8', ...headers },
	});
}

export function problem(status: number, slug: string, title: string, detail?: string, headers: HeadersInit = {}): Response {
	return new Response(JSON.stringify({ type: PROBLEM_BASE + slug, title, status, detail }), {
		status,
		headers: { 'content-type': 'application/problem+json; charset=utf-8', ...headers },
	});
}

/** A thrown problem, so a validator deep in a handler can end the request. */
export class ProblemError extends Error {
	constructor(readonly response: Response) {
		super(response.statusText);
	}
}

export function badRequest(slug: string, title: string, detail?: string): ProblemError {
	return new ProblemError(problem(400, slug, title, detail));
}

/** A cursor's contents as unpadded base64url JSON. Opaque to clients. */
export function encodeOpaque(value: unknown): string {
	const bytes = new TextEncoder().encode(JSON.stringify(value));
	return btoa(String.fromCharCode(...bytes)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

/** The inverse of encodeOpaque; throws on anything it did not make. */
export function decodeOpaque(text: string): unknown {
	const b64 = text.replace(/-/g, '+').replace(/_/g, '/');
	const bytes = Uint8Array.from(atob(b64), (c) => c.charCodeAt(0));
	return JSON.parse(new TextDecoder().decode(bytes));
}
