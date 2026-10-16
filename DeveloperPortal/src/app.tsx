// What every route shares: the Hono types, who is signed in, and the page
// and message helpers.
import type { Context, MiddlewareHandler } from 'hono';
import type { Child } from 'hono/jsx';
import { readSession, type Session } from './cookies';
import type { Env } from './env';
import { reviewerIds } from './mail';
import { renderPage, type PageProps, type Viewer } from './views/layout';

export type AppEnv = { Bindings: Env; Variables: { session: Session } };
export type Ctx = Context<AppEnv>;

/** Where a developer writes to us; also SES's sending address (§13.7). */
export const CONTACT = 'developer@coasterpedia.net';

/** Until the terms are in force, keys are made by hand (§10 phase 2). */
export function ClosedBeta() {
	return <p>During the closed beta we create keys by hand. To ask for one, email <a href={`mailto:${CONTACT}`}>{CONTACT}</a>.</p>;
}

export const USER_AGENT = 'CoasterpediaDeveloperPortal/1.0 (https://developer.coasterpedia.net; developer@coasterpedia.net)';

export function isReviewer(env: Env, sub: number): boolean {
	return reviewerIds(env).includes(sub);
}

export async function reviewQueue(env: Env): Promise<number> {
	const row = await env.ACCOUNTS.prepare(`SELECT COUNT(*) AS n FROM listings WHERE state = 'review' OR pending IS NOT NULL`).first<{ n: number }>();
	return row?.n ?? 0;
}

/** The header's view of the signed-in reader, with the queue for a reviewer. */
export async function viewerOf(c: Ctx, session: Session | null): Promise<Viewer | undefined> {
	if (!session) return undefined;
	return isReviewer(c.env, session.sub) ? { name: session.name, queue: await reviewQueue(c.env) } : { name: session.name };
}

/** /account and /review: signed in, or off to sign in and back. */
export const requireSession: MiddlewareHandler<AppEnv> = async (c, next) => {
	const session = await readSession(c);
	if (!session) {
		const back = new URL(c.req.url);
		return c.redirect(`/auth/sign-in?return=${encodeURIComponent(back.pathname + back.search)}`, 303);
	}
	c.set('session', session);
	await next();
};

/** A page from the shared layout. Dynamic pages are never cached. */
export async function page(c: Ctx, props: Omit<PageProps, 'viewer'>, status = 200): Promise<Response> {
	const session = c.get('session') ?? (await readSession(c));
	const html = await renderPage({ ...props, viewer: await viewerOf(c, session) });
	return c.html(html, status as 200, { 'cache-control': 'no-store' });
}

/** A short page that says one thing: an error, a refusal, a next step. */
export function message(c: Ctx, status: number, title: string, body: Child, section: PageProps['section'] = 'none'): Promise<Response> {
	return page(c, {
		title,
		section,
		children: (
			<div class="wrap page">
				<h1>{title}</h1>
				<div class="message">{body}</div>
			</div>
		),
	}, status);
}

export const isoNow = () => new Date().toISOString();

const DATE = new Intl.DateTimeFormat('en-GB', { day: 'numeric', month: 'short', year: 'numeric', timeZone: 'UTC' });

/** "2 Oct 2026". */
export function shortDate(iso: string): string {
	return DATE.format(new Date(iso));
}

/** "2 minutes ago", "today", "3 days ago": how recently, not exactly when. */
export function ago(iso: string, now = Date.now()): string {
	const minutes = Math.floor((now - Date.parse(iso)) / 60_000);
	if (minutes < 1) return 'just now';
	if (minutes < 60) return `${minutes} minute${minutes === 1 ? '' : 's'} ago`;
	const hours = Math.floor(minutes / 60);
	if (hours < 24) return `${hours} hour${hours === 1 ? '' : 's'} ago`;
	const days = Math.floor(hours / 24);
	return days === 1 ? 'yesterday' : `${days} days ago`;
}
