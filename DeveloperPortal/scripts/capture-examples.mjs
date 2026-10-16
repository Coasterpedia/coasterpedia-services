// Capture a real response from the live API for each endpoint in the
// reference, into vendor/examples.json, which the build shows under each
// endpoint. Real responses show what the API serves today, including which
// fields are published yet; examples written by hand would drift.
//
//   COASTERPEDIA_KEY=cpk_… npm run examples
//
// Run it after a schema version adds or changes fields, and commit the file.
import { writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const key = process.env.COASTERPEDIA_KEY;
if (!key) {
	console.error('Set COASTERPEDIA_KEY to an API key.');
	process.exit(1);
}

const API = 'https://api.coasterpedia.net';
const since = new Date(Date.now() - 86400_000).toISOString().replace(/\.\d{3}Z$/, 'Z');

// Well-known records, so a reader recognises them: Nemesis Reborn (3173) at
// Alton Towers (2027), Bolliger & Mabillard (543), its Inverted Coaster
// product (1498), and Merlin Entertainments (5800).
const REQUESTS = {
	'GET /attractions': '/v1/attractions?park=2027&sort=-height&limit=2',
	'GET /attractions/facets': '/v1/attractions/facets?park=2027',
	'GET /attractions/{id}': '/v1/attractions/3173',
	'GET /parks': '/v1/parks?country=GB&limit=2',
	'GET /parks/{id}': '/v1/parks/2027',
	'GET /parks/{id}/attractions': '/v1/parks/2027/attractions?limit=2',
	'GET /park-companies': '/v1/park-companies?limit=2',
	'GET /park-companies/{id}': '/v1/park-companies/5800',
	'GET /park-companies/{id}/parks': '/v1/park-companies/5800/parks',
	'GET /manufacturers': '/v1/manufacturers?limit=2',
	'GET /manufacturers/{id}': '/v1/manufacturers/543',
	'GET /manufacturers/{id}/products': '/v1/manufacturers/543/products?limit=2',
	'GET /products': '/v1/products?limit=2',
	'GET /products/{id}': '/v1/products/1498',
	'GET /products/{id}/installations': '/v1/products/1498/installations?limit=2',
	'GET /search': '/v1/search?q=nemesis&limit=3',
	'GET /changes': `/v1/changes?since=${since}&limit=2`,
	'GET /vocabularies': '/v1/vocabularies',
	'GET /meta': '/v1/meta',
};

/** Long lists cut to three items, so an example stays readable. */
const MAX_ITEMS = 3;

function shorten(value, state) {
	if (Array.isArray(value)) {
		if (value.length > MAX_ITEMS) state.shortened = true;
		return value.slice(0, MAX_ITEMS).map((v) => shorten(v, state));
	}
	if (value && typeof value === 'object') {
		return Object.fromEntries(Object.entries(value).map(([k, v]) => [k, shorten(v, state)]));
	}
	return value;
}

const examples = {};
let schemaVersion = null;
for (const [operation, path] of Object.entries(REQUESTS)) {
	const res = await fetch(API + path, {
		headers: { authorization: `Bearer ${key}`, 'user-agent': 'CoasterpediaDeveloperPortal-examples/1.0 (https://developer.coasterpedia.net)' },
	});
	if (res.status !== 200) throw new Error(`${operation} ${path}: ${res.status} ${await res.text()}`);
	const body = await res.json();
	if (operation === 'GET /meta') schemaVersion = body.schema_version;
	const state = { shortened: false };
	examples[operation] = { request: path, shortened: false, body: shorten(body, state) };
	examples[operation].shortened = state.shortened;
	console.log(`${operation}: ${path}${state.shortened ? ' (shortened)' : ''}`);
	// Well inside the 60 a minute.
	await new Promise((r) => setTimeout(r, 1100));
}

const out = join(dirname(dirname(fileURLToPath(import.meta.url))), 'vendor/examples.json');
writeFileSync(out, JSON.stringify({ captured_at: new Date().toISOString().replace(/\.\d{3}Z$/, 'Z'), schema_version: schemaVersion, examples }, null, '\t') + '\n');
console.log(`wrote vendor/examples.json (${schemaVersion})`);
