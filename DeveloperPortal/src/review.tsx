// The reviewer's queue (DR-010 §13.4, §13.5): new listings and edits to
// listed ones. Three checks: it works, the credit is where the form says,
// and the screenshot is fine.
import { Hono } from 'hono';
import { type AppEnv, type Ctx, isoNow, isReviewer, message, page, requireSession, shortDate } from './app';
import { type ListingFields, type ListingRow, pendingOf, platformLabel, usesOf } from './data';
import { CREDIT_LINE, shotUrl } from './listings';
import { sendMail } from './mail';
import { WIKI } from './views/layout';

export const review = new Hono<AppEnv>();
review.use('*', requireSession);
review.use('*', async (c, next) => {
	if (!isReviewer(c.env, c.get('session').sub)) {
		return message(c, 403, 'Reviewers only', <p>Only showcase reviewers can see this page.</p>);
	}
	await next();
});

type QueueRow = ListingRow & { prefix: string; uses: string | null; last_used_at: string | null; username: string; email: string };

function Entry({ row }: { row: QueueRow }) {
	const pending = pendingOf(row);
	const shown: ListingFields = pending ?? row;
	return (
		<article class="card review-entry">
			{shown.screenshot
				? <img class="review-entry__shot" src={shotUrl(shown.screenshot)} alt={`Screenshot of ${shown.name}`} width={640} height={400} />
				: <div class="review-entry__shot t-photo-placeholder" role="img" aria-label="No screenshot" />}
			<div class="review-entry__body">
				<h2>{shown.name} <span class="t-badge">{platformLabel(shown.platform)}</span></h2>
				<p>{shown.description}</p>
				<dl class="facts">
					<dt>Kind</dt><dd>{pending ? `An edit to a listed project${row.name !== shown.name ? ` (listed as ${row.name})` : ''}` : 'A new listing'}</dd>
					<dt>Sent</dt><dd>{row.submitted_at ? shortDate(row.submitted_at) : 'unknown'} by <a href={`${WIKI}/wiki/User:${encodeURIComponent(row.username)}`}>{row.username}</a></dd>
					<dt>Link</dt><dd><a href={shown.link} rel="noopener noreferrer">{shown.link}</a></dd>
					<dt>Credit</dt><dd><a href={shown.credit_url} rel="noopener noreferrer">{shown.credit_url}</a></dd>
					<dt>Key</dt><dd><code>{row.prefix}…</code>, {usesOf(row).join(' · ') || 'no requests in 30 days'}</dd>
					<dt>Maker shown</dt><dd>{shown.show_maker ? 'Yes' : 'No'}</dd>
				</dl>
				<p class="meta">The credit should read: <code>{CREDIT_LINE}</code></p>
				<div class="review-entry__actions">
					<form method="post" action={`/review/${row.key_id}/approve`}>
						<button type="submit" class="btn btn--primary">{pending ? 'Accept the edit' : 'List it'}</button>
					</form>
					<form method="post" action={`/review/${row.key_id}/reject`} class="review-entry__reject">
						<label class="field__label" for={`reason-${row.key_id}`}>Or say what’s missing, in one line</label>
						<div class="review-entry__reject-row">
							<input class="input" id={`reason-${row.key_id}`} name="reason" maxlength={200} required />
							<button type="submit" class="btn">Don’t list</button>
						</div>
					</form>
				</div>
			</div>
		</article>
	);
}

review.get('/', async (c) => {
	const { results } = await c.env.ACCOUNTS.prepare(
		`SELECT l.*, k.prefix, k.uses, k.last_used_at, a.username, a.email
		FROM listings l JOIN keys k ON k.id = l.key_id JOIN accounts a ON a.id = k.account_id
		WHERE (l.state = 'review' OR l.pending IS NOT NULL) AND k.revoked_at IS NULL
		ORDER BY l.submitted_at`,
	).all<QueueRow>();
	return page(c, {
		title: 'Review',
		section: 'review',
		children: (
			<div class="wrap page">
				<div class="page__intro">
					<h1>Review</h1>
					<p>Check that each project works, that the credit is where it says, and that the screenshot is fine.</p>
				</div>
				{results.length === 0 ? <p>Nothing is waiting.</p> : <div class="review-list">{results.map((row) => <Entry row={row} />)}</div>}
			</div>
		),
	});
});

async function queued(c: Ctx): Promise<QueueRow | null> {
	return c.env.ACCOUNTS.prepare(
		`SELECT l.*, k.prefix, k.uses, k.last_used_at, a.username, a.email
		FROM listings l JOIN keys k ON k.id = l.key_id JOIN accounts a ON a.id = k.account_id
		WHERE l.key_id = ? AND (l.state = 'review' OR l.pending IS NOT NULL)`,
	).bind(c.req.param('key')).first<QueueRow>();
}

review.post('/:key/approve', async (c) => {
	const row = await queued(c);
	if (!row) return c.redirect('/review', 303);
	const pending = pendingOf(row);
	const now = isoNow();
	const by = c.get('session').sub;
	const name = pending?.name ?? row.name;

	if (pending) {
		await c.env.ACCOUNTS.prepare(
			`UPDATE listings SET name = ?, description = ?, link = ?, platform = ?, credit_url = ?, show_maker = ?, screenshot = ?,
				pending = NULL, note = NULL, reviewed_at = ?, reviewed_by = ? WHERE key_id = ?`,
		).bind(pending.name, pending.description, pending.link, pending.platform, pending.credit_url, pending.show_maker, pending.screenshot, now, by, row.key_id).run();
		if (row.screenshot && row.screenshot !== pending.screenshot) await c.env.SHOTS.delete(row.screenshot);
	} else {
		await c.env.ACCOUNTS.prepare(
			`UPDATE listings SET state = 'listed', listed_at = COALESCE(listed_at, ?1), note = NULL, reviewed_at = ?1, reviewed_by = ?2 WHERE key_id = ?3`,
		).bind(now, by, row.key_id).run();
	}

	c.executionCtx.waitUntil(sendMail(c.env, {
		to: [row.email],
		subject: pending ? `Your edit to ${name} is live in the showcase` : `${name} is in the showcase`,
		text: [
			pending ? `We’ve accepted your edit to ${name}, and it’s now in the showcase.` : `We’ve listed ${name} in the Coasterpedia showcase.`,
			'',
			`${c.env.PORTAL_ORIGIN}/showcase`,
			'',
			'We hide it if the key makes no requests for 90 days, and show it again after the next one.',
		].join('\n'),
	}));
	return c.redirect('/review', 303);
});

review.post('/:key/reject', async (c) => {
	const row = await queued(c);
	if (!row) return c.redirect('/review', 303);
	const reason = String((await c.req.parseBody()).reason ?? '').trim().slice(0, 200);
	if (!reason) return c.redirect('/review', 303);
	const pending = pendingOf(row);
	const now = isoNow();
	const by = c.get('session').sub;

	if (pending) {
		// The listed version stays; the edit goes, with the reason.
		await c.env.ACCOUNTS.prepare(`UPDATE listings SET pending = NULL, note = ?, reviewed_at = ?, reviewed_by = ? WHERE key_id = ?`)
			.bind(reason, now, by, row.key_id).run();
		if (pending.screenshot && pending.screenshot !== row.screenshot) await c.env.SHOTS.delete(pending.screenshot);
	} else {
		await c.env.ACCOUNTS.prepare(`UPDATE listings SET state = 'rejected', note = ?, reviewed_at = ?, reviewed_by = ? WHERE key_id = ?`)
			.bind(reason, now, by, row.key_id).run();
	}

	const name = pending?.name ?? row.name;
	c.executionCtx.waitUntil(sendMail(c.env, {
		to: [row.email],
		subject: pending ? `We didn’t accept your showcase edit to ${name}` : `We haven’t listed ${name} yet`,
		text: [
			pending ? `We didn’t accept your edit to ${name}, so the listed version stays up.` : `We haven’t listed ${name} in the showcase yet.`,
			'',
			`The reviewer said: ${reason}`,
			'',
			`You can change it and send it again from Your keys, at ${c.env.PORTAL_ORIGIN}/account/keys.`,
			'If you have a question, reply to this email.',
		].join('\n'),
	}));
	return c.redirect('/review', 303);
});
