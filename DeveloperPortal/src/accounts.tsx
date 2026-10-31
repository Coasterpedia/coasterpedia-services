// Accounts and usage, for reviewers: every account the API has seen, what
// each asked for, what each key says it's for, and two actions on a key:
// asking its owner about it, and revoking it with a reason (the API terms'
// grounds for revoking). Both email the owner.
import { Hono } from 'hono';
import { ago, type AppEnv, CONTACT, type Ctx, isoNow, message, page, requireSession, shortDate } from './app';
import { COMMERCIAL, type KeyRow, kindOf, LIGHT_KINDS, LIGHT_PER_DAY, platformLabel, platformsOf } from './data';
import { apiAccount, type KeyEntry } from './env';
import { removeListing } from './listings';
import { sendMail } from './mail';
import { reviewersOnly } from './review';
import { accountDays, accountRoutes, accountsUsage, type KeyRouteRow, type UsageRow } from './usage';
import { WIKI } from './views/layout';

export const accounts = new Hono<AppEnv>();
accounts.use('*', requireSession, reviewersOnly);

const LIST_DAYS = 7;
const DETAIL_DAYS = 14;
const ACCOUNT = /^[A-Za-z0-9_.-]{1,64}$/;

interface AccountRow {
	id: number;
	username: string;
	email: string;
	tier: string;
	created_at: string;
	signed_in_at: string;
	/** A sitewide wiki block, from the daily run (blocks.ts). */
	blocked_at: string | null;
	block_reason: string | null;
	block_expiry: string | null;
	/** From list=users, kept by the daily run (blocks.ts). */
	wiki_edits: number | null;
	wiki_registered: string | null;
	keys: number;
	/** Active keys whose kind should make few requests (LIGHT_KINDS). */
	light_keys: number;
	/** Active keys that say the project makes money. */
	commercial_keys: number;
	last_used_at: string | null;
}

const MONTH = new Intl.DateTimeFormat('en-GB', { month: 'short', year: 'numeric', timeZone: 'UTC' });

/** "1,204 wiki edits since Mar 2019"; the date is missing for the oldest accounts. */
function wikiSummary(a: Pick<AccountRow, 'wiki_edits' | 'wiki_registered'>): string | null {
	if (a.wiki_edits === null) return null;
	const edits = `${count(a.wiki_edits)} wiki edit${a.wiki_edits === 1 ? '' : 's'}`;
	return a.wiki_registered ? `${edits} since ${MONTH.format(new Date(a.wiki_registered))}` : edits;
}

/** "until 2 Nov 2026", "with no end". */
function blockEnd(expiry: string | null): string {
	return !expiry || expiry === 'infinite' || Number.isNaN(Date.parse(expiry)) ? 'with no end' : `until ${shortDate(expiry)}`;
}

type KeyDetail = KeyRow & { revoked_by: number | null; revoked_reason: string | null };

/** What the owner of a light key told us, for the "worth a look" line. */
const LIGHT_SAYS: Record<string, string> = {
	personal: 'says it’s only for their own use',
	trying: 'says they’re trying the API out',
};

/** Why a reviewer might look at this key: what it says against what it does. */
function worthALook(key: KeyDetail, routes: KeyRouteRow[], tier: string, days: number): string[] {
	const mine = routes.filter((r) => r.prefix === key.prefix);
	const requests = mine.reduce((sum, r) => sum + r.requests, 0);
	const notes: string[] = [];
	if (key.kind && LIGHT_KINDS.includes(key.kind)) {
		if (requests > LIGHT_PER_DAY * days) {
			notes.push(`The owner ${LIGHT_SAYS[key.kind]}, and it made ${count(requests)} requests in ${days} days, about ${count(Math.round(requests / days))} a day.`);
		}
		if (mine.some((r) => r.route === '/v1/changes')) {
			notes.push(`The owner ${LIGHT_SAYS[key.kind]}, and it reads the change feed, which is for keeping a copy of the data up to date.`);
		}
	}
	if (key.commercial === 'yes' && tier === 'free') notes.push('The owner says it makes money, and the account is on the free tier.');
	return notes;
}

/** What the reviewer sends unless they change it. */
const askText = (name: string) =>
	`We check how a few keys are used, as the API terms say we may. Could you tell us a bit more about “${name}”? What does it do, who uses it, and where can we see it, if it’s public?`;

interface Totals {
	requests: number;
	today: number;
	limited: number;
	other: number;
	last: string | null;
}

const utcDay = (at = new Date()) => at.toISOString().slice(0, 10);
const count = (n: number) => n.toLocaleString('en-GB');

function totalsOf(rows: UsageRow[], today = utcDay()): Totals {
	const totals: Totals = { requests: 0, today: 0, limited: 0, other: 0, last: null };
	for (const r of rows) {
		totals.requests += r.requests;
		if (r.day === today) totals.today += r.requests;
		if (r.status === '429') totals.limited += r.requests;
		else if (r.status !== '200') totals.other += r.requests;
		if (!totals.last || r.last > totals.last) totals.last = r.last;
	}
	return totals;
}

/** What an account the portal doesn't hold is. */
function outsideLabel(account: string): string {
	return account === 'browser' ? 'The Attractions Browser’s sessions' : 'A key made by hand in the closed beta';
}

const usageMissing = <p class="notice notice--error" role="alert">We couldn’t read usage from Analytics Engine, so the counts are missing. The error is in the Worker’s log.</p>;

accounts.get('/', async (c) => {
	const { results: held } = await c.env.ACCOUNTS.prepare(
		`SELECT a.*,
			(SELECT COUNT(*) FROM keys k WHERE k.account_id = a.id AND k.revoked_at IS NULL) AS keys,
			(SELECT COUNT(*) FROM keys k WHERE k.account_id = a.id AND k.revoked_at IS NULL AND k.kind IN (${LIGHT_KINDS.map(() => '?').join(',')})) AS light_keys,
			(SELECT COUNT(*) FROM keys k WHERE k.account_id = a.id AND k.revoked_at IS NULL AND k.commercial = 'yes') AS commercial_keys,
			(SELECT MAX(last_used_at) FROM keys k WHERE k.account_id = a.id) AS last_used_at
		FROM accounts a`,
	).bind(...LIGHT_KINDS).all<AccountRow>();

	let usage: UsageRow[] | null = null;
	try {
		usage = await accountsUsage(c.env, LIST_DAYS);
	} catch (e) {
		console.error(`accounts usage: ${(e as Error).message}`);
	}
	const byAccount = new Map<string, UsageRow[]>();
	for (const r of usage ?? []) byAccount.set(r.account, [...(byAccount.get(r.account) ?? []), r]);

	const rows = [
		...held.map((a) => ({
			account: apiAccount(a.id), name: a.username, note: [a.email, wikiSummary(a)].filter(Boolean).join(' · '),
			keys: a.keys as number | null, lastKnown: a.last_used_at, blocked: !!a.blocked_at,
			// Every active key says it's light, so the account's total is theirs.
			allLight: a.keys > 0 && a.light_keys === a.keys,
			commercial: a.commercial_keys > 0 && a.tier === 'free',
		})),
		...[...byAccount.keys()].filter((acc) => !/^u\d+$/.test(acc)).map((acc) => ({
			account: acc, name: acc, note: outsideLabel(acc), keys: null, lastKnown: null, blocked: false, allLight: false, commercial: false,
		})),
	].map((r) => ({ ...r, totals: totalsOf(byAccount.get(r.account) ?? []) }));
	rows.sort((a, b) => b.totals.requests - a.totals.requests || a.name.localeCompare(b.name));
	const now = Date.now();

	return page(c, {
		title: 'Accounts',
		section: 'review',
		children: (
			<div class="wrap page">
				<a href="/review" class="meta">← Review</a>
				<div class="page__intro">
					<h1>Accounts</h1>
					<p>Every account that has signed in, and every account the API has answered in the last {LIST_DAYS} days. Counts are UTC days, from Analytics Engine, which samples at high volume, so treat them as close rather than exact.</p>
				</div>
				{!usage && usageMissing}
				{rows.length === 0 ? <p>Nobody has signed in, and the API has answered no keys in {LIST_DAYS} days.</p> : (
					<div class="table-scroll">
						<table class="data-table">
							<thead>
								<tr>
									<th scope="col">Account</th>
									<th scope="col" class="num">Active keys</th>
									<th scope="col" class="num">Today</th>
									<th scope="col" class="num">{LIST_DAYS} days</th>
									<th scope="col" class="num">Rate-limited</th>
									<th scope="col" class="num">Other non-200</th>
									<th scope="col">Last request</th>
								</tr>
							</thead>
							<tbody>
								{rows.map((r) => {
									const last = r.totals.last ?? r.lastKnown;
									return (
										<tr>
											<td>
												<a href={`/review/accounts/${encodeURIComponent(r.account)}`}>{r.name}</a>
												{r.blocked && <> <span class="pill">Blocked on the wiki</span></>}
												{r.commercial && <> <span class="pill">Says it makes money</span></>}
												{r.allLight && r.totals.requests > LIGHT_PER_DAY * LIST_DAYS && <> <span class="pill">Busy for what it says it is</span></>}
												<div class="meta">{r.note}</div>
											</td>
											<td class="num">{r.keys ?? '–'}</td>
											<td class="num">{count(r.totals.today)}</td>
											<td class="num">{count(r.totals.requests)}</td>
											<td class="num">{count(r.totals.limited)}</td>
											<td class="num">{count(r.totals.other)}</td>
											<td>{last ? ago(last, now) : 'never'}</td>
										</tr>
									);
								})}
							</tbody>
						</table>
					</div>
				)}
			</div>
		),
	});
});

function statusList(byStatus: Map<string, number>): string {
	return [...byStatus].filter(([s]) => s !== '200').sort(([a], [b]) => a.localeCompare(b)).map(([s, n]) => `${s} × ${count(n)}`).join(', ');
}

function ProjectLine({ k }: { k: KeyDetail }) {
	const kind = kindOf(k.kind);
	if (!kind) return <div class="meta">No project details yet.</div>;
	const commercial = COMMERCIAL.find((c) => c.slug === k.commercial)?.label;
	const platforms = platformsOf(k.platforms).map(platformLabel).join(', ');
	return (
		<div class="meta">
			{kind.short}
			{platforms && ` · ${platforms}`}
			{' · '}
			{k.link ? <a href={k.link} rel="noopener noreferrer nofollow">{k.link}</a> : kind.public ? 'not public yet' : 'no link'}
			{commercial && <> · makes money: {commercial.toLowerCase()}</>}
		</div>
	);
}

function KeyRows({ keys, routes, account, tier }: { keys: KeyDetail[]; routes: KeyRouteRow[] | null; account: string; tier: string }) {
	const requests = new Map<string, number>();
	for (const r of routes ?? []) requests.set(r.prefix, (requests.get(r.prefix) ?? 0) + r.requests);
	return (
		<div class="table-scroll">
			<table class="data-table">
				<thead>
					<tr>
						<th scope="col">Key</th>
						<th scope="col">Created</th>
						<th scope="col">Last used</th>
						<th scope="col" class="num">{DETAIL_DAYS} days</th>
						<th scope="col">State</th>
					</tr>
				</thead>
				<tbody>
					{keys.map((k) => (
						<tr>
							<td>
								<strong>{k.name}</strong> <code>{k.prefix}…</code>
								<div class="meta">{k.description}</div>
								<ProjectLine k={k} />
								{!k.revoked_at && routes && worthALook(k, routes, tier, DETAIL_DAYS).map((note) => (
									<p class="look"><span class="pill">Worth a look</span> {note}</p>
								))}
							</td>
							<td>{shortDate(k.created_at)}</td>
							<td>{k.last_used_at ? shortDate(k.last_used_at) : 'never'}</td>
							<td class="num">{routes ? count(requests.get(k.prefix) ?? 0) : '–'}</td>
							<td>
								{k.revoked_at ? (
									<>
										Revoked {shortDate(k.revoked_at)}{k.revoked_by ? ' by a reviewer' : ' by its owner'}
										{k.revoked_reason && <div class="meta">“{k.revoked_reason}”</div>}
									</>
								) : (
									<>
										<details class="ask">
											<summary>{k.asked_at ? `Asked ${shortDate(k.asked_at)}. Ask again` : 'Ask the owner about it'}</summary>
											<form method="post" action={`/review/accounts/${account}/keys/${k.id}/ask`} class="revoke-form">
												<label class="field__label" for={`ask-${k.id}`}>Question, emailed to the owner</label>
												<textarea class="input" id={`ask-${k.id}`} name="question" maxlength={1000} rows={5} required>{askText(k.name)}</textarea>
												<div class="form__actions">
													<button type="submit" class="btn">Send</button>
												</div>
											</form>
										</details>
										<form method="post" action={`/review/accounts/${account}/keys/${k.id}/revoke`} class="revoke-form">
											<label class="field__label" for={`reason-${k.id}`}>Reason, emailed to the owner</label>
											<div class="revoke-form__row">
												<input class="input" id={`reason-${k.id}`} name="reason" maxlength={200} required />
												<button type="submit" class="btn btn--danger">Revoke</button>
											</div>
										</form>
									</>
								)}
							</td>
						</tr>
					))}
				</tbody>
			</table>
		</div>
	);
}

function DayRows({ days }: { days: UsageRow[] }) {
	const byDay = new Map<string, UsageRow[]>();
	for (const r of days) byDay.set(r.day, [...(byDay.get(r.day) ?? []), r]);
	const today = new Date();
	const list = Array.from({ length: DETAIL_DAYS }, (_, i) => utcDay(new Date(today.getTime() - i * 86400_000)));
	return (
		<div class="table-scroll">
			<table class="data-table">
				<thead>
					<tr>
						<th scope="col">Day (UTC)</th>
						<th scope="col" class="num">Requests</th>
						<th scope="col" class="num">Rate-limited</th>
						<th scope="col" class="num">Other non-200</th>
					</tr>
				</thead>
				<tbody>
					{list.map((day) => {
						const t = totalsOf(byDay.get(day) ?? [], day);
						return (
							<tr>
								<td>{shortDate(`${day}T00:00:00Z`)}</td>
								<td class="num">{count(t.requests)}</td>
								<td class="num">{count(t.limited)}</td>
								<td class="num">{count(t.other)}</td>
							</tr>
						);
					})}
				</tbody>
			</table>
		</div>
	);
}

function RouteRows({ routes }: { routes: KeyRouteRow[] }) {
	const byRoute = new Map<string, { prefix: string; route: string; requests: number; statuses: Map<string, number> }>();
	for (const r of routes) {
		const id = `${r.prefix} ${r.route}`;
		let entry = byRoute.get(id);
		if (!entry) byRoute.set(id, (entry = { prefix: r.prefix, route: r.route, requests: 0, statuses: new Map() }));
		entry.requests += r.requests;
		entry.statuses.set(r.status, (entry.statuses.get(r.status) ?? 0) + r.requests);
	}
	const rows = [...byRoute.values()].sort((a, b) => b.requests - a.requests);
	return (
		<div class="table-scroll">
			<table class="data-table">
				<thead>
					<tr>
						<th scope="col">Route</th>
						<th scope="col">Key</th>
						<th scope="col" class="num">Requests</th>
						<th scope="col">Not 200</th>
					</tr>
				</thead>
				<tbody>
					{rows.map((r) => (
						<tr>
							<td><code>{r.route}</code></td>
							<td><code>{r.prefix ? `${r.prefix}…` : '–'}</code></td>
							<td class="num">{count(r.requests)}</td>
							<td>{statusList(r.statuses) || 'none'}</td>
						</tr>
					))}
				</tbody>
			</table>
		</div>
	);
}

const DONE: Record<string, string> = {
	revoked: 'We’ve revoked the key and emailed its owner. Within a minute, the API will refuse any request that uses it.',
	asked: 'We’ve emailed the owner. Their answer will come to developer@coasterpedia.net.',
};

const notFound = (c: Ctx) =>
	message(c, 404, 'No such account', <p><a href="/review/accounts">Accounts</a></p>, 'review');

accounts.get('/:account', async (c) => {
	const account = c.req.param('account');
	if (!ACCOUNT.test(account)) return notFound(c);
	const userId = /^u(\d+)$/.exec(account)?.[1];

	let held: AccountRow | null = null;
	let keys: KeyDetail[] = [];
	if (userId) {
		held = await c.env.ACCOUNTS.prepare(`SELECT *, 0 AS keys, NULL AS last_used_at FROM accounts WHERE id = ?`).bind(Number(userId)).first<AccountRow>();
		if (!held) return notFound(c);
		keys = (await c.env.ACCOUNTS.prepare(`SELECT * FROM keys WHERE account_id = ? ORDER BY revoked_at IS NOT NULL, created_at`)
			.bind(Number(userId)).all<KeyDetail>()).results;
	}

	let days: UsageRow[] | null = null;
	let routes: KeyRouteRow[] | null = null;
	try {
		[days, routes] = await Promise.all([accountDays(c.env, account, DETAIL_DAYS), accountRoutes(c.env, account, DETAIL_DAYS)]);
	} catch (e) {
		console.error(`account usage: ${(e as Error).message}`);
	}
	if (!held && !days?.length) return notFound(c);

	const name = held?.username ?? account;
	const done = DONE[c.req.query('done') ?? ''];

	return page(c, {
		title: name,
		section: 'review',
		children: (
			<div class="wrap page">
				<a href="/review/accounts" class="meta">← Accounts</a>
				{done && <p class="notice" role="status">{done}</p>}
				<div class="page__intro">
					<h1>{name}</h1>
					{held ? (
						<dl class="facts">
							<dt>Wiki user</dt><dd><a href={`${WIKI}/wiki/Special:Contributions/${encodeURIComponent(held.username)}`}>{held.username}</a> (ID {held.id})</dd>
							<dt>Email</dt><dd><a href={`mailto:${held.email}`}>{held.email}</a></dd>
							<dt>On the wiki</dt><dd>{wikiSummary(held) ?? 'Not read yet. The daily run fills this in.'}</dd>
							<dt>Tier</dt><dd>{held.tier}</dd>
							<dt>Signed up</dt><dd>{shortDate(held.created_at)}</dd>
							<dt>Last signed in</dt><dd>{shortDate(held.signed_in_at)}</dd>
							{held.blocked_at && (
								<>
									<dt>Blocked</dt>
									<dd>
										<span class="pill">Blocked on the wiki</span> since {shortDate(held.blocked_at)} or earlier, {blockEnd(held.block_expiry)}.
										{held.block_reason && <> The block says “{held.block_reason}”.</>}
										{' '}It can’t create keys. Its existing keys work until you revoke them.
									</dd>
								</>
							)}
						</dl>
					) : (
						<p>{outsideLabel(account)}. The portal doesn’t hold its keys, so you can’t revoke them here. To revoke one, put its KV entry with <code>"status":"revoked"</code>.</p>
					)}
				</div>
				{!days && usageMissing}

				{held && (
					<section class="accounts__section">
						<h2>Keys</h2>
						{keys.length === 0 ? <p>No keys yet.</p> : <KeyRows keys={keys} routes={routes} account={account} tier={held.tier} />}
					</section>
				)}

				{days && (
					<section class="accounts__section">
						<h2>Requests by day</h2>
						<DayRows days={days} />
					</section>
				)}

				{routes && routes.length > 0 && (
					<section class="accounts__section">
						<h2>Requests by route, last {DETAIL_DAYS} days</h2>
						<RouteRows routes={routes} />
					</section>
				)}
			</div>
		),
	});
});

accounts.post('/:account/keys/:id/ask', async (c) => {
	const account = c.req.param('account');
	const userId = /^u(\d+)$/.exec(account)?.[1];
	if (!userId) return notFound(c);
	const back = `/review/accounts/${account}`;

	const question = String((await c.req.parseBody()).question ?? '').trim().slice(0, 1000);
	if (!question) return c.redirect(back, 303);

	const row = await c.env.ACCOUNTS.prepare(
		`SELECT k.id, k.name, k.prefix, a.email FROM keys k JOIN accounts a ON a.id = k.account_id WHERE k.id = ? AND k.account_id = ? AND k.revoked_at IS NULL`,
	).bind(c.req.param('id'), Number(userId)).first<{ id: string; name: string; prefix: string; email: string }>();
	if (!row) return c.redirect(back, 303);

	await c.env.ACCOUNTS.prepare(`UPDATE keys SET asked_at = ?, asked_by = ? WHERE id = ?`).bind(isoNow(), c.get('session').sub, row.id).run();
	c.executionCtx.waitUntil(sendMail(c.env, {
		to: [row.email],
		subject: `A question about your Coasterpedia API key “${row.name}”`,
		text: [
			question,
			'',
			`To answer, reply to this email. Asking doesn’t change anything about your key ${row.prefix}….`,
			'',
			`The API terms: ${c.env.PORTAL_ORIGIN}/terms/`,
		].join('\n'),
	}));
	return c.redirect(`${back}?done=asked`, 303);
});

accounts.post('/:account/keys/:id/revoke', async (c) => {
	const account = c.req.param('account');
	const userId = /^u(\d+)$/.exec(account)?.[1];
	if (!userId) return notFound(c);
	const back = `/review/accounts/${account}`;

	const reason = String((await c.req.parseBody()).reason ?? '').trim().slice(0, 200);
	if (!reason) return c.redirect(back, 303);

	const row = await c.env.ACCOUNTS.prepare(
		`SELECT k.*, a.email FROM keys k JOIN accounts a ON a.id = k.account_id WHERE k.id = ? AND k.account_id = ? AND k.revoked_at IS NULL`,
	).bind(c.req.param('id'), Number(userId)).first<KeyDetail & { hash: string; email: string }>();
	if (!row) return c.redirect(back, 303);

	// KV first, as on Your keys: the API must never serve a key the portal calls revoked.
	const entry = await c.env.KEYS.get<KeyEntry>(`key:${row.hash}`, 'json');
	await c.env.KEYS.put(`key:${row.hash}`, JSON.stringify({ ...(entry ?? { account, tier: 'free' }), status: 'revoked' }));
	await c.env.ACCOUNTS.prepare(`UPDATE keys SET revoked_at = ?, revoked_by = ?, revoked_reason = ? WHERE id = ?`)
		.bind(isoNow(), c.get('session').sub, reason, row.id).run();
	await removeListing(c.env, row.id);

	c.executionCtx.waitUntil(sendMail(c.env, {
		to: [row.email],
		subject: `We’ve revoked your Coasterpedia API key “${row.name}”`,
		text: [
			`We’ve revoked your API key ${row.prefix}… (${row.name}). Within a minute, the API will refuse any request that uses it.`,
			'',
			`The reason: ${reason}`,
			'',
			`The API terms say when we can revoke a key: ${c.env.PORTAL_ORIGIN}/terms/`,
			`If you think we’ve made a mistake, reply to this email or write to ${CONTACT}.`,
		].join('\n'),
	}));
	return c.redirect(`${back}?done=revoked`, 303);
});
