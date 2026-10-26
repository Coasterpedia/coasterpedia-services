export interface Env {
	ASSETS: Fetcher;
	ACCOUNTS: D1Database;
	/** The API's key namespace (DR-010 §7.3 step 2): key:<sha256> → KeyEntry. */
	KEYS: KVNamespace;
	/** Showcase screenshots, plus build/home.json for the home page's build. */
	SHOTS: R2Bucket;
	IMAGES?: ImagesBinding;
	/** The API Worker, reached without passing through the zone's firewall. */
	API: Fetcher;

	WIKI_ORIGIN: string;
	PORTAL_ORIGIN: string;
	OAUTH_CLIENT_ID: string;
	OAUTH_CLIENT_SECRET?: string;
	/** Signs the portal's own cookies (§13.11); not the API's SESSION_SECRET. */
	PORTAL_SESSION_SECRET?: string;
	/** Wiki user IDs, comma-separated. */
	SHOWCASE_REVIEWERS: string;

	SES_REGION: string;
	MAIL_FROM: string;
	SES_ACCESS_KEY_ID?: string;
	SES_SECRET_ACCESS_KEY?: string;

	CF_ACCOUNT_ID: string;
	USAGE_DATASET: string;
	USAGE_ALERT_DAILY: string;
	/** Account Analytics Read: the Analytics Engine SQL API and GraphQL. */
	ANALYTICS_TOKEN?: string;
}

/** What a key's KV entry holds; the API's src/env.ts reads the same shape. */
export interface KeyEntry {
	account: string;
	tier: string;
	status: 'active' | 'revoked';
	name?: string;
}

/** The API's account for a wiki user: what rate limits and usage are keyed by. */
export const apiAccount = (userId: number) => `u${userId}`;
