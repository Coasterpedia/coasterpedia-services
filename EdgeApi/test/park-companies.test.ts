import { env, exports } from 'cloudflare:workers';
import { beforeAll, describe, expect, it } from 'vitest';
import { sha256Hex } from '../src/auth';
import { parkCompanyParksQuery } from '../src/park-companies';
import { resetData } from './reset';
// Written by the Projector's GoldenBatchTests: the real wire shape, not a hand copy.
import batch from './fixtures/projector-park-company-batch.json';
import parks from './fixtures/projector-park-batch.json';

const BASE = 'https://api.coasterpedia.net';
const KEY = 'cpk_parkcompanies';

const get = (path: string) =>
	exports.default.fetch(new Request(BASE + path, { redirect: 'manual', headers: { authorization: `Bearer ${KEY}` } }));

const ingest = (body: unknown) => exports.default.fetch(new Request(`${BASE}/internal/ingest`, {
	method: 'POST',
	headers: { 'content-type': 'application/json', 'x-dev-ingest-token': 'test-ingest-token' },
	body: JSON.stringify(body),
}));

const ids = async (q: string) => (await (await get(`/v1/park-companies?${q}`)).json<any>()).data.map((c: any) => c.id);
const parksOf = async (id: number, q = '') => (await (await get(`/v1/park-companies/${id}/parks?${q}`)).json<any>()).data;

const cedarPoint = parks.upsert.find((u) => u.id === 182)!;

describe('the Projector park company batch', () => {
	beforeAll(async () => {
		await resetData();
		await env.KEYS.put(`key:${await sha256Hex(KEY)}`, JSON.stringify({ account: 'park_companies', tier: 'beta', status: 'active' }));
		expect(await (await ingest(batch)).json()).toEqual({ upserted: 5, deleted: 1, promoted: 1, discarded: 1 });
		expect((await ingest(parks)).status).toBe(200);
	});

	it('lists park companies in name order, in each view', async () => {
		const body = await (await get('/v1/park-companies?view=latest')).json<any>();
		expect(body.data.map((c: any) => c.name)).toEqual([
			'Carnival Cruise Line', 'Cedar Fair', 'Herschend', 'Merlin Entertainments', 'Six Flags',
		]);
		// Herschend waits to settle.
		expect(await ids('')).toEqual([66330, 823, 5800, 821]);
		expect(await ids('view=latest&sort=-name')).toEqual([821, 5800, 7906, 823, 66330]);
	});

	it('lists the full record and filters by status and country', async () => {
		const body = await (await get('/v1/park-companies?view=latest&status=defunct')).json<any>();
		expect(body.data).toEqual([batch.upsert.find((u) => u.id === 823)!.summary]);
		expect(body.data[0]).toMatchObject({ established: { edtf: '1983' }, closed: { edtf: '2024' } });
		expect(await ids('view=latest&country=gb')).toEqual([5800]);
	});

	it('serves one record, redirects a merged one, and 404s the rest', async () => {
		const one = await (await get('/v1/park-companies/5800')).json<any>();
		expect(one.data).toMatchObject({ name: 'Merlin Entertainments', established: { edtf: '1998-12' } });
		expect(one.data.settlement).toEqual({ settled_revision: 703, pending_revision: null, settles_at: null });

		const merged = await get('/v1/park-companies/99301?view=latest');
		expect(merged.status).toBe(301);
		expect(merged.headers.get('location')).toBe(`${BASE}/v1/park-companies/821?view=latest`);

		expect((await get('/v1/park-companies/182')).status).toBe(404);
		expect((await get('/v1/park-companies/7906')).status).toBe(404); // not settled yet
		expect((await get('/v1/park-companies/7906?view=latest')).status).toBe(200);
	});

	it('counts park companies in /v1/meta, per view', async () => {
		const latest = await (await exports.default.fetch(new Request(`${BASE}/v1/meta?view=latest`))).json<any>();
		expect(latest.counts.park_companies).toBe(5);
		const settled = await (await exports.default.fetch(new Request(`${BASE}/v1/meta`))).json<any>();
		expect(settled.counts.park_companies).toBe(4);
	});

	it('lists a company\'s parks from the parks\' history, one item per spell', async () => {
		expect(await parksOf(821)).toEqual([{
			park: { id: 182, name: 'Cedar Point' }, role: 'owner',
			start: { edtf: '2024', earliest: '2024-01-01', latest: '2024-12-31' }, end: null, current: true,
		}]);
		expect(await parksOf(823)).toEqual([{
			park: { id: 182, name: 'Cedar Point' }, role: 'owner',
			start: { edtf: '1983', earliest: '1983-01-01', latest: '1983-12-31' },
			end: { edtf: '2024', earliest: '2024-01-01', latest: '2024-12-31' }, current: false,
		}]);
		// A company still waiting to settle has its parks listed: the parks are what the view filters.
		expect((await parksOf(7906)).map((p: any) => p.park.name)).toEqual(['Dollywood']);
		expect((await parksOf(66330)).map((p: any) => p.park.name)).toEqual(['Carnival Horizon']);
		// Merlin is in no park's history in this batch: an empty list, not a 404.
		expect(await parksOf(5800)).toEqual([]);
	});

	it('filters by role and by whether the spell is in force', async () => {
		expect(await parksOf(821, 'role=operator')).toEqual([]);
		expect((await parksOf(821, 'role=owner&include=current')).length).toBe(1);
		expect(await parksOf(821, 'include=former')).toEqual([]);
		expect((await parksOf(823, 'include=former')).length).toBe(1);
		expect(await parksOf(823, 'include=current')).toEqual([]);
	});

	it('refuses what it cannot answer', async () => {
		expect((await get('/v1/park-companies/821/parks?role=landlord')).status).toBe(400);
		expect((await get('/v1/park-companies/821/parks?include=some')).status).toBe(400);
		expect((await get('/v1/park-companies/821/parks?limit=5')).status).toBe(400);
		expect((await get('/v1/park-companies?q=s')).status).toBe(400);
		expect((await get('/v1/park-companies/182/parks')).status).toBe(404); // a park, not a company
		expect((await get('/v1/park-companies/abc/parks')).status).toBe(404);
		const merged = await get('/v1/park-companies/99301/parks?role=owner');
		expect(merged.status).toBe(301);
		expect(merged.headers.get('location')).toBe(`${BASE}/v1/park-companies/821/parks?role=owner`);
	});

	it('follows a park edit: its history replaces the old links, in both views', async () => {
		// Cedar Point's latest edit records Cedar Fair as the operator too, and waits to settle.
		const edited = structuredClone(cedarPoint) as any;
		edited.revision = 900;
		edited.settled = false;
		edited.settles_at = '2026-10-20T12:00:00Z';
		edited.record.history.operators = [{ company: { id: 823, name: 'Cedar Fair' }, start: null, end: null, current: true }];
		expect((await ingest({ as_of: '2026-10-13T13:00:00Z', upsert: [edited], delete: [] })).status).toBe(200);

		expect((await parksOf(823, 'view=latest')).map((p: any) => p.role)).toEqual(['owner', 'operator']);
		expect((await parksOf(823)).map((p: any) => p.role)).toEqual(['owner']);

		// Back to the settled content, then gone: no links are left behind.
		expect((await ingest({ as_of: '2026-10-13T14:00:00Z', upsert: [], delete: [], discard: [{ kind: 'park', id: 182 }] })).status).toBe(200);
		expect((await parksOf(823, 'view=latest')).map((p: any) => p.role)).toEqual(['owner']);
		expect((await ingest({ as_of: '2026-10-13T15:00:00Z', upsert: [], delete: [{ kind: 'park', id: 182, merged_into: null }] })).status).toBe(200);
		expect(await parksOf(823, 'view=latest')).toEqual([]);
		expect(await parksOf(821)).toEqual([]);
	});
});

describe('a company\'s parks read its own links', () => {
	it('plans on the link table\'s key, not a scan', async () => {
		const { sql, binds } = parkCompanyParksQuery(new URL(`${BASE}/v1/park-companies/821/parks?include=current`), 821);
		const plan = await env.DATA.prepare(`EXPLAIN QUERY PLAN ${sql}`).bind(...binds).all<{ detail: string }>();
		const details = plan.results.map((r) => r.detail).join('\n');
		expect(details).toMatch(/SEARCH park_company_parks USING PRIMARY KEY \(company=\?\)/);
		expect(details).not.toMatch(/SCAN park_company_parks/);
	});
});
