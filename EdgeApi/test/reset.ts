import { env } from 'cloudflare:workers';

/**
 * Storage is shared across tests in this pool version; start from empty. The
 * records go first and through DELETE, so the count triggers take
 * the counters back to 0 rather than losing them with the rest of state. The
 * change log's epoch is the migration's, so it stays too.
 */
export async function resetData(): Promise<void> {
	await env.DATA.batch([
		env.DATA.prepare('DELETE FROM attractions'),
		// Emptied by the delete trigger too; explicit in case a test wrote it directly.
		env.DATA.prepare('DELETE FROM attraction_manufacturers'),
		env.DATA.prepare('DELETE FROM attraction_products'),
		env.DATA.prepare('DELETE FROM product_installations'),
		env.DATA.prepare('DELETE FROM parks'),
		env.DATA.prepare('DELETE FROM manufacturers'),
		env.DATA.prepare('DELETE FROM products'),
		env.DATA.prepare('DELETE FROM product_manufacturers'),
		env.DATA.prepare('DELETE FROM park_companies'),
		env.DATA.prepare('DELETE FROM park_company_parks'),
		// Emptied by the kinds' delete triggers too.
		env.DATA.prepare('DELETE FROM search_names'),
		env.DATA.prepare('DELETE FROM attraction_past_parks'),
		env.DATA.prepare('DELETE FROM merges'),
		env.DATA.prepare('DELETE FROM changes'),
		env.DATA.prepare(`DELETE FROM state WHERE key NOT IN ('count_latest', 'count_settled', 'count_parks_latest', 'count_parks_settled',
			'count_manufacturers_latest', 'count_manufacturers_settled', 'count_products_latest', 'count_products_settled',
			'count_park_companies_latest', 'count_park_companies_settled',
			'changes_epoch')`),
	]);
}
