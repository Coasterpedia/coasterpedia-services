// The API reference, rendered from vendor/openapi.yaml at build time
// (DR-010 §13.9): HTML in the portal's layout, and a Markdown twin for
// agents. No viewer: "try it" can't work without CORS, and a client-side
// viewer is worse for agents than plain HTML.
import { Marked } from 'marked';
import { parse } from 'yaml';
import { raw } from 'hono/html';

type Schema = Record<string, any>;
interface Spec {
	info: { title: string; version: string; description?: string };
	servers: { url: string }[];
	paths: Record<string, Record<string, Schema>>;
	components: { schemas: Record<string, Schema>; parameters: Record<string, Schema>; responses: Record<string, Schema> };
}

/** The site's own browser door: not for third parties (its description says so). */
const UNLISTED_PATHS = new Set(['/session']);

const GROUPS: { title: string; prefix: string }[] = [
	{ title: 'Attractions', prefix: '/attractions' },
	{ title: 'Parks', prefix: '/parks' },
	{ title: 'Park companies', prefix: '/park-companies' },
	{ title: 'Manufacturers', prefix: '/manufacturers' },
	{ title: 'Products', prefix: '/products' },
	{ title: 'Search', prefix: '/search' },
	{ title: 'Change feed', prefix: '/changes' },
	{ title: 'Vocabularies', prefix: '/vocabularies' },
	{ title: 'Meta', prefix: '/meta' },
];

const md = new Marked({ gfm: true });

/** Real responses, captured by scripts/capture-examples.mjs into vendor/examples.json. */
export interface Examples {
	captured_at: string;
	schema_version: string;
	examples: Record<string, { request: string; shortened: boolean; body: unknown }>;
}

const exampleOf = (examples: Examples | null, op: { method: string; path: string }) => examples?.examples[`${op.method} ${op.path}`];

/** Design-record pointers are for maintainers; the public text drops them. */
export function publicText(text: string | undefined): string {
	if (!text) return '';
	return text
		.replace(/\s*\((?:[^()]*?[;,]\s*)?(?:DR-0\d\d|MediaWiki:|Module:)[^()]*\)/g, '')
		.replace(/\s*\bDR-0\d\d(?: §[\d.]+)?/g, '')
		.replace(/\s+([.,;:])/g, '$1')
		.trim();
}

const inline = (text: string | undefined) => md.parseInline(publicText(text)) as string;
const block = (text: string | undefined) => md.parse(publicText(text)) as string;

export function loadSpec(text: string): Spec {
	return parse(text) as Spec;
}

function resolve(spec: Spec, schema: Schema | undefined): Schema {
	if (!schema?.$ref) return schema ?? {};
	const [, , kind, name] = (schema.$ref as string).split('/');
	return (spec.components as any)[kind]?.[name] ?? {};
}

const refName = (ref: string) => ref.split('/').pop()!;
const anchor = (text: string) => text.toLowerCase().replace(/[{}]/g, '').replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '');

/** A type in words, schema names linked (html) or bare (markdown). */
function typeOf(schema: Schema | undefined, link: boolean): string {
	if (!schema) return '';
	if (schema.$ref) {
		const name = refName(schema.$ref);
		return link ? `<a href="#schema-${anchor(name)}">${name}</a>` : name;
	}
	if (schema.const !== undefined) return JSON.stringify(schema.const);
	if (schema.enum) return `one of ${schema.enum.map((v: unknown) => (link ? `<code>${v}</code>` : `\`${v}\``)).join(', ')}`;
	for (const key of ['oneOf', 'anyOf'] as const) {
		if (schema[key]) return (schema[key] as Schema[]).map((s) => typeOf(s, link)).join(' or ');
	}
	if (schema.allOf) return (schema.allOf as Schema[]).map((s) => typeOf(s, link)).filter(Boolean).join(' and ');
	const types: string[] = Array.isArray(schema.type) ? schema.type : schema.type ? [schema.type] : [];
	const words = types.map((t) => {
		if (t === 'array') return `array of ${typeOf(schema.items, link) || 'values'}`;
		if (t === 'null') return 'null';
		return schema.format ? `${t} (${schema.format})` : t;
	});
	return words.join(' or ');
}

interface Property { name: string; required: boolean; schema: Schema }

/** An object's properties, allOf members merged in order. */
function propertiesOf(spec: Spec, schema: Schema): Property[] {
	const out: Property[] = [];
	const walk = (s: Schema) => {
		const r = resolve(spec, s);
		for (const member of r.allOf ?? []) walk(member);
		const required = new Set<string>(r.required ?? []);
		for (const [name, prop] of Object.entries(r.properties ?? {})) {
			const at = out.findIndex((p) => p.name === name);
			const entry = { name, required: required.has(name), schema: prop as Schema };
			if (at >= 0) out[at] = entry;
			else out.push(entry);
		}
	};
	walk(schema);
	return out;
}

function propertyNote(schema: Schema): string[] {
	const notes: string[] = [];
	if (schema.description) notes.push(publicText(schema.description));
	if (schema['x-known-values']) notes.push(`Known values today: ${(schema['x-known-values'] as unknown[]).map((v) => `\`${v}\``).join(', ')}. Expect others.`);
	if (schema['x-beta']) notes.push(`During the beta: ${publicText(schema['x-beta'])}`);
	if (schema.default !== undefined) notes.push(`Default \`${schema.default}\`.`);
	if (schema.minimum !== undefined || schema.maximum !== undefined) notes.push(`From ${schema.minimum ?? '…'} to ${schema.maximum ?? '…'}.`);
	return notes;
}

interface Operation {
	method: string;
	path: string;
	summary: string;
	description?: string;
	open: boolean;
	params: { name: string; in: string; required: boolean; schema: Schema; description?: string; beta?: string }[];
	responses: { status: string; description: string; schema?: Schema }[];
	beta?: string;
}

function operations(spec: Spec): Operation[] {
	const ops: Operation[] = [];
	for (const [path, item] of Object.entries(spec.paths)) {
		if (UNLISTED_PATHS.has(path)) continue;
		for (const [method, op] of Object.entries(item)) {
			const params = ((op.parameters ?? []) as Schema[]).map((p) => {
				const r = resolve(spec, p);
				return { name: r.name, in: r.in, required: !!r.required, schema: r.schema ?? {}, description: r.description, beta: r['x-beta'] };
			});
			const responses = Object.entries((op.responses ?? {}) as Record<string, Schema>).map(([status, res]) => {
				const r = resolve(spec, res);
				const content = (r.content ?? {}) as Record<string, Schema>;
				const json = content['application/json']?.schema;
				return { status, description: res.description ?? r.description ?? '', schema: json };
			});
			ops.push({
				method: method.toUpperCase(),
				path,
				summary: op.summary ?? '',
				description: op.description,
				open: Array.isArray(op.security) && op.security.length === 0,
				params,
				responses,
				beta: op['x-beta'],
			});
		}
	}
	return ops;
}

/** The schema a 200 returns as data, when it names one. */
function dataSchema(spec: Spec, schema: Schema | undefined): string | null {
	if (!schema) return null;
	if (schema.$ref) return refName(schema.$ref);
	const data = schema.properties?.data;
	if (data?.$ref) return refName(data.$ref);
	if (data?.items?.$ref) return `${refName(data.items.$ref)} (a list)`;
	return null;
}

/** The example's own request where there is one, so the command and the response match. */
function curl(spec: Spec, op: Operation, examples: Examples | null): string {
	const auth = op.open ? '' : ' -H "Authorization: Bearer $COASTERPEDIA_KEY"';
	const example = exampleOf(examples, op);
	const url = example ? new URL(spec.servers[0].url).origin + example.request : spec.servers[0].url + op.path;
	return `curl${auth} "${url}"`;
}

const groupOf = (op: Operation) => GROUPS.find((g) => op.path === g.prefix || op.path.startsWith(`${g.prefix}/`))?.title ?? 'Other';

// ── HTML ───────────────────────────────────────────────────────────────────

function Params({ op }: { op: Operation }) {
	if (op.params.length === 0) return null;
	return (
		<div class="table-scroll">
			<table>
				<thead><tr><th>Parameter</th><th>Type</th><th>What it does</th></tr></thead>
				<tbody>
					{op.params.map((p) => (
						<tr>
							<td><code>{p.name}</code>{p.in === 'path' ? ' (path)' : ''}{p.required ? ', required' : ''}</td>
							<td>{raw(typeOf(p.schema, true))}</td>
							<td>{raw(inline([p.description, ...propertyNote({ ...p.schema, description: undefined }), p.beta ? `During the beta: ${p.beta}` : ''].filter(Boolean).join(' ')))}</td>
						</tr>
					))}
				</tbody>
			</table>
		</div>
	);
}

function ExampleHtml({ example }: { example: Examples['examples'][string] }) {
	return (
		<details class="example">
			<summary>Example response</summary>
			{example.shortened && <p class="meta">We’ve shortened the lists in this example to three items each.</p>}
			<pre><code>{JSON.stringify(example.body, null, 2)}</code></pre>
		</details>
	);
}

function OperationHtml({ spec, op, examples }: { spec: Spec; op: Operation; examples: Examples | null }) {
	const example = exampleOf(examples, op);
	const id = anchor(`${op.method} ${op.path}`);
	const ok = op.responses.find((r) => r.status === '200');
	const returns = dataSchema(spec, ok?.schema);
	return (
		<section id={id}>
			<h3 class="endpoint">
				<span class="t-badge">{op.method}</span>
				<code>{op.path}</code>
			</h3>
			<p><strong>{op.summary}.</strong> {op.open ? 'You don’t need a key for this.' : 'You need a key for this.'}</p>
			{op.description && raw(block(op.description))}
			{op.beta && <p>During the beta: {raw(inline(op.beta))}</p>}
			<Params op={op} />
			{returns && <p>Returns {raw(typeOf({ $ref: `#/components/schemas/${returns.replace(' (a list)', '')}` }, true))}{returns.endsWith('(a list)') ? ' records, as a list in data, with meta.next_cursor for the next page' : ' in data'}.</p>}
			{op.responses.filter((r) => !['200', 'default', '429'].includes(r.status)).length > 0 && (
				<ul>
					{op.responses.filter((r) => !['200', 'default', '429'].includes(r.status)).map((r) => (
						<li><strong>{r.status}</strong>: {raw(inline(r.description))}</li>
					))}
				</ul>
			)}
			<pre><code>{curl(spec, op, examples)}</code></pre>
			{example && <ExampleHtml example={example} />}
		</section>
	);
}

function SchemaHtml({ spec, name }: { spec: Spec; name: string }) {
	const schema = spec.components.schemas[name];
	const props = propertiesOf(spec, schema);
	const plain = !props.length;
	return (
		<section id={`schema-${anchor(name)}`}>
			<h3>{name}</h3>
			{schema.description && raw(block(schema.description))}
			{plain && <p>{raw(typeOf(schema, true))}{schema['x-known-values'] ? '. ' : ''}{raw(inline(propertyNote({ ...schema, description: undefined }).join(' ')))}</p>}
			{!plain && (
				<div class="table-scroll">
					<table>
						<thead><tr><th>Field</th><th>Type</th><th>Meaning</th></tr></thead>
						<tbody>
							{props.map((p) => (
								<tr>
									<td><code>{p.name}</code>{p.required ? ', always' : ''}</td>
									<td>{raw(typeOf(p.schema, true))}</td>
									<td>{raw(inline(propertyNote(p.schema).join(' ')))}</td>
								</tr>
							))}
						</tbody>
					</table>
				</div>
			)}
		</section>
	);
}

export function ReferenceHtml({ spec, examples }: { spec: Spec; examples: Examples | null }) {
	const ops = operations(spec);
	const schemaNames = Object.keys(spec.components.schemas);
	return (
		<>
			<h1>API reference</h1>
			<p>
				Every endpoint and record in version <code>{spec.info.version}</code> of the contract, generated from
				{' '}<a href="/openapi.yaml">the OpenAPI file</a>. Requests go to <code>{spec.servers[0].url}</code>.
				If a field is missing, we don’t know its value, either because the wiki doesn’t record it or because we haven’t published that field yet.
			</p>
			{examples && (
				<p>
					Each endpoint has an example response. They’re real responses from the API, which we captured on {examples.captured_at.slice(0, 10)}, at version <code>{examples.schema_version}</code>.
				</p>
			)}
			<h2 id="endpoints">Endpoints</h2>
			{GROUPS.map((g) => {
				const inGroup = ops.filter((op) => groupOf(op) === g.title);
				if (!inGroup.length) return null;
				return (
					<>
						<h2 id={anchor(g.title)}>{g.title}</h2>
						{inGroup.map((op) => <OperationHtml spec={spec} op={op} examples={examples} />)}
					</>
				);
			})}
			<h2 id="records">Records</h2>
			<p>The shapes the endpoints return, and the types they’re built from.</p>
			{schemaNames.map((name) => <SchemaHtml spec={spec} name={name} />)}
		</>
	);
}

// ── Markdown ───────────────────────────────────────────────────────────────

const cell = (text: string) => text.replace(/\|/g, '\\|').replace(/\s*\n\s*/g, ' ');

export function referenceMarkdown(spec: Spec, examples: Examples | null): string {
	const lines: string[] = [
		'# Coasterpedia API reference',
		'',
		`Contract version ${spec.info.version}. Requests go to ${spec.servers[0].url}. Send a key as \`Authorization: Bearer cpk_…\`; get one at https://developer.coasterpedia.net. The machine-readable contract is https://developer.coasterpedia.net/openapi.yaml.`,
		'',
		'If a field is missing, we don’t know its value, either because the wiki doesn’t record it or because we haven’t published that field yet.',
		'',
		...(examples ? [`Each endpoint has an example response. They're real responses from the API, captured on ${examples.captured_at.slice(0, 10)} at version ${examples.schema_version}.`, ''] : []),
	];
	const ops = operations(spec);
	for (const g of GROUPS) {
		const inGroup = ops.filter((op) => groupOf(op) === g.title);
		if (!inGroup.length) continue;
		lines.push(`## ${g.title}`, '');
		for (const op of inGroup) {
			lines.push(`### ${op.method} ${op.path}`, '', `${op.summary}. ${op.open ? 'You don’t need a key for this.' : 'You need a key for this.'}`, '');
			if (op.description) lines.push(publicText(op.description), '');
			if (op.params.length) {
				lines.push('| Parameter | Type | What it does |', '| --- | --- | --- |');
				for (const p of op.params) {
					const note = [p.description, ...propertyNote({ ...p.schema, description: undefined }), p.beta ? `During the beta: ${p.beta}` : ''].filter(Boolean).join(' ');
					lines.push(`| \`${p.name}\`${p.in === 'path' ? ' (path)' : ''}${p.required ? ', required' : ''} | ${cell(typeOf(p.schema, false))} | ${cell(publicText(note))} |`);
				}
				lines.push('');
			}
			const returns = dataSchema(spec, op.responses.find((r) => r.status === '200')?.schema);
			if (returns) lines.push(`Returns ${returns}.`, '');
			for (const r of op.responses.filter((r) => !['200', 'default', '429'].includes(r.status))) lines.push(`- ${r.status}: ${publicText(r.description)}`);
			lines.push('', '```sh', curl(spec, op, examples), '```', '');
			const example = exampleOf(examples, op);
			if (example) {
				lines.push(example.shortened ? 'Example response, with lists shortened to three items:' : 'Example response:', '', '```json', JSON.stringify(example.body, null, 2), '```', '');
			}
		}
	}
	lines.push('## Records', '');
	for (const [name, schema] of Object.entries(spec.components.schemas)) {
		lines.push(`### ${name}`, '');
		if (schema.description) lines.push(publicText(schema.description), '');
		const props = propertiesOf(spec, schema);
		if (!props.length) {
			lines.push(`${typeOf(schema, false)}. ${propertyNote({ ...schema, description: undefined }).join(' ')}`.trim(), '');
			continue;
		}
		lines.push('| Field | Type | Meaning |', '| --- | --- | --- |');
		for (const p of props) lines.push(`| \`${p.name}\`${p.required ? ', always' : ''} | ${cell(typeOf(p.schema, false))} | ${cell(propertyNote(p.schema).join(' '))} |`);
		lines.push('');
	}
	return lines.join('\n');
}
