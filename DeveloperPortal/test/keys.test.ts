import { env } from 'cloudflare:workers';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { KeyEntry } from '../src/env';
import { newKey, sha256Hex } from '../src/keys';
import { Browser, resetAccounts, stubOutbound } from './helpers';

// DR-010 §7.2 steps 5–7, §13.3.


beforeEach(async () => {
	await resetAccounts();
	stubOutbound();
});
afterEach(() => vi.restoreAllMocks());

async function createKey(browser: Browser, name = 'Credit log', description = 'Log every coaster you have ridden.') {
	const res = await browser.post('/account/keys', { name, description, terms: '1' });
	expect(res.status).toBe(200);
	const html = await res.text();
	return { html, key: /<code id="new-key">(cpk_[A-Za-z0-9]+)<\/code>/.exec(html)![1] };
}

describe('keys', () => {
	it('sends a reader who is not signed in to sign in and back', async () => {
		const res = await new Browser().fetch('/account/keys/new');
		expect(res.status).toBe(303);
		expect(res.headers.get('location')).toBe('/auth/sign-in?return=%2Faccount%2Fkeys%2Fnew');
	});

	it('makes keys in the shape the API reads', () => {
		const key = newKey();
		expect(key).toMatch(/^cpk_[0-9A-Za-z]{43}$/);
	});

	it('shows a new key once, and stores only its hash and prefix', async () => {
		const browser = new Browser();
		await browser.signIn({ sub: 42, username: 'Developer', confirmed_email: true, email: 'dev@example.com' });
		const { html, key } = await createKey(browser);
		expect(html).toContain(key);

		const row = (await env.ACCOUNTS.prepare('SELECT * FROM keys').first<Record<string, string>>())!;
		expect(row.prefix).toBe(key.slice(0, 8));
		expect(row.hash).toBe(await sha256Hex(key));
		expect(row.terms_version).toBe('1');
		expect(JSON.stringify(row)).not.toContain(key);

		const entry = await env.KEYS.get<KeyEntry>(`key:${row.hash}`, 'json');
		expect(entry).toEqual({ account: 'u42', tier: 'free', status: 'active', name: 'Credit log' });

		const list = await (await browser.fetch('/account/keys')).text();
		expect(list).toContain('Credit log');
		expect(list).toContain(`${key.slice(0, 8)}…`);
		expect(list).not.toContain(key);
	});

	it('needs the terms accepted', async () => {
		const browser = new Browser();
		await browser.signIn();
		const res = await browser.post('/account/keys', { name: 'No terms', description: 'Nope' });
		expect(res.status).toBe(400);
		expect(await env.ACCOUNTS.prepare('SELECT COUNT(*) AS n FROM keys').first('n')).toBe(0);
	});

	it('stops at the account limit', async () => {
		const browser = new Browser();
		await browser.signIn();
		for (let i = 0; i < 10; i++) await createKey(browser, `Project ${i}`);
		const res = await browser.post('/account/keys', { name: 'Eleven', description: 'One too many', terms: '1' });
		expect(res.status).toBe(409);
	});

	it('revokes in KV and D1, and removes the listing', async () => {
		const browser = new Browser();
		await browser.signIn();
		await createKey(browser);
		const row = await env.ACCOUNTS.prepare('SELECT id, hash FROM keys').first<{ id: string; hash: string }>();
		await env.ACCOUNTS.prepare(
			`INSERT INTO listings (key_id, state, name, description, link, platform, credit_url) VALUES (?, 'listed', 'X', 'Y', 'https://x.example', 'web', 'https://x.example/about')`,
		).bind(row!.id).run();

		const confirm = await browser.fetch(`/account/keys/${row!.id}/revoke`);
		expect(await confirm.text()).toContain('Revoke Credit log?');
		const res = await browser.post(`/account/keys/${row!.id}/revoke`, {});
		expect(res.status).toBe(303);

		expect((await env.KEYS.get<KeyEntry>(`key:${row!.hash}`, 'json'))!.status).toBe('revoked');
		expect(await env.ACCOUNTS.prepare('SELECT revoked_at FROM keys').first('revoked_at')).not.toBeNull();
		expect(await env.ACCOUNTS.prepare('SELECT COUNT(*) AS n FROM listings').first('n')).toBe(0);
		expect(await (await browser.fetch('/account/keys')).text()).not.toContain('Credit log');
	});

	it('never lets one account touch another’s key', async () => {
		const owner = new Browser();
		await owner.signIn({ sub: 42, username: 'Owner', confirmed_email: true, email: 'o@example.com' });
		await createKey(owner);
		const id = await env.ACCOUNTS.prepare('SELECT id FROM keys').first<string>('id');

		const other = new Browser();
		await other.signIn({ sub: 99, username: 'Other', confirmed_email: true, email: 'x@example.com' });
		await other.post(`/account/keys/${id}/revoke`, {});
		expect(await env.ACCOUNTS.prepare('SELECT revoked_at FROM keys').first('revoked_at')).toBeNull();
		expect((await other.fetch(`/account/keys/${id}/listing`)).status).toBe(404);
	});

	it('shows today’s count and last use from Analytics Engine', async () => {
		const { setAeRows } = await import('./helpers');
		const browser = new Browser();
		await browser.signIn();
		const { key } = await createKey(browser);
		setAeRows([
			{ match: 'SUM(_sample_interval)', data: [{ requests: '1284' }] },
			{ match: 'MAX(timestamp)', data: [{ prefix: key.slice(0, 8), last: new Date(Date.now() - 120_000).toISOString().replace('T', ' ').slice(0, 19) }] },
		]);
		const html = await (await browser.fetch('/account/keys')).text();
		expect(html).toContain('You’ve made 1,284 requests today (UTC).');
		expect(html).toContain('used 2 minutes ago');
	});
});
