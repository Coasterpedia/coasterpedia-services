export interface Env {
	DATA: D1Database;
	KEYS: KVNamespace;
	USAGE?: AnalyticsEngineDataset;
	RATE_LIMITER?: RateLimit;
	ACCESS_TEAM_DOMAIN: string;
	ACCESS_AUD: string;
	/** How long a location keeps a 200 (DR-010 §7.3); "0" turns the cache off. */
	CACHE_SECONDS?: string;
	/** Turnstile's siteverify secret for the browser's widget (DR-010 §7.4); a Worker secret. */
	TURNSTILE_SECRET?: string;
	/** The host the widget must have run on: the wiki's. */
	TURNSTILE_HOSTNAME?: string;
	/** Signs cps_ session tokens; a Worker secret. Rotating it ends every session. */
	SESSION_SECRET?: string;
	/** Local development only (.dev.vars): lets a local Projector ingest without Access. */
	DEV_INGEST_TOKEN?: string;
}

/** What a key's KV entry holds. */
export interface KeyEntry {
	account: string;
	tier: string;
	status: 'active' | 'revoked';
	name?: string;
}
