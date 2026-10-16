// Copy the portal's inputs from the wiki mirror into vendor/ (DR-010 §13.9).
//
//   npm run sync                      # mirror at ~/Documents/Code/Coasterpedia
//   COASTERPEDIA_DIR=/path npm run sync
//
// The mirror isn't a git repository, so CI can't read it: vendor/ is the
// committed copy the build reads, and this script is the one place the two
// can drift. Run it after changing the contract, the terms or the tokens.
import { copyFileSync, existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parse, stringify } from 'yaml';

const here = dirname(dirname(fileURLToPath(import.meta.url)));
const mirror = process.env.COASTERPEDIA_DIR ?? join(homedir(), 'Documents/Code/Coasterpedia');
const vendor = join(here, 'vendor');
mkdirSync(join(vendor, 'tokens'), { recursive: true });

function copy(from, to) {
	copyFileSync(join(mirror, from), join(vendor, to));
	console.log(`${from} → vendor/${to}`);
}

// The contract, as the public sees it. The mirror's copy is also the
// Projector's mapping and a design note, so its comments, x-cargo and
// x-not-exported name tables, columns and decisions. None of that is
// published: /openapi.yaml is served as is.
const PRIVATE_KEYS = new Set(['x-cargo', 'x-not-exported']);
function strip(node) {
	if (Array.isArray(node)) return node.map(strip);
	if (node && typeof node === 'object') {
		return Object.fromEntries(Object.entries(node).filter(([k]) => !PRIVATE_KEYS.has(k)).map(([k, v]) => [k, strip(v)]));
	}
	return node;
}
const contract = strip(parse(readFileSync(join(mirror, 'api/openapi.yaml'), 'utf8')));
writeFileSync(join(vendor, 'openapi.yaml'),
	'# The Coasterpedia API contract. Docs: https://developer.coasterpedia.net/docs/\n' +
	stringify(contract, { lineWidth: 0 }));
console.log('api/openapi.yaml → vendor/openapi.yaml, without comments, x-cargo or x-not-exported');

// Not base.css (wiki element defaults), layout.css (Citizen's rail and
// drawer) or fonts.css (Google Fonts; the portal serves its own).
for (const name of ['colors', 'typography', 'borders', 'spacing', 'elevation', 'motion']) {
	copy(`Design/tokens/${name}.css`, `tokens/${name}.css`);
}

// MediaWiki:Citizen.css's token overrides: the brand channels, the
// progressive-subtle fills and the darker light-mode focus ring, none of
// which Design/tokens carries. Its night block is applied by preference.
const citizen = readFileSync(join(mirror, 'Citizen.css'), 'utf8');
function blockAfter(selector) {
	const start = citizen.indexOf(`${selector} {`);
	if (start < 0) throw new Error(`Citizen.css: no ${selector} block`);
	const end = citizen.indexOf('\n}', start);
	return citizen.slice(start + selector.length + 2, end);
}
writeFileSync(join(vendor, 'citizen.css'),
	'/* From MediaWiki:Citizen.css by scripts/sync-sources.mjs. */\n' +
	`:root {${blockAfter(':root')}\n}\n\n` +
	`@media (prefers-color-scheme: dark) {\n:root {${blockAfter(':root.skin-theme-clientpref-night')}\n}\n}\n`);
console.log('Citizen.css token blocks → vendor/citizen.css');

// The primitives are Common.css's first block, which ends at the first
// "/* ====" banner (CLAUDE.md: keep the block first).
const common = readFileSync(join(mirror, 'Common.css'), 'utf8').split('\n');
const end = common.findIndex((line, i) => i > 0 && /^\/\* =+/.test(line));
if (end < 0) throw new Error('Common.css: no banner after the primitives block');
writeFileSync(join(vendor, 'primitives.css'), common.slice(0, end).join('\n').trimEnd() + '\n');
console.log(`Common.css lines 1–${end} → vendor/primitives.css`);

// The API terms. Until a version is in force, key creation is refused
// (src/keys.tsx): a "Version …" line that says "draft" is not in force. The
// drafting notes ("## For Alex to decide") are never published.
const terms = join(mirror, 'api/terms.md');
let version = null;
if (existsSync(terms)) {
	const text = readFileSync(terms, 'utf8');
	const line = text.split('\n').find((l) => /^version\b/i.test(l.trim()));
	if (!line) throw new Error('api/terms.md: no "Version …" line to read the version from');
	if (!/draft/i.test(line)) version = line.match(/version\W+([0-9][\w.-]*)/i)?.[1] ?? null;
	writeFileSync(join(vendor, 'terms.md'), text.replace(/\n## For Alex to decide[\s\S]*$/, '').replace(/(\s*\n---)+\s*$/, '') + '\n');
	console.log('api/terms.md → vendor/terms.md');
}
writeFileSync(join(here, 'src/terms-version.ts'),
	'// Written by scripts/sync-sources.mjs from api/terms.md. null: not published yet.\n' +
	`export const TERMS_VERSION: string | null = ${JSON.stringify(version)};\n`);
console.log(`Terms version: ${version ?? 'none yet'}`);
