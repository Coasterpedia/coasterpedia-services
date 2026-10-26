// The showcase (DR-010 §13.4): listed projects whose key was used in the
// last 90 days, newest listed first, never ordered by usage.
import { Hono } from 'hono';
import { type AppEnv, page } from './app';
import { HIDE_AFTER_DAYS, type ListingRow, PLATFORMS, platformLabel, usesOf } from './data';
import type { Env } from './env';
import { shotUrl } from './listings';
import { WIKI } from './views/layout';

export const showcase = new Hono<AppEnv>();

type ShowcaseRow = ListingRow & { uses: string | null; username: string };

/** Listed, key alive, and used (or listed) within the window. */
export async function visibleListings(env: Env, now = Date.now()): Promise<ShowcaseRow[]> {
	const cutoff = new Date(now - HIDE_AFTER_DAYS * 86400_000).toISOString();
	const { results } = await env.ACCOUNTS.prepare(
		`SELECT l.*, k.uses, a.username
		FROM listings l JOIN keys k ON k.id = l.key_id JOIN accounts a ON a.id = k.account_id
		WHERE l.state = 'listed' AND k.revoked_at IS NULL AND (k.last_used_at >= ?1 OR l.listed_at >= ?1)
		ORDER BY l.listed_at DESC`,
	).bind(cutoff).all<ShowcaseRow>();
	return results;
}

export function ProjectCard({ row }: { row: ShowcaseRow }) {
	const uses = usesOf(row);
	return (
		<article class="card project">
			{/* The placeholder primitive goes on the image box itself. */}
			<a class={row.screenshot ? 'project__shot' : 'project__shot t-photo-placeholder'} href={row.link} tabindex={-1} aria-hidden="true">
				{row.screenshot && <img src={shotUrl(row.screenshot)} alt="" width={640} height={400} loading="lazy" />}
			</a>
			<div class="project__body">
				<div class="project__title">
					<a href={row.link} rel="noopener">{row.name}</a>
					<span class="t-badge">{platformLabel(row.platform)}</span>
				</div>
				<p>{row.description}</p>
				{(uses.length > 0 || row.show_maker === 1) && (
					<p class="meta project__meta">
						{uses.length > 0 && `Uses ${uses.join(' · ')}`}
						{uses.length > 0 && row.show_maker === 1 && ' · '}
						{row.show_maker === 1 && <>by <a href={`${WIKI}/wiki/User:${encodeURIComponent(row.username)}`}>{row.username}</a></>}
					</p>
				)}
			</div>
		</article>
	);
}

showcase.get('/showcase', async (c) => {
	const all = await visibleListings(c.env);
	const on = new Set((c.req.query('on') ?? '').split(',').filter((s) => PLATFORMS.some((p) => p.slug === s)));
	const shown = on.size ? all.filter((r) => on.has(r.platform)) : all;
	const facets = PLATFORMS.map((p) => ({ ...p, count: all.filter((r) => r.platform === p.slug).length })).filter((p) => p.count > 0);

	const toggled = (slug: string) => {
		const next = new Set(on);
		if (next.has(slug)) next.delete(slug);
		else next.add(slug);
		return PLATFORMS.map((p) => p.slug).filter((s) => next.has(s)).join(',');
	};

	return page(c, {
		title: 'Showcase',
		description: 'Apps, bots, websites and libraries built on the Coasterpedia API.',
		section: 'showcase',
		children: (
			<div class="wrap page">
				<div class="page__head">
					<div class="page__intro">
						<h1>Built with Coasterpedia</h1>
						<p class="lede">Apps, bots, websites and libraries built on the Coasterpedia API.</p>
					</div>
					<a class="btn" href="/account/keys">List your project</a>
				</div>

				{facets.length > 1 && (
					<form method="get" action="/showcase" class="facets" aria-label="Platform">
						{facets.map((f) => (
							<button type="submit" class="t-facet" name="on" value={toggled(f.slug)} aria-pressed={on.has(f.slug) ? 'true' : 'false'}>
								{f.label} <span class="t-facet__count">{f.count}</span>
							</button>
						))}
					</form>
				)}

				{all.length === 0 ? (
					<p>Nothing is listed yet. If you’ve built something on the API, you can list it from <a href="/account/keys">Your keys</a>.</p>
				) : (
					<div class="projects">{shown.map((row) => <ProjectCard row={row} />)}</div>
				)}

				<p class="meta page__foot">
					We list a project once we’ve checked that it works and credits Coasterpedia. If its key makes no requests for {HIDE_AFTER_DAYS} days, we hide it until the next one.
				</p>
			</div>
		),
	});
});

/** showcase.json's body, which the daily run also hands to the home page's build. */
export async function showcaseJson(env: Env, now = Date.now()) {
	const rows = await visibleListings(env, now);
	return {
		data: rows.map((r) => ({
			name: r.name,
			description: r.description,
			url: r.link,
			platform: r.platform,
			screenshot: r.screenshot ? `${env.PORTAL_ORIGIN}${shotUrl(r.screenshot)}` : null,
			uses: usesOf(r),
			maker: r.show_maker === 1 ? r.username : null,
			listed_at: r.listed_at,
		})),
	};
}

/** Public, no key (§13.5): what the wiki can read later. */
showcase.get('/showcase.json', async (c) => c.json(await showcaseJson(c.env), 200, { 'cache-control': 'public, max-age=300' }));

/** Screenshot names are random and change with every upload, so they never go stale. */
showcase.get('/showcase/shots/:name', async (c) => {
	const name = c.req.param('name');
	if (!/^[A-Za-z0-9_-]+\.webp$/.test(name)) return c.notFound();
	const object = await c.env.SHOTS.get(name);
	if (!object) return c.notFound();
	return new Response(object.body, {
		headers: { 'content-type': 'image/webp', 'cache-control': 'public, max-age=31536000, immutable', etag: object.httpEtag },
	});
});
