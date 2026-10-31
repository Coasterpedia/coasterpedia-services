// Your keys (DR-010 §7.2 steps 5–7, §13.3): create, list, revoke, and the
// project details each key carries (project.tsx).
import { Hono } from 'hono';
import { ago, type AppEnv, ClosedBeta, CONTACT, type Ctx, isoNow, message, page, requireSession, shortDate } from './app';
import { isBlocked } from './blocks';
import { b64url, randomToken } from './cookies';
import { FIX, HIDE_AFTER_DAYS, isVisible, type KeyRow, kindOf, type ListingRow, MAX_KEYS, ownKey, REQUESTS_PER_MINUTE, usesOf } from './data';
import { apiAccount, type KeyEntry } from './env';
import { removeListing } from './listings';
import { reviewerAddresses, sendMail } from './mail';
import { emptyProject, type Project, ProjectFields, projectOf, readProject } from './project';
import { TERMS_VERSION } from './terms-version';
import { liveUsage, type LiveUsage } from './usage';

export const keys = new Hono<AppEnv>();
keys.use('*', requireSession);

const ALPHABET = '0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz';

/** cpk_ and 32 random bytes in base62, as scripts/create-key.mjs makes them. */
export function newKey(): string {
	let n = BigInt('0x' + [...crypto.getRandomValues(new Uint8Array(32))].map((b) => b.toString(16).padStart(2, '0')).join(''));
	let body = '';
	while (n > 0n) {
		body = ALPHABET[Number(n % 62n)] + body;
		n /= 62n;
	}
	return 'cpk_' + body.padStart(43, '0');
}

export async function sha256Hex(text: string): Promise<string> {
	const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(text));
	return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, '0')).join('');
}

type Row = KeyRow & { listing_state: ListingRow['state'] | null; listing_pending: string | null; listing_note: string | null; listing_submitted: string | null; listing_listed: string | null };

function ShowcaseCell({ row, now }: { row: Row; now: number }) {
	const listingUrl = `/account/keys/${row.id}/listing`;
	const withdraw = (label: string) => (
		<form method="post" action={`${listingUrl}/withdraw`}>
			<button type="submit" class="link-button">{label}</button>
		</form>
	);
	let pill = 'Not listed';
	let note: string | undefined;
	let action = <a href={listingUrl}>List in showcase</a>;

	if (row.listing_state === 'review') {
		pill = 'In review';
		note = `Sent ${shortDate(row.listing_submitted ?? isoNow())}. We’re checking that it works and credits Coasterpedia.`;
		action = withdraw('Withdraw');
	} else if (row.listing_state === 'listed') {
		pill = 'Listed';
		const uses = usesOf(row);
		if (!isVisible(row.last_used_at, row.listing_listed, now)) {
			note = `We’ve hidden it from the showcase, because this key hasn’t made a request for ${HIDE_AFTER_DAYS} days. We’ll show it again after the next one.`;
		} else if (row.listing_pending) {
			note = 'We’re reviewing your edit. The listed version stays up until we’ve finished.';
		} else if (row.listing_note) {
			note = `We didn’t accept your edit. The reviewer said “${row.listing_note}”`;
		} else if (uses.length) {
			note = `Uses ${uses.join(' · ')}`;
		}
		action = row.listing_pending ? withdraw('Withdraw the edit') : <a href={listingUrl}>Edit listing</a>;
	} else if (row.listing_state === 'rejected') {
		note = row.listing_note ? `“${row.listing_note}”` : undefined;
		action = <a href={listingUrl}>Edit and send again</a>;
	} else if (row.listing_state === 'draft') {
		action = <a href={listingUrl}>Send for review</a>;
	}

	return (
		<div class="keys__showcase">
			<span class="pill">{pill}</span>
			{note && <span class="meta">{note}</span>}
			{action}
		</div>
	);
}

/** No kind, or a public one with no platform (a 0003 "app" key, converted by 0004). */
const detailsMissing = (row: KeyRow) => !row.kind || (kindOf(row.kind)?.public === true && !row.platforms);

const FLASH: Record<string, string> = {
	sent: 'Thanks. We’ll review it and email you when we’re done.',
	withdrawn: 'We’ve withdrawn it.',
	revoked: 'We’ve revoked the key. Within a minute, the API will refuse any request that uses it.',
	details: 'We’ve saved the project details.',
	commercial: `Thanks for telling us. We’ll email you about a paid plan. If you have questions first, write to ${CONTACT}.`,
};

keys.get('/', async (c) => {
	const session = c.get('session');
	const { results } = await c.env.ACCOUNTS.prepare(
		`SELECT k.*, l.state AS listing_state, l.pending AS listing_pending, l.note AS listing_note,
			l.submitted_at AS listing_submitted, l.listed_at AS listing_listed
		FROM keys k LEFT JOIN listings l ON l.key_id = k.id
		WHERE k.account_id = ? AND k.revoked_at IS NULL ORDER BY k.created_at`,
	).bind(session.sub).all<Row>();

	let live: LiveUsage | null = null;
	try {
		live = await liveUsage(c.env, apiAccount(session.sub));
	} catch (e) {
		console.warn(`live usage unavailable: ${(e as Error).message}`);
	}
	const now = Date.now();
	const flash = FLASH[c.req.query('done') ?? ''];

	return page(c, {
		title: 'Your keys',
		section: 'keys',
		children: (
			<div class="wrap page">
				{flash && <p class="notice" role="status">{flash}</p>}
				<div class="page__head">
					<div class="page__intro">
						<h1>Your keys</h1>
						<p>
							Your keys share one limit: together, they can make {REQUESTS_PER_MINUTE} requests a minute to the API.
							{live && ` You’ve made ${live.today.toLocaleString('en-GB')} requests today (UTC).`}
						</p>
					</div>
					{results.length < MAX_KEYS && <a class="btn btn--primary" href="/account/keys/new">Create a key</a>}
				</div>

				{results.length === 0 ? (
					<p>You have no keys yet. To create one, you’ll need a name for your project, a line about what it does and a link if it’s public, and you’ll need to accept the API terms.</p>
				) : (
					<div class="keys" role="table" aria-label="Your keys">
						<div class="keys__row keys__row--head" role="row">
							<span role="columnheader">Project</span>
							<span role="columnheader">Key</span>
							<span role="columnheader">Showcase</span>
							<span role="columnheader"><span class="visually-hidden">Actions</span></span>
						</div>
						{results.map((row) => {
							const lastUsed = live?.lastUsed.get(row.prefix) ?? row.last_used_at;
							return (
								<div class="keys__row" role="row">
									<div role="cell" class="keys__project">
										<strong>{row.name}</strong>
										<span>{row.description}</span>
										<a href={`/account/keys/${row.id}/details`}>{detailsMissing(row) ? 'Add project details' : 'Project details'}</a>
									</div>
									<div role="cell" class="keys__key">
										<code>{row.prefix}…</code>
										<span class="meta">Created {shortDate(row.created_at)} · {lastUsed ? `used ${ago(lastUsed, now)}` : 'not used yet'}</span>
									</div>
									<div role="cell"><ShowcaseCell row={row} now={now} /></div>
									<div role="cell"><a class="btn btn--danger" href={`/account/keys/${row.id}/revoke`}>Revoke</a></div>
								</div>
							);
						})}
					</div>
				)}

				<p class="meta page__foot">
					We show a key once, when you create it. If you lose a key, revoke it and create another.
				</p>
			</div>
		),
	});
});

interface NewKeyProps {
	errors?: string[];
	name?: string;
	description?: string;
	project?: Project;
}

function Errors({ errors }: { errors?: string[] }) {
	if (!errors?.length) return null;
	if (errors.length === 1) return <p class="notice notice--error" role="alert">{errors[0]}</p>;
	return (
		<div class="notice notice--error" role="alert">
			<p>Fix these, then try again:</p>
			<ul>{errors.map((e) => <li>{e}</li>)}</ul>
		</div>
	);
}

function NewKeyForm({ errors, name = '', description = '', project = emptyProject }: NewKeyProps) {
	return (
		<form method="post" action="/account/keys" class="form">
			<Errors errors={errors} />
			<div class="field">
				<label class="field__label" for="name">Project name</label>
				<input class="input" id="name" name="name" maxlength={40} required value={name} />
			</div>
			<div class="field">
				<label class="field__label" for="description">What it does, in one line</label>
				<input class="input" id="description" name="description" maxlength={100} required value={description} />
				<p class="field__help">Up to 100 characters. We show it on Your keys, and in the showcase if you list the project.</p>
			</div>
			<ProjectFields project={project} />
			<label class="check">
				<input type="checkbox" name="terms" value={TERMS_VERSION ?? ''} required />
				<span>I accept the <a href="/terms/">API terms</a> (version {TERMS_VERSION}), including that free keys are for non-commercial projects.</span>
			</label>
			<div class="form__actions">
				<button type="submit" class="btn btn--primary">Create key</button>
				<a href="/account/keys">Cancel</a>
			</div>
		</form>
	);
}

function newKeyPage(c: Ctx, props: NewKeyProps, status = 200) {
	return page(c, {
		title: 'Create a key',
		section: 'keys',
		children: (
			<div class="wrap page page--narrow">
				<a href="/account/keys" class="meta">← Your keys</a>
				<h1>Create a key</h1>
				<NewKeyForm {...props} />
			</div>
		),
	}, status);
}

const termsClosed = (c: Ctx) =>
	message(c, 503, 'You can’t create keys yet', <><p>The API terms aren’t in force yet.</p><ClosedBeta /></>, 'keys');

const blockedNow = (c: Ctx) =>
	message(c, 403, 'Blocked accounts can’t create keys', <p>Your wiki account is blocked, so you can’t create keys while the block lasts. If you think the block is wrong, you can appeal it on the wiki.</p>, 'keys');

async function activeKeyCount(c: Ctx): Promise<number> {
	const row = await c.env.ACCOUNTS.prepare(`SELECT COUNT(*) AS n FROM keys WHERE account_id = ? AND revoked_at IS NULL`).bind(c.get('session').sub).first<{ n: number }>();
	return row?.n ?? 0;
}

keys.get('/new', async (c) => {
	if (!TERMS_VERSION) return termsClosed(c);
	// The form goes by the daily run's record; creating the key asks the wiki.
	const stored = await c.env.ACCOUNTS.prepare(`SELECT blocked_at FROM accounts WHERE id = ?`).bind(c.get('session').sub).first<{ blocked_at: string | null }>();
	if (stored?.blocked_at) return blockedNow(c);
	if ((await activeKeyCount(c)) >= MAX_KEYS) {
		return message(c, 409, `You already have ${MAX_KEYS} keys`, <p>To create another, revoke one you no longer use on <a href="/account/keys">Your keys</a>.</p>, 'keys');
	}
	return newKeyPage(c, {});
});

keys.post('/', async (c) => {
	if (!TERMS_VERSION) return termsClosed(c);
	const session = c.get('session');
	const form = await c.req.parseBody({ all: true });
	const name = String(form.name ?? '').trim();
	const description = String(form.description ?? '').trim();
	const { project, errors: projectErrors } = readProject(form);

	const errors: string[] = [];
	if (!name || name.length > 40) errors.push(FIX.name);
	if (!description || description.length > 100) errors.push(FIX.description);
	errors.push(...projectErrors);
	if (form.terms !== TERMS_VERSION) errors.push(FIX.terms);
	if (errors.length) return newKeyPage(c, { errors, name, description, project }, 400);
	if ((await activeKeyCount(c)) >= MAX_KEYS) return newKeyPage(c, { errors: [`You already have ${MAX_KEYS} keys. Revoke one you no longer use first.`], name, description, project }, 409);

	const account = await c.env.ACCOUNTS.prepare(`SELECT tier FROM accounts WHERE id = ?`).bind(session.sub).first<{ tier: string }>();
	if (!account) return c.redirect('/auth/sign-in?return=/account/keys/new', 303);
	// The terms: a commercial project doesn't use a free key, it asks us for a plan.
	if (project.commercial === 'yes' && account.tier === 'free') {
		return newKeyPage(c, {
			errors: [`Free keys are for projects that don’t make money. For this one, email ${CONTACT} and tell us what it is, and we’ll send you the terms and a price for a paid plan. If it won’t make money yet, choose “Not yet”.`],
			name, description, project,
		}, 400);
	}
	if (await isBlocked(c.env, session.sub)) return blockedNow(c);

	// Usage is recorded by prefix, so a prefix is unique within the account.
	let key = '';
	let prefix = '';
	for (let tries = 0; tries < 5; tries++) {
		key = newKey();
		prefix = key.slice(0, 8);
		const clash = await c.env.ACCOUNTS.prepare(`SELECT 1 FROM keys WHERE account_id = ? AND prefix = ?`).bind(session.sub, prefix).first();
		if (!clash) break;
	}
	const hash = await sha256Hex(key);
	const id = b64url(crypto.getRandomValues(new Uint8Array(12)));

	await c.env.ACCOUNTS.prepare(
		`INSERT INTO keys (id, account_id, hash, prefix, name, description, kind, platforms, link, commercial, terms_version, created_at)
		VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
	).bind(id, session.sub, hash, prefix, name, description, project.kind, platformsJson(project), project.link || null, project.commercial, TERMS_VERSION, isoNow()).run();
	const entry: KeyEntry = { account: apiAccount(session.sub), tier: account.tier, status: 'active', name };
	try {
		await c.env.KEYS.put(`key:${hash}`, JSON.stringify(entry));
	} catch (e) {
		await c.env.ACCOUNTS.prepare(`DELETE FROM keys WHERE id = ?`).bind(id).run();
		throw e;
	}

	return page(c, {
		title: 'Your new key',
		section: 'keys',
		children: (
			<div class="wrap page page--narrow">
				<h1>Your new key</h1>
				<p>Copy it now. This is the only time we’ll show it, because we only keep its first eight characters.</p>
				<div class="key-reveal">
					<code id="new-key">{key}</code>
					<button type="button" class="btn" data-copy="new-key" hidden>Copy</button>
				</div>
				<p>Send it in the <code>Authorization</code> header:</p>
				<pre class="code" data-command>{`curl -H "Authorization: Bearer ${key}" \\\n  "https://api.coasterpedia.net/v1/attractions?limit=1"`}</pre>
				<p>Keep it on your server. If you put it in a web page or an app, anyone who has the page or the app can read the key and use it. If you’re building an app, have the app call your own server, and have your server call the API. <a href="/docs/">Getting started</a> explains the rest.</p>
				<p><a class="btn btn--primary" href="/account/keys">Done</a></p>
			</div>
		),
	});
});

function detailsPage(c: Ctx, key: KeyRow, project: Project, errors: string[] = [], status = 200) {
	return page(c, {
		title: `Project details for ${key.name}`,
		section: 'keys',
		children: (
			<div class="wrap page page--narrow">
				<a href="/account/keys" class="meta">← Your keys</a>
				<h1>Project details</h1>
				<p>
					For <strong>{key.name}</strong>, the key <code>{key.prefix}…</code>. We use these to check that keys are used as the API terms describe.
					{' '}If the project changes, update them here. If it’s going to make money, tell us here before it does.
				</p>
				<form method="post" action={`/account/keys/${key.id}/details`} class="form">
					<Errors errors={errors} />
					<ProjectFields project={project} />
					<div class="form__actions">
						<button type="submit" class="btn btn--primary">Save</button>
						<a href="/account/keys">Cancel</a>
					</div>
				</form>
			</div>
		),
	}, status);
}

const platformsJson = (project: Project) => (project.platforms.length ? JSON.stringify(project.platforms) : null);

async function activeOwnKey(c: Ctx): Promise<KeyRow | null> {
	const key = await ownKey(c.env.ACCOUNTS, c.get('session').sub, c.req.param('id') ?? '');
	return key && !key.revoked_at ? key : null;
}

const noSuchKey = (c: Ctx) => message(c, 404, 'No such key', <p><a href="/account/keys">Your keys</a></p>, 'keys');

keys.get('/:id/details', async (c) => {
	const key = await activeOwnKey(c);
	return key ? detailsPage(c, key, projectOf(key)) : noSuchKey(c);
});

keys.post('/:id/details', async (c) => {
	const key = await activeOwnKey(c);
	if (!key) return noSuchKey(c);
	const { project, errors } = readProject(await c.req.parseBody({ all: true }));
	if (errors.length) return detailsPage(c, key, project, errors, 400);

	await c.env.ACCOUNTS.prepare(`UPDATE keys SET kind = ?, platforms = ?, link = ?, commercial = ? WHERE id = ?`)
		.bind(project.kind, platformsJson(project), project.link || null, project.commercial, key.id).run();

	// The terms ask a free project to tell us before it makes money. This is it telling us.
	const session = c.get('session');
	const account = await c.env.ACCOUNTS.prepare(`SELECT tier FROM accounts WHERE id = ?`).bind(session.sub).first<{ tier: string }>();
	if (project.commercial === 'yes' && key.commercial !== 'yes' && account?.tier === 'free') {
		c.executionCtx.waitUntil((async () => sendMail(c.env, {
			to: await reviewerAddresses(c.env),
			subject: `${key.name} says it makes money`,
			text: [
				`${session.name} changed the key ${key.prefix}… (${key.name}) to say the project makes money, or is about to.`,
				'It’s on the free tier, so it needs a paid plan. Email them about one.',
				'',
				`${c.env.PORTAL_ORIGIN}/review/accounts/u${session.sub}`,
			].join('\n'),
		}))());
		return c.redirect('/account/keys?done=commercial', 303);
	}
	return c.redirect('/account/keys?done=details', 303);
});

keys.get('/:id/revoke', async (c) => {
	const row = await c.env.ACCOUNTS.prepare(`SELECT * FROM keys WHERE id = ? AND account_id = ? AND revoked_at IS NULL`)
		.bind(c.req.param('id'), c.get('session').sub).first<KeyRow>();
	if (!row) return message(c, 404, 'No such key', <p><a href="/account/keys">Your keys</a></p>, 'keys');
	return page(c, {
		title: `Revoke ${row.name}`,
		section: 'keys',
		children: (
			<div class="wrap page page--narrow">
				<h1>Revoke {row.name}?</h1>
				<p>
					Within a minute, the API will refuse any request that uses <code>{row.prefix}…</code>. You can’t undo this.
					{' '}If the key has a showcase listing, we’ll remove it.
				</p>
				<form method="post" action={`/account/keys/${row.id}/revoke`} class="form__actions">
					<button type="submit" class="btn btn--danger-fill">Revoke the key</button>
					<a href="/account/keys">Keep it</a>
				</form>
			</div>
		),
	});
});

keys.post('/:id/revoke', async (c) => {
	const session = c.get('session');
	const row = await c.env.ACCOUNTS.prepare(`SELECT * FROM keys WHERE id = ? AND account_id = ? AND revoked_at IS NULL`)
		.bind(c.req.param('id'), session.sub).first<KeyRow & { hash: string }>();
	if (!row) return c.redirect('/account/keys', 303);

	// KV first: a revoked row with a live KV entry would be a key the portal
	// says is dead and the API still serves.
	const entry = await c.env.KEYS.get<KeyEntry>(`key:${row.hash}`, 'json');
	await c.env.KEYS.put(`key:${row.hash}`, JSON.stringify({ ...(entry ?? { account: apiAccount(session.sub), tier: 'free' }), status: 'revoked' }));
	await c.env.ACCOUNTS.prepare(`UPDATE keys SET revoked_at = ? WHERE id = ?`).bind(isoNow(), row.id).run();
	await removeListing(c.env, row.id);
	return c.redirect('/account/keys?done=revoked', 303);
});
