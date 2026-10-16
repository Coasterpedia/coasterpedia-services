import { env } from 'cloudflare:workers';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { safeReturn } from '../src/auth';
import { USER_AGENT } from '../src/app';
import { Browser, outbound, resetAccounts, stubOutbound, WIKI } from './helpers';

// DR-010 §7.2, §13.11: sign in with Coasterpedia.

beforeEach(async () => {
	await resetAccounts();
	stubOutbound();
});
afterEach(() => vi.restoreAllMocks());

describe('sign-in', () => {
	it('sends the reader to the wiki with PKCE and a state', async () => {
		const res = await new Browser().fetch('/auth/sign-in?return=/account/keys');
		expect(res.status).toBe(303);
		const url = new URL(res.headers.get('location')!);
		expect(url.origin + url.pathname).toBe(`${WIKI}/w/rest.php/oauth2/authorize`);
		expect(url.searchParams.get('client_id')).toBe('test-client');
		expect(url.searchParams.get('redirect_uri')).toBe('https://developer.coasterpedia.net/auth/callback');
		expect(url.searchParams.get('code_challenge_method')).toBe('S256');
		expect(url.searchParams.get('code_challenge')).toMatch(/^[A-Za-z0-9_-]{43}$/);
		expect(url.searchParams.get('state')).toMatch(/^[A-Za-z0-9_-]{22}$/);
		const cookie = res.headers.getSetCookie().find((c) => c.startsWith('__Host-portal-oauth='))!;
		expect(cookie).toMatch(/HttpOnly/i);
		expect(cookie).toMatch(/Secure/i);
		expect(cookie).toMatch(/Path=\//i);
		expect(cookie).not.toMatch(/Domain=/i);
	});

	it('signs in, keeps the account, and throws the wiki tokens away', async () => {
		const browser = new Browser();
		const res = await browser.signIn({ sub: 42, username: 'Developer', confirmed_email: true, email: 'dev@example.com' });
		expect(res.status).toBe(303);
		expect(res.headers.get('location')).toBe('/account/keys');
		expect(browser.cookies.has('__Host-portal')).toBe(true);
		expect(browser.cookies.has('__Host-portal-oauth')).toBe(false);

		const account = await env.ACCOUNTS.prepare('SELECT * FROM accounts WHERE id = 42').first<Record<string, unknown>>();
		expect(account).toMatchObject({ username: 'Developer', email: 'dev@example.com', tier: 'free' });
		expect(JSON.stringify(account)).not.toContain('wiki-access-token');

		const exchange = outbound.find((o) => o.url.endsWith('/oauth2/access_token'))!;
		const body = new URLSearchParams(String(exchange.init!.body));
		expect(body.get('grant_type')).toBe('authorization_code');
		expect(body.get('code_verifier')).toMatch(/^[A-Za-z0-9_-]{43}$/);
		expect(body.get('client_secret')).toBe('test-client-secret');
		// The zone's WAF refuses an empty user agent (§13.10).
		for (const o of outbound) expect(new Headers(o.init!.headers).get('user-agent')).toBe(USER_AGENT);
	});

	it('keeps the account on a rename', async () => {
		await new Browser().signIn({ sub: 42, username: 'Old name', confirmed_email: true, email: 'dev@example.com' });
		await new Browser().signIn({ sub: 42, username: 'New name', confirmed_email: true, email: 'dev@example.com' });
		const { results } = await env.ACCOUNTS.prepare('SELECT username FROM accounts').all();
		expect(results).toEqual([{ username: 'New name' }]);
	});

	it('refuses a callback whose state does not match', async () => {
		const browser = new Browser();
		await browser.fetch('/auth/sign-in');
		const res = await browser.fetch('/auth/callback?code=wiki-code&state=someone-elses');
		expect(res.status).toBe(400);
		expect(outbound).toHaveLength(0);
	});

	it('tells a reader who declined on the wiki in our words, not OAuth’s', async () => {
		const browser = new Browser();
		await browser.fetch('/auth/sign-in');
		const res = await browser.fetch('/auth/callback?error=access_denied&error_description=The+resource+owner+or+authorization+server+denied+the+request.');
		const html = await res.text();
		expect(html).toContain('You didn’t allow access on the wiki');
		expect(html).not.toContain('resource owner');
	});

	it('refuses a callback with no sign-in in progress', async () => {
		const res = await new Browser().fetch('/auth/callback?code=wiki-code&state=anything');
		expect(res.status).toBe(400);
	});

	it('refuses a blocked account', async () => {
		const res = await new Browser().signIn({ sub: 43, username: 'Blocked', confirmed_email: true, email: 'b@example.com', blocked: true });
		expect(res.status).toBe(403);
		expect(await env.ACCOUNTS.prepare('SELECT 1 FROM accounts WHERE id = 43').first()).toBeNull();
	});

	it('asks for a confirmed email address first', async () => {
		const res = await new Browser().signIn({ sub: 44, username: 'Unconfirmed', confirmed_email: false, email: 'u@example.com' });
		expect(res.status).toBe(403);
		expect(await res.text()).toContain('Special:ConfirmEmail');
	});

	it('signs out', async () => {
		const browser = new Browser();
		await browser.signIn();
		expect(browser.cookies.has('__Host-portal')).toBe(true);
		const res = await browser.post('/auth/sign-out', {});
		expect(res.status).toBe(303);
		expect(browser.cookies.has('__Host-portal')).toBe(false);
		expect((await browser.fetch('/account/keys')).status).toBe(303);
	});

	it('refuses a form posted from another site', async () => {
		const browser = new Browser();
		await browser.signIn();
		const res = await browser.post('/auth/sign-out', {}, { origin: 'https://evil.example' });
		expect(res.status).toBe(403);
	});

	it('refuses a tampered session cookie', async () => {
		const browser = new Browser();
		await browser.signIn();
		const [body, sig] = browser.cookies.get('__Host-portal')!.split('.');
		const forged = btoa(JSON.stringify({ sub: 7, name: 'Reviewer', exp: 9999999999 })).replace(/=+$/, '');
		browser.cookies.set('__Host-portal', `${forged}.${sig}`);
		expect(body).not.toBe(forged);
		expect((await browser.fetch('/account/keys')).status).toBe(303);
	});
});

describe('safeReturn', () => {
	it('allows only paths on this site', () => {
		expect(safeReturn('/account/keys/abc/listing')).toBe('/account/keys/abc/listing');
		expect(safeReturn('//evil.example/x')).toBe('/account/keys');
		expect(safeReturn('/\\evil.example')).toBe('/account/keys');
		expect(safeReturn('https://evil.example')).toBe('/account/keys');
		expect(safeReturn(undefined)).toBe('/account/keys');
	});
});
