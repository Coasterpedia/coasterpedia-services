import { env } from 'cloudflare:workers';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { daily } from '../src/cron';
import type { KeyEntry } from '../src/env';
import { Browser, DEVELOPER, outbound, resetAccounts, setAeRows, setWikiBlocked, stubOutbound } from './helpers';

// Accounts and usage, for reviewers: the list, one account, and revoking.

const REVIEWER = { sub: 7, username: 'Reviewer', confirmed_email: true, email: 'reviewer@example.com' };
const today = new Date().toISOString().slice(0, 10);

beforeEach(async () => {
	await resetAccounts();
	stubOutbound();
	setAeRows([]);
});
afterEach(() => vi.restoreAllMocks());

async function developerWithKey() {
	const developer = new Browser();
	await developer.signIn(DEVELOPER);
	const res = await developer.post('/account/keys', { name: 'Credit log', description: 'Log every coaster you have ridden.', terms: '1' });
	const key = /<code id="new-key">(cpk_[A-Za-z0-9]+)<\/code>/.exec(await res.text())![1];
	const row = (await env.ACCOUNTS.prepare('SELECT id, hash FROM keys').first<{ id: string; hash: string }>())!;
	return { developer, key, ...row };
}

async function reviewer() {
	const browser = new Browser();
	await browser.signIn(REVIEWER);
	return browser;
}

describe('accounts', () => {
	it('is for reviewers only', async () => {
		const { developer } = await developerWithKey();
		expect((await developer.fetch('/review/accounts')).status).toBe(403);
		expect((await developer.fetch('/review/accounts/u42')).status).toBe(403);
	});

	it('lists portal accounts and hand-made keys, with their usage', async () => {
		await developerWithKey();
		setAeRows([{
			match: 'GROUP BY account, day, status',
			data: [
				{ account: 'u42', day: `${today} 00:00:00`, status: '200', requests: '120', last: `${today} 09:00:00` },
				{ account: 'u42', day: `${today} 00:00:00`, status: '429', requests: '3', last: `${today} 09:00:00` },
				{ account: 'Exxodus', day: `${today} 00:00:00`, status: '200', requests: '5000', last: `${today} 10:00:00` },
			],
		}]);
		const html = await (await (await reviewer()).fetch('/review/accounts')).text();
		expect(html).toContain('href="/review/accounts/u42"');
		expect(html).toContain('Developer');
		expect(html).toContain('dev@example.com');
		expect(html).toContain('href="/review/accounts/Exxodus"');
		expect(html).toContain('A key made by hand in the closed beta');
		// Busiest first.
		expect(html.indexOf('Exxodus')).toBeLessThan(html.indexOf('Developer'));
		expect(html).toContain('5,000');
	});

	it('still lists accounts when usage can’t be read', async () => {
		await developerWithKey();
		vi.spyOn(console, 'error').mockImplementation(() => {});
		const configured = { ...env, ANALYTICS_TOKEN: undefined };
		const browser = await reviewer();
		const res = await browser.fetch('/review/accounts', {}, configured);
		const html = await res.text();
		expect(res.status).toBe(200);
		expect(html).toContain('couldn’t read usage');
		expect(html).toContain('Developer');
	});

	it('shows one account’s keys, days and routes', async () => {
		const { key } = await developerWithKey();
		setAeRows([
			{ match: 'GROUP BY day, status', data: [{ day: `${today} 00:00:00`, status: '200', requests: '40', last: `${today} 09:00:00` }] },
			{ match: 'GROUP BY prefix, route, status', data: [
				{ prefix: key.slice(0, 8), route: '/v1/attractions', status: '200', requests: '38' },
				{ prefix: key.slice(0, 8), route: '/v1/attractions', status: '404', requests: '2' },
			] },
		]);
		const html = await (await (await reviewer()).fetch('/review/accounts/u42')).text();
		expect(html).toContain('Credit log');
		expect(html).toContain(`${key.slice(0, 8)}…`);
		expect(html).not.toContain(key);
		expect(html).toContain('/v1/attractions');
		expect(html).toContain('404 × 2');
	});

	it('is a 404 for an account nobody has seen', async () => {
		const browser = await reviewer();
		expect((await browser.fetch('/review/accounts/u999')).status).toBe(404);
		expect((await browser.fetch('/review/accounts/nobody')).status).toBe(404);
		expect((await browser.fetch(`/review/accounts/${encodeURIComponent("x' OR 1=1")}`)).status).toBe(404);
	});

	it('revokes a key with a reason, and records who and why', async () => {
		const { id, hash } = await developerWithKey();
		const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
		const browser = await reviewer();

		const empty = await browser.post(`/review/accounts/u42/keys/${id}/revoke`, { reason: ' ' });
		expect(empty.status).toBe(303);
		expect((await env.KEYS.get<KeyEntry>(`key:${hash}`, 'json'))!.status).toBe('active');

		const res = await browser.post(`/review/accounts/u42/keys/${id}/revoke`, { reason: 'Supplies the whole dataset to another site.' });
		expect(res.headers.get('location')).toBe('/review/accounts/u42?done=revoked');
		expect((await env.KEYS.get<KeyEntry>(`key:${hash}`, 'json'))!.status).toBe('revoked');
		const row = (await env.ACCOUNTS.prepare('SELECT * FROM keys WHERE id = ?').bind(id).first<Record<string, unknown>>())!;
		expect(row.revoked_at).toBeTruthy();
		expect(row.revoked_by).toBe(7);
		expect(row.revoked_reason).toBe('Supplies the whole dataset to another site.');
		// SES isn't configured in tests, so the mail is logged instead.
		expect(warn).toHaveBeenCalledWith(expect.stringContaining('We’ve revoked your Coasterpedia API key “Credit log”'));

		const html = await (await browser.fetch('/review/accounts/u42')).text();
		expect(html).toContain('by a reviewer');
		expect(html).toContain('Supplies the whole dataset to another site.');
	});

	it('won’t revoke another account’s key through this one', async () => {
		const { id, hash } = await developerWithKey();
		const browser = await reviewer();
		await browser.post(`/review/accounts/u7/keys/${id}/revoke`, { reason: 'Wrong account.' });
		expect((await env.KEYS.get<KeyEntry>(`key:${hash}`, 'json'))!.status).toBe('active');
	});
});

describe('wiki blocks', () => {
	const blockedAt = async (id = 42) =>
		(await env.ACCOUNTS.prepare('SELECT blocked_at, block_reason, block_expiry FROM accounts WHERE id = ?').bind(id).first<Record<string, string | null>>())!;
	const quiet = () => vi.spyOn(console, 'log').mockImplementation(() => {});

	it('records a block in the daily run and clears it when it ends', async () => {
		await developerWithKey();
		quiet();
		setWikiBlocked(new Set([42]));
		await daily(env);
		const first = await blockedAt();
		expect(first.blocked_at).toBeTruthy();
		expect(first.block_reason).toBe('Vandalism');
		expect(first.block_expiry).toBe('infinite');

		// The first sighting stays.
		await daily(env);
		expect((await blockedAt()).blocked_at).toBe(first.blocked_at);

		setWikiBlocked(new Set());
		await daily(env);
		expect(await blockedAt()).toEqual({ blocked_at: null, block_reason: null, block_expiry: null });
	});

	it('names itself to the wiki', async () => {
		await developerWithKey();
		quiet();
		await daily(env);
		const call = outbound.find((o) => o.url.includes('/w/api.php?'))!;
		expect(new Headers(call.init!.headers).get('user-agent')).toMatch(/^CoasterpediaDeveloperPortal\//);
	});

	it('keeps a recorded block when the wiki can’t answer', async () => {
		await developerWithKey();
		quiet();
		vi.spyOn(console, 'error').mockImplementation(() => {});
		setWikiBlocked(new Set([42]));
		await daily(env);
		setWikiBlocked(null);
		await daily(env);
		expect((await blockedAt()).blocked_at).toBeTruthy();
	});

	it('refuses a new key while the wiki says the account is blocked, and leaves its keys alone', async () => {
		const { developer, hash } = await developerWithKey();
		setWikiBlocked(new Set([42]));
		const res = await developer.post('/account/keys', { name: 'Second', description: 'Another one.', terms: '1' });
		expect(res.status).toBe(403);
		expect(await res.text()).toContain('Blocked accounts can’t create keys');
		expect((await env.ACCOUNTS.prepare('SELECT COUNT(*) AS n FROM keys').first<{ n: number }>())!.n).toBe(1);
		expect((await env.KEYS.get<KeyEntry>(`key:${hash}`, 'json'))!.status).toBe('active');
		// Recorded on the way, so the form refuses too.
		expect((await developer.fetch('/account/keys/new')).status).toBe(403);
	});

	it('lets a key be created when the block has ended, though the record is a day old', async () => {
		const { developer } = await developerWithKey();
		quiet();
		setWikiBlocked(new Set([42]));
		await daily(env);
		setWikiBlocked(new Set());
		const res = await developer.post('/account/keys', { name: 'Second', description: 'Another one.', terms: '1' });
		expect(res.status).toBe(200);
		expect((await blockedAt()).blocked_at).toBeNull();
	});

	it('goes by the record when the wiki can’t answer', async () => {
		const { developer } = await developerWithKey();
		vi.spyOn(console, 'warn').mockImplementation(() => {});
		setWikiBlocked(null);
		const res = await developer.post('/account/keys', { name: 'Second', description: 'Another one.', terms: '1' });
		expect(res.status).toBe(200);
	});

	it('shows a block on the accounts page', async () => {
		await developerWithKey();
		quiet();
		setWikiBlocked(new Set([42]));
		await daily(env);
		const browser = await reviewer();
		expect(await (await browser.fetch('/review/accounts')).text()).toContain('Blocked on the wiki');
		const html = await (await browser.fetch('/review/accounts/u42')).text();
		expect(html).toContain('with no end');
		expect(html).toContain('“Vandalism”');
		expect(html).toContain('Its existing keys work until you revoke them.');
	});
});
