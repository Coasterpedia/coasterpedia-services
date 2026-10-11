import { env } from 'cloudflare:workers';

/**
 * Storage is shared across tests in this pool version; start from empty. The
 * attractions go first and through DELETE, so the count triggers take the
 * counters back to 0 rather than losing them with the rest of state.
 */
export async function resetData(): Promise<void> {
	await env.DATA.batch([
		env.DATA.prepare('DELETE FROM attractions'),
		env.DATA.prepare('DELETE FROM merges'),
		env.DATA.prepare('DELETE FROM changes'),
		env.DATA.prepare(`DELETE FROM state WHERE key NOT IN ('count_latest', 'count_settled')`),
	]);
}
