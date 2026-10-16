// Renders every public page to dist/ as static assets (DR-010 §13.9): a
// request that matches a file here never runs the Worker and isn't billed.
// Runs on every deploy and nightly, so the counts and the showcase strip are
// at most a day old.
//
//   npm run build
import { mkdirSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { raw } from 'hono/html';
import type { Child } from 'hono/jsx';
import { Marked } from 'marked';
import { parse, stringify } from 'yaml';
import { ClosedBeta } from '../src/app';
import { PLATFORMS } from '../src/data';
import { TERMS_VERSION } from '../src/terms-version';
import { renderPage, type Section } from '../src/views/layout';
import { buildCss } from './css';
import { type Examples, loadSpec, ReferenceHtml, referenceMarkdown } from './openapi';

const root = dirname(dirname(fileURLToPath(import.meta.url)));
const out = join(root, 'dist');
const API = 'https://api.coasterpedia.net';
const PORTAL = 'https://developer.coasterpedia.net';
const md = new Marked({ gfm: true });

rmSync(out, { recursive: true, force: true });
mkdirSync(out, { recursive: true });

function write(path: string, body: string | Uint8Array): void {
	const file = join(out, path);
	mkdirSync(dirname(file), { recursive: true });
	writeFileSync(file, body);
}

async function fetchJson<T>(url: string): Promise<T | null> {
	try {
		const res = await fetch(url, { headers: { 'user-agent': 'CoasterpediaDeveloperPortal-build/1.0 (https://developer.coasterpedia.net)' }, signal: AbortSignal.timeout(10_000) });
		if (!res.ok) throw new Error(`${res.status}`);
		return (await res.json()) as T;
	} catch (e) {
		console.warn(`build: ${url} unavailable (${(e as Error).message}); building without it`);
		return null;
	}
}

interface Meta { schema_version: string; counts: Record<string, number> }
interface Listed { name: string; description: string; url: string; platform: string; screenshot: string | null; uses: string[]; maker: string | null }

const meta = await fetchJson<Meta>(`${API}/v1/meta`);
const listed = (await fetchJson<{ data: Listed[] }>(`${PORTAL}/showcase.json`))?.data ?? [];
const apiVersion = meta?.schema_version;

async function page(path: string, props: { title: string; description?: string; section: Section; markdown?: string; children: Child }) {
	write(path, await renderPage({ ...props, apiVersion }));
}

function frontMatter(text: string): { title: string; description?: string; body: string } {
	const match = /^---\n([\s\S]*?)\n---\n/.exec(text);
	const fields = Object.fromEntries((match?.[1] ?? '').split('\n').map((l) => l.split(/:\s*/, 2) as [string, string]));
	return { title: fields.title ?? '', description: fields.description, body: match ? text.slice(match[0].length) : text };
}

// ── Stylesheet, contract, small files ─────────────────────────────────────

write('portal.css', buildCss(root, out));
// The contract carries the live version, not the draft label it's written
// with, so the file, the reference and /v1/meta all agree.
const contractText = readFileSync(join(root, 'vendor/openapi.yaml'), 'utf8');
const published = parse(contractText) as { info: { version: string } };
if (meta) published.info.version = meta.schema_version;
write('openapi.yaml', contractText.split('\n')[0] + '\n' + stringify(published, { lineWidth: 0 }));
write('copy.js', readFileSync(join(root, 'content/copy.js'), 'utf8'));
write('favicon.ico', readFileSync(join(root, 'content/favicon.ico')));
write('robots.txt', 'User-agent: *\nDisallow: /account/\nDisallow: /auth/\nDisallow: /review\n');
// Static assets carry the same headers the Worker sets on its pages.
write('_headers', [
	'/*',
	"  Content-Security-Policy: default-src 'self'; img-src 'self' data:; script-src 'self'; style-src 'self'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'",
	'  X-Content-Type-Options: nosniff',
	'  Referrer-Policy: strict-origin-when-cross-origin',
	'/fonts/*',
	'  Cache-Control: public, max-age=31536000, immutable',
	'',
].join('\n'));

// ── Docs ──────────────────────────────────────────────────────────────────

const GUIDES = [
	{ slug: 'getting-started', path: '/docs/' },
	{ slug: 'paging', path: '/docs/paging/' },
	{ slug: 'views', path: '/docs/views/' },
	{ slug: 'changes', path: '/docs/changes/' },
	{ slug: 'errors', path: '/docs/errors/' },
	{ slug: 'attribution', path: '/docs/attribution/' },
].map((g) => ({ ...g, ...frontMatter(readFileSync(join(root, 'content/guides', `${g.slug}.md`), 'utf8')) }));

const guideFiles = readdirSync(join(root, 'content/guides')).filter((f: string) => f.endsWith('.md'));
if (guideFiles.length !== GUIDES.length) throw new Error(`content/guides has ${guideFiles.length} files but GUIDES lists ${GUIDES.length}`);

function DocsNav({ current }: { current: string }) {
	return (
		<nav class="docs-nav" aria-label="Docs">
			<p class="docs-nav__heading">Guides</p>
			<ul>
				{GUIDES.map((g) => <li><a href={g.path} aria-current={g.path === current ? 'page' : undefined}>{g.title}</a></li>)}
			</ul>
			<p class="docs-nav__heading">Reference</p>
			<ul>
				<li><a href="/docs/reference/" aria-current={current === '/docs/reference/' ? 'page' : undefined}>Endpoints and records</a></li>
				<li><a href="/openapi.yaml">OpenAPI contract</a></li>
			</ul>
		</nav>
	);
}

function Docs({ current, children }: { current: string; children: Child }) {
	return (
		<div class="wrap docs">
			<DocsNav current={current} />
			<article class="prose">{children}</article>
		</div>
	);
}

for (const g of GUIDES) {
	const mdPath = `/docs/${g.slug}.md`;
	write(mdPath, g.body);
	await page(`${g.path.slice(1)}index.html`, {
		title: g.slug === 'getting-started' ? 'Docs' : g.title,
		description: g.description,
		section: 'docs',
		markdown: mdPath,
		children: <Docs current={g.path}>{raw(md.parse(g.body) as string)}</Docs>,
	});
}

const spec = loadSpec(contractText);
if (meta) spec.info.version = meta.schema_version;
let examples: Examples | null = null;
try {
	examples = JSON.parse(readFileSync(join(root, 'vendor/examples.json'), 'utf8')) as Examples;
} catch {
	console.warn('build: vendor/examples.json missing; run npm run examples');
}
// A new endpoint needs a request in scripts/capture-examples.mjs.
const missing = Object.entries(spec.paths).flatMap(([path, item]) => Object.keys(item).map((m) => `${m.toUpperCase()} ${path}`))
	.filter((op) => op !== 'POST /session' && !examples?.examples[op]);
if (missing.length) console.warn(`build: no example response for ${missing.join(', ')}`);
write('docs/reference.md', referenceMarkdown(spec, examples));
await page('docs/reference/index.html', {
	title: 'API reference',
	description: 'Every endpoint and record in the Coasterpedia API.',
	section: 'docs',
	markdown: '/docs/reference.md',
	children: <Docs current="/docs/reference/"><ReferenceHtml spec={spec} examples={examples} /></Docs>,
});

// ── Changelog and terms ───────────────────────────────────────────────────

const changelog = frontMatter(readFileSync(join(root, 'content/changelog.md'), 'utf8'));
write('changelog.md', changelog.body);
await page('changelog/index.html', {
	title: 'Changelog',
	description: changelog.description,
	section: 'changelog',
	markdown: '/changelog.md',
	children: <div class="wrap page"><article class="prose">{raw(md.parse(changelog.body) as string)}</article></div>,
});

let terms: string;
try {
	terms = readFileSync(join(root, 'vendor/terms.md'), 'utf8');
} catch {
	terms = '# API terms\n\nWe’re still writing the API terms. During the closed beta we create keys by hand. To ask for one, email [developer@coasterpedia.net](mailto:developer@coasterpedia.net).\n';
}
write('terms.md', terms);
await page('terms/index.html', {
	title: 'API terms',
	section: 'terms',
	markdown: '/terms.md',
	children: (
		<div class="wrap page">
			{!TERMS_VERSION && <div class="notice" role="note"><p>These terms are a draft and aren’t in force yet.</p><ClosedBeta /></div>}
			<article class="prose">{raw(md.parse(terms) as string)}</article>
		</div>
	),
});

// ── Home ──────────────────────────────────────────────────────────────────

const COUNTED: [string, string][] = [['attractions', 'Attractions'], ['parks', 'Parks'], ['manufacturers', 'Manufacturers'], ['products', 'Products']];

const GETS = [
	{ title: 'The wiki’s own records', body: 'The same data the wiki shows, kept up by its editors every day, with stable IDs that survive renames and moves.' },
	{ title: 'Current within minutes', body: <>An edit reaches the API in a couple of minutes. You can read edits as they happen, or <a href="/docs/views/">only those that have stood for a week</a>.</> },
	{ title: 'A feed of what changed', body: <><code>/v1/changes</code> lists every record that changed since you last asked, so you only download what changed.</> },
	{ title: 'Open licence', body: <>The data is CC BY-SA 4.0. Credit Coasterpedia wherever you show it, and <a href="/docs/attribution/">share alike</a>.</> },
];

function HomeCard({ p }: { p: Listed }) {
	const label = PLATFORMS.find((x) => x.slug === p.platform)?.label ?? p.platform;
	return (
		<article class="card project">
			<a class={p.screenshot ? 'project__shot' : 'project__shot t-photo-placeholder'} href={p.url} tabindex={-1} aria-hidden="true">
				{p.screenshot && <img src={p.screenshot.replace(PORTAL, '')} alt="" width={640} height={400} loading="lazy" />}
			</a>
			<div class="project__body">
				<div class="project__title">
					<a href={p.url} rel="noopener">{p.name}</a>
					<span class="t-badge">{label}</span>
				</div>
				<p>{p.description}</p>
				{p.uses.length > 0 && <p class="meta project__meta">Uses {p.uses.join(' · ')}</p>}
			</div>
		</article>
	);
}

await page('index.html', {
	title: 'Coasterpedia for developers',
	description: 'Coasterpedia’s ride data as an API: attractions, parks, manufacturers and products as JSON, with stable IDs and a change feed.',
	section: 'home',
	children: (
		<>
			<section class="wrap hero">
				<div class="hero__text">
					<h1>Coasterpedia’s ride data, as an API</h1>
					<p class="lede">Every attraction, park, manufacturer and product on the wiki, as structured JSON with stable IDs and a change feed. Free for non-commercial projects.</p>
					<div class="hero__actions">
						<a class="btn btn--primary" href="/account/keys">Get a free key</a>
						<a class="btn" href="/docs/">Read the docs</a>
					</div>
					<p class="meta">Sign in with your wiki account. You don’t need a new password, and creating a key takes about a minute.</p>
				</div>
				<div class="hero__sample">
					<div class="sample">
						<div class="sample__label">Request</div>
						<pre class="code">{'curl -H "Authorization: Bearer cpk_…" \\\n  "https://api.coasterpedia.net/v1/attractions?sort=-height"'}</pre>
						<div class="sample__label">Each record</div>
						<pre class="code">{'id · name · type · status · location\ndates · stats · manufacturers · products\nhistory'}</pre>
					</div>
				</div>
			</section>

			{meta && (
				<section class="wrap section" aria-label="What the API holds">
					<div class="counts">
						{COUNTED.filter(([key]) => meta.counts[key] !== undefined).map(([key, label]) => (
							<div class="card count">
								<div class="count__number">{meta.counts[key].toLocaleString('en-GB')}</div>
								<div>{label}</div>
							</div>
						))}
					</div>
					<p class="meta">We update these numbers daily from <a href="/docs/reference/#get-meta">/v1/meta</a>.</p>
				</section>
			)}

			<section class="wrap section">
				<h2>What you get</h2>
				<div class="gets">
					{GETS.map((g) => <div><h3>{g.title}</h3><p>{g.body}</p></div>)}
				</div>
			</section>

			{listed.length > 0 && (
				<section class="strip">
					<div class="wrap section">
						<div class="strip__head">
							<div>
								<h2>Built with Coasterpedia</h2>
								<p>Apps, bots, websites and libraries built on the Coasterpedia API.</p>
							</div>
							<a href="/showcase">See all projects</a>
						</div>
						<div class="projects">{listed.slice(0, 3).map((p) => <HomeCard p={p} />)}</div>
						<p>If you’ve built something on the API, you can list it from <a href="/account/keys">Your keys</a>.</p>
					</div>
				</section>
			)}
		</>
	),
});

// ── llms.txt ──────────────────────────────────────────────────────────────

write('llms.txt', [
	'# Coasterpedia API',
	'',
	'> Read-only JSON API over Coasterpedia, the amusement ride wiki: attractions, parks, park companies, manufacturers and products, with stable IDs and a change feed. Data is CC BY-SA 4.0.',
	'',
	'Requests go to https://api.coasterpedia.net/v1 with `Authorization: Bearer cpk_…`. A person gets a key by signing in at https://developer.coasterpedia.net with a Coasterpedia account; free keys are for non-commercial projects. /v1/meta and /v1/vocabularies need no key.',
	'',
	'## Docs',
	'',
	...GUIDES.map((g) => `- [${g.title}](${PORTAL}/docs/${g.slug}.md): ${g.description ?? ''}`),
	`- [API reference](${PORTAL}/docs/reference.md): every endpoint and record`,
	`- [OpenAPI contract](${PORTAL}/openapi.yaml)`,
	'',
	'## Optional',
	'',
	`- [Changelog](${PORTAL}/changelog.md)`,
	`- [API terms](${PORTAL}/terms.md)`,
	'',
].join('\n'));

console.log(`built dist/: API ${apiVersion ?? 'unknown'}, ${listed.length} listed, terms ${TERMS_VERSION ?? 'draft'}`);
