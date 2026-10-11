// Responses: JSON bodies, and errors as RFC 9457 problem details.

export const LICENCE = {
	name: 'CC BY-SA 4.0',
	url: 'https://creativecommons.org/licenses/by-sa/4.0/',
	attribution: 'Coasterpedia contributors',
} as const;

export const SCHEMA_VERSION = '1.0.0-beta.3';

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
