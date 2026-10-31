// Wiki blocks on portal accounts. Under the API terms a block can suspend
// keys but doesn't by itself, so nothing here touches a key: the daily run
// records blocks, the accounts page shows them, and a blocked account can't
// create keys. Sign-in refuses a blocked account from the OAuth profile.
// The same read records the account's edit count and age, for reviewers.
import { isoNow, USER_AGENT } from './app';
import type { Env } from './env';

export interface Block {
	reason: string;
	/** As the wiki gives it: an ISO time, or "infinite". */
	expiry: string;
}

/** What the accounts page shows about the wiki account. */
export interface WikiFacts {
	edits: number | null;
	/** ISO; null for accounts older than the wiki's record of it. */
	registered: string | null;
}

interface WikiUser {
	userid?: number;
	missing?: boolean;
	editcount?: number;
	registration?: string | null;
	blockid?: number;
	blockpartial?: boolean;
	blockreason?: string;
	blockexpiry?: string;
}

/**
 * Sitewide blocks by wiki user ID, read anonymously from list=users, 50 IDs
 * a request. A partial block (some pages only) doesn't count. Throws when the
 * wiki can't answer, so a failed read never clears a block.
 */
export async function wikiBlocks(env: Env, ids: number[]): Promise<{ blocks: Map<number, Block>; facts: Map<number, WikiFacts> }> {
	const blocks = new Map<number, Block>();
	const facts = new Map<number, WikiFacts>();
	for (let i = 0; i < ids.length; i += 50) {
		const url = new URL(`${env.WIKI_ORIGIN}/w/api.php`);
		url.search = new URLSearchParams({
			action: 'query',
			list: 'users',
			ususerids: ids.slice(i, i + 50).join('|'),
			usprop: 'blockinfo|editcount|registration',
			format: 'json',
			formatversion: '2',
		}).toString();
		// Every request to the wiki names itself: the zone's WAF refuses an empty user agent.
		const res = await fetch(url, { headers: { accept: 'application/json', 'user-agent': USER_AGENT } });
		if (!res.ok) throw new Error(`wiki ${res.status}`);
		const body = (await res.json()) as { query?: { users: WikiUser[] }; error?: { code: string } };
		if (!body.query) throw new Error(`wiki: ${body.error?.code ?? 'no query in the answer'}`);
		for (const user of body.query.users) {
			if (user.userid) facts.set(user.userid, { edits: user.editcount ?? null, registered: user.registration ?? null });
			if (user.userid && user.blockid && !user.blockpartial) {
				blocks.set(user.userid, { reason: user.blockreason ?? '', expiry: user.blockexpiry ?? 'infinite' });
			}
		}
	}
	return { blocks, facts };
}

/** What the wiki said, onto these accounts. blocked_at keeps the first sighting and clears when the block ends. */
async function recordBlocks(env: Env, ids: number[], { blocks, facts }: Awaited<ReturnType<typeof wikiBlocks>>, now = isoNow()): Promise<void> {
	if (ids.length === 0) return;
	const factStatements = [...facts].map(([id, f]) =>
		env.ACCOUNTS.prepare(`UPDATE accounts SET wiki_edits = ?, wiki_registered = ? WHERE id = ?`).bind(f.edits, f.registered, id));
	await env.ACCOUNTS.batch([...factStatements, ...ids.map((id) => {
		const block = blocks.get(id);
		return block
			? env.ACCOUNTS.prepare(`UPDATE accounts SET blocked_at = COALESCE(blocked_at, ?), block_reason = ?, block_expiry = ? WHERE id = ?`)
				.bind(now, block.reason, block.expiry, id)
			: env.ACCOUNTS.prepare(`UPDATE accounts SET blocked_at = NULL, block_reason = NULL, block_expiry = NULL WHERE id = ? AND blocked_at IS NOT NULL`)
				.bind(id);
	})]);
}

/** The daily run: every account. Returns how many are blocked. */
export async function checkBlocks(env: Env): Promise<number> {
	const { results } = await env.ACCOUNTS.prepare(`SELECT id FROM accounts`).all<{ id: number }>();
	const ids = results.map((r) => r.id);
	const read = await wikiBlocks(env, ids);
	await recordBlocks(env, ids, read);
	return read.blocks.size;
}

/**
 * Before a new key: this account, asked live and recorded. If the wiki
 * can't answer, the daily run's record decides, so an outage doesn't stop
 * anyone creating a key.
 */
export async function isBlocked(env: Env, id: number): Promise<boolean> {
	try {
		const read = await wikiBlocks(env, [id]);
		await recordBlocks(env, [id], read);
		return read.blocks.has(id);
	} catch (e) {
		console.warn(`block check for ${id} fell back to the stored record: ${(e as Error).message}`);
		const row = await env.ACCOUNTS.prepare(`SELECT blocked_at FROM accounts WHERE id = ?`).bind(id).first<{ blocked_at: string | null }>();
		return !!row?.blocked_at;
	}
}
