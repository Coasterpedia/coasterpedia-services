import { badRequest, isoSeconds } from './http';

/**
 * The list parameters that every kind shares, or that need more than a column
 * (DR-010 §12.19): updated_since, bbox and q. Each parses here and returns SQL
 * fragments; the list builders decide which index drives.
 */

/**
 * updated_since: an RFC 3339 time, to the second, as updated_at is stored
 * ("2026-10-13T12:00:00Z"). Inclusive. Null when absent.
 */
export function updatedSinceParam(url: URL): string | null {
	const raw = url.searchParams.get('updated_since');
	if (raw == null) return null;
	const ms = /^\d{4}-\d\d-\d\dT\d\d:\d\d(:\d\d(\.\d+)?)?(Z|[+-]\d\d:\d\d)$/.test(raw.trim()) ? Date.parse(raw.trim()) : NaN;
	if (Number.isNaN(ms)) {
		throw badRequest('parameter-invalid', 'updated_since takes a time with its offset, 2026-10-13T12:00:00Z');
	}
	return isoSeconds(new Date(ms));
}

export interface Box {
	south: number;
	west: number;
	north: number;
	east: number;
}

/**
 * The widest box answered, in degrees each way. A box reads the points inside
 * it; 20° holds any one European country with room to spare, and stops a box
 * the size of a continent being paged through a page at a time.
 */
export const MAX_BOX_DEGREES = 20;

/** bbox: south,west,north,east in decimal degrees. west > east crosses the antimeridian. */
export function bboxParam(url: URL): Box | null {
	const raw = url.searchParams.get('bbox');
	if (raw == null) return null;
	const parts = raw.split(',').map((v) => Number(v.trim()));
	const [south, west, north, east] = parts;
	const ok = parts.length === 4 && parts.every((v) => Number.isFinite(v))
		&& south >= -90 && north <= 90 && south <= north
		&& west >= -180 && west <= 180 && east >= -180 && east <= 180;
	if (!ok) {
		throw badRequest('parameter-invalid', 'bbox is south,west,north,east in decimal degrees',
			'Latitudes from -90 to 90 with south ≤ north; longitudes from -180 to 180. A box over the antimeridian has west > east.');
	}
	const width = west <= east ? east - west : east + 360 - west;
	if (north - south > MAX_BOX_DEGREES || width > MAX_BOX_DEGREES) {
		throw badRequest('parameter-invalid', `bbox is at most ${MAX_BOX_DEGREES}° each way`,
			'Narrow the box, or filter by country or state for a larger area.');
	}
	return { south, west, north, east };
}

/** The 1° grid squares (migration 0014's cell) the box touches. */
export function boxCells(b: Box): number[] {
	const row = (lat: number) => Math.min(Math.floor(lat + 90), 179);
	const col = (lon: number) => Math.min(Math.floor(lon + 180), 359);
	const cols: number[] = [];
	if (b.west <= b.east) {
		for (let c = col(b.west); c <= col(b.east); c++) cols.push(c);
	} else {
		for (let c = col(b.west); c <= 359; c++) cols.push(c);
		for (let c = 0; c <= col(b.east); c++) cols.push(c);
	}
	const cells: number[] = [];
	for (let r = row(b.south); r <= row(b.north); r++) {
		for (const c of cols) cells.push(r * 360 + c);
	}
	return cells;
}

/** The box as a filter on lat and lon (named by the caller, for a join's alias or a unary +). */
export function boxWhere(b: Box, lat: string, lon: string): { sql: string; binds: number[] } {
	const lons = b.west <= b.east ? `${lon} BETWEEN ? AND ?` : `(${lon} >= ? OR ${lon} <= ?)`;
	return { sql: `${lat} BETWEEN ? AND ? AND ${lons}`, binds: [b.south, b.north, b.west, b.east] };
}

/** At most this many words of q count; the rest add nothing a client needs. */
const MAX_WORDS = 6;

/**
 * q as an FTS5 query: each word of two or more letters a prefix, all of them
 * required ("bat ride" finds Batman The Ride). Case and accents are folded by
 * the index's tokenizer, so "elan" finds Élan. A one-letter word alone would
 * match half the wiki, so it is refused.
 */
export function searchMatch(url: URL, name = 'q', required = false): string | null {
	const raw = url.searchParams.get(name);
	if (raw == null) {
		if (required) throw badRequest('parameter-required', `${name} is required`);
		return null;
	}
	const words = raw.normalize('NFKC').split(/[^\p{L}\p{N}]+/u).filter((w) => w.length >= 2).slice(0, MAX_WORDS);
	if (raw.length > 200 || words.length === 0) {
		throw badRequest('parameter-invalid', `${name} needs a word of at least two letters`,
			'Each word matches the start of a word in a current or past name, case- and accent-blind.');
	}
	return words.map((w) => `"${w.replace(/"/g, '""')}"*`).join(' ');
}

/**
 * Records of one kind whose current or past name matches, as a filter on the
 * row's key (id and in_latest, named by the caller). The match is read once,
 * then each row is a key lookup.
 */
export function searchWhere(kind: string, id: string, latest: string, match: string): { sql: string; binds: string[] } {
	return {
		sql: `(${id}, ${latest}) IN (SELECT n.id, n.in_latest FROM name_fts JOIN search_names n ON n.rid = name_fts.rowid
			WHERE name_fts MATCH ? AND n.kind = '${kind}')`,
		binds: [match],
	};
}
