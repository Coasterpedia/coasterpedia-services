export interface Env {
	DATA: D1Database;
	KEYS: KVNamespace;
	USAGE?: AnalyticsEngineDataset;
	RATE_LIMITER?: RateLimit;
	ACCESS_TEAM_DOMAIN: string;
	ACCESS_AUD: string;
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
