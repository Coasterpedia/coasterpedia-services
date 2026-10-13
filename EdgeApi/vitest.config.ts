import { cloudflareTest, readD1Migrations } from '@cloudflare/vitest-pool-workers';
import { defineConfig } from 'vitest/config';

export default defineConfig({
	plugins: [
		cloudflareTest(async () => ({
			wrangler: { configPath: './wrangler.toml' },
			miniflare: {
				bindings: {
					TEST_MIGRATIONS: await readD1Migrations('./migrations'),
					DEV_INGEST_TOKEN: 'test-ingest-token',
					// Sessions (DR-010 §7.4); siteverify itself is stubbed in session.test.ts.
					TURNSTILE_SECRET: 'test-turnstile-secret',
					SESSION_SECRET: 'test-session-secret',
					// Off: storage is shared across tests, so a cached answer would leak
					// into the next one. cache.test.ts turns it on with its own env.
					CACHE_SECONDS: '0',
				},
			},
		})),
	],
	test: { setupFiles: ['./test/apply-migrations.ts'] },
});
