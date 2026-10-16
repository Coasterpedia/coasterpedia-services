import { env } from 'cloudflare:workers';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { daily } from '../src/cron';
import { FIX } from '../src/data';
import { Browser, outbound, resetAccounts, setAeRows, stubOutbound } from './helpers';

// DR-010 §13.4–13.5, §13.7, §13.10.


const REVIEWER = { sub: 7, username: 'Reviewer', confirmed_email: true, email: 'reviewer@example.com' };
const DEVELOPER = { sub: 42, username: 'Developer', confirmed_email: true, email: 'dev@example.com' };

const LISTING = {
	name: 'Statbot',
	description: 'Coaster stats for Discord servers.',
	link: 'https://statbot.example',
	platform: 'discord_bot',
	credit_url: 'https://statbot.example/about',
	show_maker: '1',
};

let developer: Browser;
let reviewer: Browser;
let keyId: string;

beforeEach(async () => {
	await resetAccounts();
	stubOutbound();
	reviewer = new Browser();
	await reviewer.signIn(REVIEWER);
	developer = new Browser();
	await developer.signIn(DEVELOPER);
	await developer.post('/account/keys', { name: 'Statbot', description: 'Coaster stats for Discord servers.', terms: '1' });
	keyId = (await env.ACCOUNTS.prepare('SELECT id FROM keys').first<string>('id'))!;
});
afterEach(() => vi.restoreAllMocks());

const sesMails = () => outbound.filter((o) => o.url.startsWith('https://email.')).map((o) => JSON.parse(String(o.init!.body)));

function form(fields: Record<string, string>) {
	const data = new FormData();
	for (const [k, v] of Object.entries(fields)) data.set(k, v);
	return data;
}

async function submit(fields = LISTING) {
	return developer.post(`/account/keys/${keyId}/listing`, form(fields));
}

describe('listing and review', () => {
	it('sends a listing for review and tells the reviewers', async () => {
		const res = await submit();
		expect(res.status).toBe(303);
		expect(res.headers.get('location')).toBe('/account/keys?done=sent');
		expect(await env.ACCOUNTS.prepare('SELECT state FROM listings').first('state')).toBe('review');

		const keys = await (await developer.fetch('/account/keys')).text();
		expect(keys).toContain('In review');

		const queue = await (await reviewer.fetch('/review')).text();
		expect(queue).toContain('Statbot');
		expect(queue).toContain('https://statbot.example/about');
	});

	it('refuses a listing without https links or a platform', async () => {
		const res = await submit({ ...LISTING, link: 'http://statbot.example', platform: 'fax' });
		expect(res.status).toBe(400);
		const html = await res.text();
		expect(html).toContain(FIX.link);
		expect(html).toContain(FIX.platform);
		expect(await env.ACCOUNTS.prepare('SELECT COUNT(*) AS n FROM listings').first('n')).toBe(0);
	});

	it('keeps the review queue to reviewers', async () => {
		expect((await developer.fetch('/review')).status).toBe(403);
		await submit();
		const res = await developer.post(`/review/${keyId}/approve`, {});
		expect(res.status).toBe(403);
		expect(await env.ACCOUNTS.prepare('SELECT state FROM listings').first('state')).toBe('review');
	});

	it('lists an approved project in the showcase and showcase.json', async () => {
		await submit();
		expect((await reviewer.post(`/review/${keyId}/approve`, {})).status).toBe(303);
		const row = await env.ACCOUNTS.prepare('SELECT state, listed_at, reviewed_by FROM listings').first<Record<string, unknown>>();
		expect(row).toMatchObject({ state: 'listed', reviewed_by: 7 });

		const page = await (await new Browser().fetch('/showcase')).text();
		expect(page).toContain('Statbot');
		expect(page).toContain('by <a href="https://coasterpedia.net/wiki/User:Developer">Developer</a>');

		const json = await (await new Browser().fetch('/showcase.json')).json<{ data: Record<string, unknown>[] }>();
		expect(json.data).toEqual([expect.objectContaining({ name: 'Statbot', platform: 'discord_bot', maker: 'Developer', url: 'https://statbot.example/' })]);
	});

	it('keeps the listed version up while an edit is reviewed, and keeps it when the edit is turned down', async () => {
		await submit();
		await reviewer.post(`/review/${keyId}/approve`, {});
		await submit({ ...LISTING, name: 'Statbot 2' });

		let page = await (await new Browser().fetch('/showcase')).text();
		expect(page).toContain('Statbot');
		expect(page).not.toContain('Statbot 2');
		expect(await (await developer.fetch('/account/keys')).text()).toContain('We’re reviewing your edit');

		await reviewer.post(`/review/${keyId}/reject`, { reason: 'The about page doesn’t credit Coasterpedia yet.' });
		const row = await env.ACCOUNTS.prepare('SELECT state, pending, note FROM listings').first<Record<string, unknown>>();
		expect(row).toEqual({ state: 'listed', pending: null, note: 'The about page doesn’t credit Coasterpedia yet.' });
		page = await (await new Browser().fetch('/showcase')).text();
		expect(page).toContain('Statbot');
	});

	it('applies an accepted edit', async () => {
		await submit();
		await reviewer.post(`/review/${keyId}/approve`, {});
		await submit({ ...LISTING, name: 'Statbot 2' });
		await reviewer.post(`/review/${keyId}/approve`, {});
		const page = await (await new Browser().fetch('/showcase')).text();
		expect(page).toContain('Statbot 2');
	});

	it('shows a rejection on the key’s row and takes the listing again', async () => {
		await submit();
		await reviewer.post(`/review/${keyId}/reject`, { reason: 'The link is a 404.' });
		expect(await (await developer.fetch('/account/keys')).text()).toContain('“The link is a 404.”');
		await submit();
		expect(await env.ACCOUNTS.prepare('SELECT state, note FROM listings').first()).toEqual({ state: 'review', note: null });
	});

	it('withdraws a listing from review', async () => {
		await submit();
		await developer.post(`/account/keys/${keyId}/listing/withdraw`, {});
		expect(await env.ACCOUNTS.prepare('SELECT state FROM listings').first('state')).toBe('draft');
		expect(await (await reviewer.fetch('/review')).text()).toContain('Nothing is waiting.');
	});

	it('hides a project after 90 days without a request, and shows it again on the next', async () => {
		await submit();
		await reviewer.post(`/review/${keyId}/approve`, {});
		const old = new Date(Date.now() - 91 * 86400_000).toISOString();
		await env.ACCOUNTS.prepare('UPDATE listings SET listed_at = ?').bind(old).run();
		await env.ACCOUNTS.prepare('UPDATE keys SET last_used_at = ?').bind(old).run();
		expect(await (await new Browser().fetch('/showcase')).text()).not.toContain('Statbot');

		await env.ACCOUNTS.prepare('UPDATE keys SET last_used_at = ?').bind(new Date().toISOString()).run();
		expect(await (await new Browser().fetch('/showcase')).text()).toContain('Statbot');
	});

	it('filters the showcase by platform', async () => {
		await submit();
		await reviewer.post(`/review/${keyId}/approve`, {});
		expect(await (await new Browser().fetch('/showcase?on=web')).text()).not.toContain('statbot.example');
		expect(await (await new Browser().fetch('/showcase?on=discord_bot')).text()).toContain('statbot.example');
	});
});

describe('the daily run', () => {
	it('rolls usage into each key, and keeps a last use older than Analytics Engine reaches', async () => {
		const prefix = (await env.ACCOUNTS.prepare('SELECT prefix FROM keys').first<string>('prefix'))!;
		setAeRows([
			{ match: 'blob3 AS route', data: [
				{ account: 'u42', prefix, route: '/v1/search' },
				{ account: 'u42', prefix, route: '/v1/attractions/{id}' },
				{ account: 'u42', prefix, route: '/v1/attractions/facets' },
				{ account: 'browser', prefix: 'cps_abcd', route: '/v1/attractions' },
			] },
			{ match: 'MAX(timestamp) AS last', data: [{ account: 'u42', prefix, last: '2026-10-15 21:04:00' }] },
		]);
		await daily(env, new Date('2026-10-16T04:45:00Z'));
		const row = (await env.ACCOUNTS.prepare('SELECT uses, last_used_at FROM keys').first<Record<string, string>>())!;
		expect(JSON.parse(row.uses)).toEqual(['attractions', 'search']);
		expect(row.last_used_at).toBe('2026-10-15T21:04:00Z');

		setAeRows([]);
		await daily(env, new Date('2026-10-17T04:45:00Z'));
		expect(await env.ACCOUNTS.prepare('SELECT last_used_at FROM keys').first('last_used_at')).toBe('2026-10-15T21:04:00Z');
	});

	it('asks the GraphQL API for yesterday, across every Worker', async () => {
		setAeRows([]);
		await daily(env, new Date('2026-10-16T04:45:00Z'));
		const graphql = outbound.find((o) => o.url.endsWith('/graphql'))!;
		const body = JSON.parse(String(graphql.init!.body));
		expect(body.variables).toEqual({ account: 'test-account', start: '2026-10-15T00:00:00Z', end: '2026-10-16T00:00:00.000Z' });
	});
});

describe('mail', () => {
	it('mails the reviewer on a submission and the developer on the outcome, once SES is configured', async () => {
		const configured = { ...env, SES_ACCESS_KEY_ID: 'AKIATEST', SES_SECRET_ACCESS_KEY: 'secret' };
		const { sendMail, reviewerAddresses } = await import('../src/mail');
		expect(await reviewerAddresses(configured)).toEqual(['reviewer@example.com']);
		await sendMail(configured, { to: ['dev@example.com'], subject: 'Statbot is in the showcase', text: 'Listed.' });
		const [mail] = sesMails();
		expect(mail).toMatchObject({
			FromEmailAddress: 'Coasterpedia for developers <developer@coasterpedia.net>',
			Destination: { ToAddresses: ['dev@example.com'] },
		});
		const signed = outbound.find((o) => o.url.startsWith('https://email.'))!;
		expect(signed.url).toBe('https://email.eu-west-2.amazonaws.com/v2/email/outbound-emails');
		expect(new Headers(signed.init!.headers).get('authorization')).toMatch(/^AWS4-HMAC-SHA256 Credential=AKIATEST\//);
	});

	it('sends nothing while SES is unconfigured, and never fails the request', async () => {
		const res = await submit();
		expect(res.status).toBe(303);
		expect(sesMails()).toHaveLength(0);
	});
});
