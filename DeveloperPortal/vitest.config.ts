import { cloudflareTest, readD1Migrations } from '@cloudflare/vitest-pool-workers';
import { fileURLToPath } from 'node:url';
import { defineConfig } from 'vitest/config';

export default defineConfig({
	plugins: [
		cloudflareTest(async () => ({
			wrangler: { configPath: './wrangler.toml' },
			miniflare: {
				bindings: {
					TEST_MIGRATIONS: await readD1Migrations('./migrations'),
					// .dev.vars points at localhost for wrangler dev; tests are the real host.
					PORTAL_ORIGIN: 'https://developer.coasterpedia.net',
					PORTAL_SESSION_SECRET: 'test-portal-secret',
					OAUTH_CLIENT_ID: 'test-client',
					OAUTH_CLIENT_SECRET: 'test-client-secret',
					SHOWCASE_REVIEWERS: '7',
					CF_ACCOUNT_ID: 'test-account',
					ANALYTICS_TOKEN: 'test-analytics-token',
				},
			},
		})),
	],
	// The generated terms version is null until the terms are in force; tests
	// run as if version 1 were.
	resolve: {
		alias: [{ find: /^\.\/terms-version$/, replacement: fileURLToPath(new URL('./test/fixtures/terms-version.ts', import.meta.url)) }],
	},
	test: { setupFiles: ['./test/apply-migrations.ts'] },
});
