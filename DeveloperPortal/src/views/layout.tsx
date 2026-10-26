// The portal's one page frame, rendered by the Worker for its dynamic pages
// and by build/build.tsx for the static ones (DR-010 §13.9), so the two can't
// drift apart.
import type { Child } from 'hono/jsx';

export type Section = 'home' | 'docs' | 'showcase' | 'changelog' | 'keys' | 'review' | 'terms' | 'none';

export interface Viewer {
	name: string;
	/** Listings waiting for review; set only for a reviewer (§13.7). */
	queue?: number;
}

export interface PageProps {
	title: string;
	description?: string;
	section: Section;
	/** Signed in. Static pages never know, so they always offer sign-in. */
	viewer?: Viewer;
	/** schema_version from /v1/meta, when the page was built with it. */
	apiVersion?: string;
	/** A page in the docs has a Markdown twin for agents (§8.1). */
	markdown?: string;
	children: Child;
}

export const WIKI = 'https://coasterpedia.net';

const NAV: { section: Section; href: string; label: string }[] = [
	{ section: 'docs', href: '/docs/', label: 'Docs' },
	{ section: 'showcase', href: '/showcase', label: 'Showcase' },
	{ section: 'changelog', href: '/changelog/', label: 'Changelog' },
	{ section: 'keys', href: '/account/keys', label: 'Your keys' },
];

function Header({ section, viewer }: { section: Section; viewer?: Viewer }) {
	return (
		<header class="portal-header">
			<div class="wrap portal-header__inner">
				<a class="portal-header__brand" href="/">
					Coasterpedia <span>for developers</span>
				</a>
				<nav class="portal-header__nav" aria-label="Portal">
					{NAV.map((item) => (
						<a href={item.href} aria-current={item.section === section ? 'page' : undefined}>{item.label}</a>
					))}
					{viewer?.queue !== undefined && (
						<a href="/review" aria-current={section === 'review' ? 'page' : undefined}>
							Review{viewer.queue > 0 ? ` (${viewer.queue})` : ''}
						</a>
					)}
				</nav>
				{viewer ? (
					<span class="portal-header__user">
						<svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
							<circle cx="12" cy="8" r="4" />
							<path d="M4 21a8 8 0 0 1 16 0" />
						</svg>
						{viewer.name}
					</span>
				) : (
					<a class="btn portal-header__signin" href="/auth/sign-in">Sign in with Coasterpedia</a>
				)}
			</div>
		</header>
	);
}

function Footer({ apiVersion, viewer }: { apiVersion?: string; viewer?: Viewer }) {
	return (
		<footer class="portal-footer">
			<div class="wrap portal-footer__inner">
				{apiVersion && <span>API version {apiVersion}</span>}
				<a href="/terms/">API terms</a>
				<a href={`${WIKI}/wiki/Coasterpedia:Privacy_policy`}>Privacy</a>
				<a href="/openapi.yaml">OpenAPI contract</a>
				<a href="/llms.txt">llms.txt</a>
				<a href="https://status.coasterpedia.net">Status</a>
				<a href={WIKI}>coasterpedia.net</a>
				{viewer && (
					<form method="post" action="/auth/sign-out" class="portal-footer__signout">
						<button type="submit" class="link-button">Sign out</button>
					</form>
				)}
			</div>
		</footer>
	);
}

export function Layout(props: PageProps) {
	return (
		<html lang="en">
			<head>
				<meta charset="utf-8" />
				<meta name="viewport" content="width=device-width, initial-scale=1" />
				<title>{props.title === 'Coasterpedia for developers' ? props.title : `${props.title} · Coasterpedia for developers`}</title>
				{props.description && <meta name="description" content={props.description} />}
				{props.markdown && <link rel="alternate" type="text/markdown" href={props.markdown} />}
				<link rel="stylesheet" href="/portal.css" />
				<link rel="icon" href="/favicon.ico" sizes="16x16 32x32" />
			</head>
			<body>
				<a class="skip-link" href="#content">Skip to content</a>
				<Header section={props.section} viewer={props.viewer} />
				<main id="content" class="portal-main">{props.children}</main>
				<Footer apiVersion={props.apiVersion} viewer={props.viewer} />
			</body>
		</html>
	);
}

/** The whole document as a string. */
export async function renderPage(props: PageProps): Promise<string> {
	return '<!doctype html>' + (await (<Layout {...props} />).toString());
}
