// Listing a key's project in the showcase (DR-010 §13.4–13.5). A listing
// hangs off a key; every new listing and every edit is reviewed, and an edit
// to a listed project waits in `pending` while the listed version stays up.
import { Hono } from 'hono';
import { type AppEnv, type Ctx, isoNow, message, page, requireSession } from './app';
import { randomToken } from './cookies';
import { FIX, type KeyRow, type ListingFields, type ListingRow, listingOf, ownKey, pendingOf, PLATFORMS, platformLabel, usesOf } from './data';
import type { Env } from './env';
import { reviewerAddresses, sendMail } from './mail';

export const listings = new Hono<AppEnv>();
listings.use('*', requireSession);

export const CREDIT_LINE = 'Ride data from <a href="https://coasterpedia.net">Coasterpedia</a>, CC BY-SA 4.0';

/** Screenshots: stored at one size, 16:10, whatever was uploaded (§13.5). */
export const SHOT = { width: 1280, height: 800, minWidth: 1200, minHeight: 750, maxBytes: 5 * 1024 * 1024 };

export const shotUrl = (name: string) => `/showcase/shots/${name}`;

/** Deletes a listing and its screenshots; revoking the key does this. */
export async function removeListing(env: Env, keyId: string): Promise<void> {
	const row = await listingOf(env.ACCOUNTS, keyId);
	if (!row) return;
	const shots = [row.screenshot, pendingOf(row)?.screenshot].filter((s): s is string => !!s);
	await env.ACCOUNTS.prepare(`DELETE FROM listings WHERE key_id = ?`).bind(keyId).run();
	if (shots.length) await env.SHOTS.delete(shots);
}

export function httpsUrl(value: string): string | null {
	try {
		const url = new URL(value);
		return url.protocol === 'https:' && value.length <= 300 ? url.toString() : null;
	} catch {
		return null;
	}
}

type Draft = Omit<ListingFields, 'screenshot'>;

interface FormProps {
	apiKey: KeyRow;
	fields: Draft;
	screenshot: string | null;
	errors: string[];
	listed: boolean;
	note: string | null;
}

function ListingForm({ apiKey: key, fields, screenshot, errors, listed, note }: FormProps) {
	const uses = usesOf(key);
	return (
		<div class="wrap page page--narrow">
			<a href="/account/keys" class="meta">← Your keys</a>
			<h1>{listed ? 'Edit the listing' : 'List in the showcase'}</h1>
			<p>
				This listing is for the key <code>{key.prefix}…</code>. We’ve filled in what we can from the key, and you can change it here.
				{listed && ' The listed version stays up while we review your edit.'}
			</p>
			{note && <p class="notice notice--error">We didn’t list this last time. The reviewer said “{note}”</p>}
			{errors.length > 0 && (
				<div class="notice notice--error" role="alert">
					<p>Fix these, then send it again:</p>
					<ul>{errors.map((e) => <li>{e}</li>)}</ul>
				</div>
			)}

			<form method="post" action={`/account/keys/${key.id}/listing`} enctype="multipart/form-data" class="form">
				<div class="field">
					<label class="field__label" for="name">Project name</label>
					<input class="input" id="name" name="name" maxlength={40} required value={fields.name} />
				</div>
				<div class="field">
					<label class="field__label" for="description">What it does, in one line</label>
					<input class="input" id="description" name="description" maxlength={100} required value={fields.description} />
					<p class="field__help">Up to 100 characters.</p>
				</div>
				<div class="field">
					<label class="field__label" for="link">Link</label>
					<input class="input" id="link" name="link" type="url" placeholder="https://" required value={fields.link} />
					<p class="field__help">Where someone can use it, such as the website, the app store page or a bot’s invite link.</p>
				</div>
				<fieldset class="field">
					<legend class="field__label">Platform</legend>
					<div class="t-segmented portal-segmented">
						{PLATFORMS.map((p) => (
							<label class="t-segmented__option">
								<input type="radio" name="platform" value={p.slug} checked={fields.platform === p.slug} required />
								{p.label}
							</label>
						))}
					</div>
				</fieldset>
				<div class="field">
					<label class="field__label" for="screenshot">Screenshot</label>
					{screenshot && <img class="listing-form__shot" src={shotUrl(screenshot)} alt="The screenshot sent last time" width={320} height={200} />}
					<input class="input input--file" id="screenshot" name="screenshot" type="file" accept="image/png,image/jpeg,image/webp" />
					<p class="field__help">
						A PNG, JPEG or WebP, at least {SHOT.minWidth} × {SHOT.minHeight} and up to 5 MB. We show it at 16 : 10, cropped from the centre.
						{screenshot ? ' Leave this empty to keep the one above.' : ' You can leave it out, and the card will show a placeholder instead.'}
					</p>
				</div>
				<div class="field field--ruled">
					<label class="field__label" for="credit_url">Where your project credits Coasterpedia</label>
					<p>Before we list a project, we check that it credits Coasterpedia where it shows the data. This line is enough:</p>
					<pre class="code">{CREDIT_LINE}</pre>
					<input class="input" id="credit_url" name="credit_url" type="url" placeholder="https://" required value={fields.credit_url} />
					<p class="field__help">Link to the page that shows the credit. For a bot, link to its help page or the docs that list the command that shows it.</p>
				</div>
				<div class="field field--ruled">
					<span class="field__label">What it uses</span>
					<p>{uses.length ? uses.join(' · ') : 'None yet. This key hasn’t made any requests in the last 30 days.'}</p>
					<p class="field__help">You don’t fill this in. We take it from this key’s requests over the last 30 days, so it stays up to date as your project changes.</p>
				</div>
				<label class="check">
					<input type="checkbox" name="show_maker" value="1" checked={fields.show_maker === 1} />
					<span>Show my wiki username as the maker</span>
				</label>
				<div class="form__actions">
					<button type="submit" class="btn btn--primary">Send for review</button>
					<a href="/account/keys">Cancel</a>
				</div>
				<p class="field__help">
					We check that it works and shows the credit, usually within a few days. If the key makes no requests for 90 days, we hide the project, and show it again after the next request.
				</p>
			</form>
		</div>
	);
}

async function form(c: Ctx, key: KeyRow, row: ListingRow | null, fields: Draft, errors: string[], status = 200) {
	const pending = row ? pendingOf(row) : null;
	const screenshot = pending?.screenshot ?? row?.screenshot ?? null;
	return page(c, {
		title: row?.state === 'listed' ? 'Edit the listing' : 'List in the showcase',
		section: 'keys',
		children: <ListingForm apiKey={key} fields={fields} screenshot={screenshot} errors={errors} listed={row?.state === 'listed'} note={row?.state === 'rejected' ? row.note : null} />,
	}, status);
}

async function activeKey(c: Ctx): Promise<KeyRow | null> {
	const key = await ownKey(c.env.ACCOUNTS, c.get('session').sub, c.req.param('id') ?? '');
	return key && !key.revoked_at ? key : null;
}

const noKey = (c: Ctx) => message(c, 404, 'No such key', <p><a href="/account/keys">Your keys</a></p>, 'keys');

listings.get('/', async (c) => {
	const key = await activeKey(c);
	if (!key) return noKey(c);
	const row = await listingOf(c.env.ACCOUNTS, key.id);
	const source = (row && pendingOf(row)) ?? row;
	const fields: Draft = source
		? { name: source.name, description: source.description, link: source.link, platform: source.platform, credit_url: source.credit_url, show_maker: source.show_maker }
		: { name: key.name, description: key.description, link: key.link ?? '', platform: '', credit_url: '', show_maker: 0 };
	return form(c, key, row, fields, []);
});

/** Re-encode to one fixed size, so the original is never kept (§13.5). */
async function storeScreenshot(env: Env, keyId: string, file: File): Promise<string | string[]> {
	if (file.size > SHOT.maxBytes) return ['Choose a screenshot of 5 MB or less.'];
	if (!['image/png', 'image/jpeg', 'image/webp'].includes(file.type)) return ['Choose a screenshot that’s a PNG, JPEG or WebP.'];
	if (!env.IMAGES) throw new Error('the IMAGES binding is missing');
	const info = await env.IMAGES.info(file.stream());
	if (!('width' in info)) return ['Choose a screenshot that’s a PNG, JPEG or WebP.'];
	if (info.width < SHOT.minWidth || info.height < SHOT.minHeight) {
		return [`Choose a screenshot at least ${SHOT.minWidth} × ${SHOT.minHeight}. This one is ${info.width} × ${info.height}.`];
	}
	const out = await env.IMAGES.input(file.stream())
		.transform({ width: SHOT.width, height: SHOT.height, fit: 'cover' })
		.output({ format: 'image/webp', quality: 85 });
	const name = `${keyId}-${randomToken(9)}.webp`;
	await env.SHOTS.put(name, out.image(), { httpMetadata: { contentType: 'image/webp' } });
	return name;
}

listings.post('/', async (c) => {
	const key = await activeKey(c);
	if (!key) return noKey(c);
	const session = c.get('session');
	const row = await listingOf(c.env.ACCOUNTS, key.id);
	const body = await c.req.parseBody();
	const text = (name: string) => String(body[name] ?? '').trim();

	const fields: Draft = {
		name: text('name'),
		description: text('description'),
		link: text('link'),
		platform: text('platform'),
		credit_url: text('credit_url'),
		show_maker: body.show_maker === '1' ? 1 : 0,
	};
	const errors: string[] = [];
	if (!fields.name || fields.name.length > 40) errors.push(FIX.name);
	if (!fields.description || fields.description.length > 100) errors.push(FIX.description);
	const link = httpsUrl(fields.link);
	if (!link) errors.push(FIX.link);
	const credit = httpsUrl(fields.credit_url);
	if (!credit) errors.push(FIX.credit);
	if (!PLATFORMS.some((p) => p.slug === fields.platform)) errors.push(FIX.platform);

	const pending = row ? pendingOf(row) : null;
	let screenshot = pending?.screenshot ?? row?.screenshot ?? null;
	const file = body.screenshot;
	if (errors.length === 0 && file instanceof File && file.size > 0) {
		const stored = await storeScreenshot(c.env, key.id, file);
		if (Array.isArray(stored)) errors.push(...stored);
		else {
			// A replaced screenshot that nothing listed shows any more goes.
			if (screenshot && screenshot !== row?.screenshot) await c.env.SHOTS.delete(screenshot);
			screenshot = stored;
		}
	}
	if (errors.length) return form(c, key, row, fields, errors, 400);

	const listing: ListingFields = { ...fields, link: link!, credit_url: credit!, screenshot };
	const now = isoNow();
	if (row?.state === 'listed') {
		await c.env.ACCOUNTS.prepare(`UPDATE listings SET pending = ?, note = NULL, submitted_at = ? WHERE key_id = ?`)
			.bind(JSON.stringify(listing), now, key.id).run();
	} else {
		await c.env.ACCOUNTS.prepare(
			`INSERT INTO listings (key_id, state, name, description, link, platform, credit_url, show_maker, screenshot, submitted_at)
			VALUES (?1, 'review', ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9)
			ON CONFLICT (key_id) DO UPDATE SET state = 'review', name = ?2, description = ?3, link = ?4, platform = ?5,
				credit_url = ?6, show_maker = ?7, screenshot = ?8, note = NULL, submitted_at = ?9`,
		).bind(key.id, listing.name, listing.description, listing.link, listing.platform, listing.credit_url, listing.show_maker, listing.screenshot, now).run();
	}

	c.executionCtx.waitUntil((async () => sendMail(c.env, {
		to: await reviewerAddresses(c.env),
		subject: `${row?.state === 'listed' ? 'Showcase edit' : 'Showcase listing'} to review: ${listing.name}`,
		text: [
			`${session.name} ${row?.state === 'listed' ? 'edited a listed project' : 'sent a project for the showcase'}.`,
			'',
			`Project: ${listing.name}`,
			`Platform: ${platformLabel(listing.platform)}`,
			`Link: ${listing.link}`,
			`Credit: ${listing.credit_url}`,
			'',
			`Review it: ${c.env.PORTAL_ORIGIN}/review`,
		].join('\n'),
	}))());
	return c.redirect('/account/keys?done=sent', 303);
});

listings.post('/withdraw', async (c) => {
	const key = await activeKey(c);
	if (!key) return noKey(c);
	const row = await listingOf(c.env.ACCOUNTS, key.id);
	if (!row) return c.redirect('/account/keys', 303);
	const pending = pendingOf(row);
	if (pending) {
		// Withdraw the edit; the listed version stays.
		await c.env.ACCOUNTS.prepare(`UPDATE listings SET pending = NULL WHERE key_id = ?`).bind(key.id).run();
		if (pending.screenshot && pending.screenshot !== row.screenshot) await c.env.SHOTS.delete(pending.screenshot);
	} else if (row.state === 'review' || row.state === 'listed') {
		await c.env.ACCOUNTS.prepare(`UPDATE listings SET state = 'draft' WHERE key_id = ?`).bind(key.id).run();
	}
	return c.redirect('/account/keys?done=withdrawn', 303);
});
