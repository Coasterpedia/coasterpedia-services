// What the home page's build needs from the live services: /v1/meta and the
// showcase. Bot Fight Mode challenges GitHub's runners on every host in the
// zone, and no WAF rule can skip it on the Free plan, so the build can't fetch
// these itself. The daily run writes them to R2 instead, the API's half over
// the service binding, and the build reads the object with wrangler.
import type { Env } from './env';
import { showcaseJson } from './showcase';

/** In SHOTS, outside the screenshot route's pattern, so the Worker never serves it. */
export const HOME_DATA_KEY = 'build/home.json';

export interface HomeData {
	written_at: string;
	/** /v1/meta's body, or the last good one when the API didn't answer. */
	meta: unknown;
	showcase: Awaited<ReturnType<typeof showcaseJson>>;
}

/** Writes build/home.json; returns what it wrote. A part that fails keeps its last value. */
export async function writeHomeData(env: Env, now = new Date()): Promise<HomeData> {
	const previous = await env.SHOTS.get(HOME_DATA_KEY).then((o) => o?.json<HomeData>() ?? null).catch(() => null);

	let meta = previous?.meta ?? null;
	try {
		const res = await env.API.fetch('https://api.coasterpedia.net/v1/meta');
		if (!res.ok) throw new Error(`${res.status}`);
		meta = await res.json();
	} catch (e) {
		console.error(`home data: /v1/meta failed (${(e as Error).message}); keeping the last one`);
	}

	let showcase = previous?.showcase ?? { data: [] };
	try {
		showcase = await showcaseJson(env, now.getTime());
	} catch (e) {
		console.error(`home data: showcase failed (${(e as Error).message}); keeping the last one`);
	}

	const data: HomeData = { written_at: now.toISOString(), meta, showcase };
	await env.SHOTS.put(HOME_DATA_KEY, JSON.stringify(data), { httpMetadata: { contentType: 'application/json' } });
	return data;
}
