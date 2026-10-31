// Rows from D1 accounts (migrations/0001_accounts.sql), and the shared
// vocabulary the pages use about them.

export const PLATFORMS = [
	{ slug: 'web', label: 'Web' },
	{ slug: 'ios', label: 'iOS' },
	{ slug: 'android', label: 'Android' },
	{ slug: 'discord_bot', label: 'Discord bot' },
	{ slug: 'library', label: 'Library' },
	{ slug: 'other', label: 'Other' },
] as const;

export const platformLabel = (slug: string) => PLATFORMS.find((p) => p.slug === slug)?.label ?? slug;

/** What a key is for, asked when it's created. `public` kinds need a link unless the project isn't public yet. */
export const KINDS = [
	{ slug: 'website', label: 'A website', public: true },
	{ slug: 'app', label: 'An app for phones or computers', public: true },
	{ slug: 'bot', label: 'A bot, such as for Discord', public: true },
	{ slug: 'personal', label: 'Something only I use, such as a ride log or a spreadsheet', public: false },
	{ slug: 'trying', label: 'I’m trying the API out', public: false },
	{ slug: 'other', label: 'Something else', public: false },
] as const;

export const kindOf = (slug: string | null) => KINDS.find((k) => k.slug === slug);

/** The terms' commercial test, as one question. 'yes' can't have a free key. */
export const COMMERCIAL = [
	{ slug: 'no', label: 'No' },
	{ slug: 'later', label: 'Not yet, but it might later' },
	{ slug: 'yes', label: 'Yes' },
] as const;

/**
 * Kinds that should make few requests. Above this many a day, on average, the
 * reviewer pages suggest a look: a ride log reads a few records, not a park
 * list every hour.
 */
export const LIGHT_KINDS = ['personal', 'trying'];
export const LIGHT_PER_DAY = 200;

/** The API's burst limit per account; EdgeApi/src/ratelimit.ts BURST says the same. */
export const REQUESTS_PER_MINUTE = 60;

/** Active keys per account: enough for any project, few enough to notice a farm. */
export const MAX_KEYS = 10;

/** We hide a listing after this long without a request (§13.4). */
export const HIDE_AFTER_DAYS = 90;

/** What to fix, worded the same on the key form and the listing form. */
export const FIX = {
	name: 'Give the project a name of up to 40 characters.',
	description: 'Describe the project in one line of up to 100 characters.',
	terms: 'Accept the API terms to create a key.',
	kind: 'Choose what you’re building.',
	keyLink: 'Add a link to the project that starts with https://, or tick “It isn’t public yet”.',
	optionalLink: 'Make the link start with https://, or leave it empty.',
	commercial: 'Say whether the project will make money.',
	link: 'Add a link to the project that starts with https://.',
	credit: 'Add a link to where the project credits Coasterpedia, starting with https://.',
	platform: 'Choose the platform it runs on.',
};

export interface KeyRow {
	id: string;
	account_id: number;
	prefix: string;
	name: string;
	description: string;
	created_at: string;
	revoked_at: string | null;
	last_used_at: string | null;
	uses: string | null;
	/** Null on keys made before migration 0003. */
	kind: string | null;
	link: string | null;
	commercial: string | null;
	asked_at: string | null;
}

/** The fields a developer fills in; also the shape of a pending edit. */
export interface ListingFields {
	name: string;
	description: string;
	link: string;
	platform: string;
	credit_url: string;
	show_maker: number;
	screenshot: string | null;
}

export interface ListingRow extends ListingFields {
	key_id: string;
	state: 'draft' | 'review' | 'listed' | 'rejected';
	pending: string | null;
	note: string | null;
	submitted_at: string | null;
	listed_at: string | null;
	reviewed_at: string | null;
	reviewed_by: number | null;
}

export function usesOf(row: { uses: string | null }): string[] {
	try {
		return row.uses ? (JSON.parse(row.uses) as string[]) : [];
	} catch {
		return [];
	}
}

export function pendingOf(row: ListingRow): ListingFields | null {
	return row.pending ? (JSON.parse(row.pending) as ListingFields) : null;
}

/** A key of this account, revoked or not; null for anyone else's. */
export async function ownKey(db: D1Database, accountId: number, keyId: string): Promise<KeyRow | null> {
	return db.prepare(`SELECT * FROM keys WHERE id = ? AND account_id = ?`).bind(keyId, accountId).first<KeyRow>();
}

export async function listingOf(db: D1Database, keyId: string): Promise<ListingRow | null> {
	return db.prepare(`SELECT * FROM listings WHERE key_id = ?`).bind(keyId).first<ListingRow>();
}

/** Hidden by the 90-day rule: no request lately, and not listed lately either. */
export function isVisible(lastUsed: string | null, listedAt: string | null, now = Date.now()): boolean {
	const cutoff = now - HIDE_AFTER_DAYS * 86400_000;
	return (lastUsed !== null && Date.parse(lastUsed) >= cutoff) || (listedAt !== null && Date.parse(listedAt) >= cutoff);
}
