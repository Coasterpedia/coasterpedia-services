import { createExecutionContext, waitOnExecutionContext } from 'cloudflare:test';
import { env } from 'cloudflare:workers';
import { expect, vi } from 'vitest';
import worker from '../src/index';

export const PORTAL = 'https://developer.coasterpedia.net';
export const WIKI = 'https://coasterpedia.net';

export interface Profile {
	sub: number;
	username: string;
	confirmed_email?: boolean;
	blocked?: boolean;
	email?: string;
}

/** Outbound requests the Worker made, as the stub saw them. */
export const outbound: { url: string; init?: RequestInit }[] = [];
export const DEVELOPER: Profile = { sub: 42, username: 'Developer', confirmed_email: true, email: 'dev@example.com' };
/** What the wiki's profile endpoint answers next. */
export let profile: Profile = DEVELOPER;
export function setProfile(next: Profile) {
	profile = next;
}
/** Rows the Analytics Engine SQL API answers, by a fragment of the query. */
export let aeRows: { match: string; data: unknown[] }[] = [];
export function setAeRows(rows: { match: string; data: unknown[] }[]) {
	aeRows = rows;
}

/** Wiki user IDs with a sitewide block, as list=users answers; null: the wiki is down. */
export let wikiBlocked: Set<number> | null = new Set();
export function setWikiBlocked(next: Set<number> | null) {
	wikiBlocked = next;
}

/** The wiki, SES and the Cloudflare APIs, all stubbed. */
export function stubOutbound() {
	outbound.length = 0;
	wikiBlocked = new Set();
	vi.spyOn(globalThis, 'fetch').mockImplementation(async (input, init) => {
		const request = input instanceof Request ? input : new Request(String(input), init);
		const url = request.url;
		outbound.push({ url, init: { method: request.method, headers: request.headers, body: init?.body ?? (await request.clone().text()) } });
		if (url === `${WIKI}/w/rest.php/oauth2/access_token`) return Response.json({ access_token: 'wiki-access-token', token_type: 'Bearer' });
		if (url === `${WIKI}/w/rest.php/oauth2/resource/profile`) return Response.json(profile);
		if (url.startsWith(`${WIKI}/w/api.php?`) && new URL(url).searchParams.get('list') === 'users') {
			if (!wikiBlocked) return new Response('Bad gateway', { status: 502 });
			const ids = new URL(url).searchParams.get('ususerids')!.split('|').map(Number);
			return Response.json({
				query: {
					users: ids.map((id) => (wikiBlocked!.has(id)
						? { userid: id, name: `User ${id}`, blockid: 1, blockedby: 'Admin', blockreason: 'Vandalism', blockexpiry: 'infinite', blockpartial: false }
						: { userid: id, name: `User ${id}` })),
				},
			});
		}
		if (url.includes('/analytics_engine/sql')) {
			const sql = String(init?.body ?? (await request.clone().text()));
			return Response.json({ data: aeRows.find((r) => sql.includes(r.match))?.data ?? [] });
		}
		if (url === 'https://api.cloudflare.com/client/v4/graphql') {
			return Response.json({ data: { viewer: { accounts: [{ workersInvocationsAdaptive: [{ sum: { requests: 250_000 } }] }] } } });
		}
		if (url.startsWith('https://email.')) return Response.json({ MessageId: 'test' });
		throw new Error(`unexpected outbound fetch: ${url}`);
	});
}

/** A cookie jar of one, enough for one reader's session. */
export class Browser {
	cookies = new Map<string, string>();

	/** bindings: env with some bindings changed, for one request. */
	async fetch(path: string, init: RequestInit = {}, bindings: typeof env = env): Promise<Response> {
		const headers = new Headers(init.headers);
		if (this.cookies.size) headers.set('cookie', [...this.cookies].map(([k, v]) => `${k}=${v}`).join('; '));
		if (init.method && init.method !== 'GET' && !headers.has('origin')) headers.set('origin', PORTAL);
		const ctx = createExecutionContext();
		const res = await worker.fetch(new Request(PORTAL + path, { ...init, headers, redirect: 'manual' }), bindings, ctx);
		await waitOnExecutionContext(ctx);
		for (const line of res.headers.getSetCookie()) {
			const [pair, ...attrs] = line.split(';');
			const [name, value] = pair.split('=');
			if (attrs.some((a) => /max-age=0/i.test(a.trim())) || value === '') this.cookies.delete(name.trim());
			else this.cookies.set(name.trim(), value);
		}
		return res;
	}

	post(path: string, form: Record<string, string> | FormData, headers: Record<string, string> = {}) {
		const body = form instanceof FormData ? form : new URLSearchParams(form);
		return this.fetch(path, { method: 'POST', body, headers });
	}

	/** The whole sign-in round trip, the wiki stubbed. */
	async signIn(as: Profile = DEVELOPER): Promise<Response> {
		setProfile(as);
		const start = await this.fetch('/auth/sign-in?return=/account/keys');
		expect(start.status).toBe(303);
		const authorize = new URL(start.headers.get('location')!);
		return this.fetch(`/auth/callback?code=wiki-code&state=${authorize.searchParams.get('state')}`);
	}
}

export async function resetAccounts() {
	await env.ACCOUNTS.batch([
		env.ACCOUNTS.prepare('DELETE FROM listings'),
		env.ACCOUNTS.prepare('DELETE FROM keys'),
		env.ACCOUNTS.prepare('DELETE FROM accounts'),
	]);
}
