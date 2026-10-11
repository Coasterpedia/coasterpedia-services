import type { Env } from './env';
import { badRequest, json, LICENCE, problem } from './http';

/**
 * GET /v1/vocabularies (DR-010 §6.2). Open, like /v1/meta: the allowed values
 * behind every vocabulary field the API publishes, as the Projector last
 * validated against them. Stored as sent; one row read.
 */
export async function vocabularies(url: URL, env: Env): Promise<Response> {
	for (const name of url.searchParams.keys()) {
		throw badRequest('parameter-unknown', `Unknown parameter ${name}`);
	}
	const row = await env.DATA.prepare(`SELECT value FROM state WHERE key = 'vocabularies'`).first<{ value: string }>();
	if (!row) {
		return problem(503, 'not-ready', 'Vocabularies are not published yet',
			'They arrive with the next projection pass. Expect values you have not seen in the meantime.');
	}
	const { as_of, data } = JSON.parse(row.value);
	return json({ data, meta: { data_as_of: as_of, licence: LICENCE } });
}
