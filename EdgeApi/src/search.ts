import { checkParams, FLAG, limitParam, list, viewParam, withDataAsOf } from './attractions';
import type { Env } from './env';
import { searchMatch } from './filters';
import { badRequest, json, LICENCE } from './http';

const PARAMS = new Set(['q', 'types', 'view', 'limit']);

/** The kinds search_names holds (migration 0014), as /v1/changes names them. */
const KINDS = new Set(['attraction', 'park', 'park_company', 'manufacturer', 'product']);

/**
 * A record with several matching names (current and past) has a row for each;
 * reading this many per result leaves room to keep one per record and still
 * fill the page.
 */
const ROWS_PER_RESULT = 4;

interface Row {
	kind: string;
	id: number;
	display: string;
	name: string;
	current: number;
	url: string | null;
}

/** The query for a request, apart from running it, so tests can measure what it reads. */
export function searchQuery(url: URL): { sql: string; binds: (string | number)[]; limit: number } {
	checkParams(url, PARAMS, new Set());
	const view = viewParam(url);
	const limit = limitParam(url);
	const match = searchMatch(url, 'q', true)!;
	const kinds = list(url, 'types');
	for (const kind of kinds) {
		if (!KINDS.has(kind)) {
			throw badRequest('parameter-invalid', `types=${kind} is not a kind`, `Kinds: ${[...KINDS].join(', ')}.`);
		}
	}

	const where = ['name_fts MATCH ?', `n.${FLAG[view]} = 1`];
	const binds: (string | number)[] = [match];
	if (kinds.length > 0) {
		where.push(`n.kind IN (${kinds.map(() => '?').join(',')})`);
		binds.push(...kinds);
	}
	// A record's own name before a past one, then FTS5's relevance, then by name.
	const sql = `SELECT n.kind, n.id, n.display, n.name, n.current, n.url
		FROM name_fts JOIN search_names n ON n.rid = name_fts.rowid
		WHERE ${where.join(' AND ')}
		ORDER BY n.current DESC, bm25(name_fts), n.sort_name, n.kind, n.id LIMIT ?`;
	return { sql, binds: [...binds, limit * ROWS_PER_RESULT], limit };
}

/**
 * GET /v1/search (DR-010 §12.19): every kind by current and past names, each
 * word of q matching the start of a word. One result per record, with matched
 * saying which name it was found by.
 */
export async function search(url: URL, env: Env): Promise<{ response: Response; rows: number }> {
	const { sql, binds, limit } = searchQuery(url);
	const { results, asOf } = await withDataAsOf<Row>(env, env.DATA.prepare(sql).bind(...binds));

	const seen = new Set<string>();
	const data: Record<string, unknown>[] = [];
	for (const r of results) {
		const key = `${r.kind}:${r.id}`;
		if (seen.has(key)) continue;
		seen.add(key);
		data.push({ kind: r.kind, id: r.id, name: r.display, matched: r.name, url: r.url });
		if (data.length === limit) break;
	}

	return {
		rows: data.length,
		response: json({ data, meta: { data_as_of: asOf, view: viewParam(url), licence: LICENCE } }),
	};
}
