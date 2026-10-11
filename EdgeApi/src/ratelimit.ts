/**
 * The limit RATE_LIMITER enforces, as the RateLimit-Policy header states it
 * (IETF draft-ietf-httpapi-ratelimit-headers). Must match [[ratelimits]] in
 * wrangler.toml: the binding cannot report its own configuration. One policy
 * for everyone until tiers exist (DR-010 §9).
 *
 * "burst" because that is all it is: the binding counts per Cloudflare
 * location, so a client spread across locations can exceed it (§7.3).
 */
export const BURST = { name: 'burst', quota: 60, window: 60 } as const;

const POLICY = `"${BURST.name}";q=${BURST.quota};w=${BURST.window}`;

/**
 * Every keyed answer carries the policy. RateLimit, the remaining quota, goes
 * only on a 429, where it is known to be 0: the binding answers allowed or
 * not and never says how much is left, and a guessed count would mislead.
 */
export function withPolicy(response: Response): Response {
	const headers = new Headers(response.headers);
	headers.set('ratelimit-policy', POLICY);
	return new Response(response.body, { status: response.status, statusText: response.statusText, headers });
}

/** The 429's headers. The binding gives no reset time, so a whole window is the honest wait. */
export const LIMITED_HEADERS: Record<string, string> = {
	'ratelimit-policy': POLICY,
	ratelimit: `"${BURST.name}";r=0;t=${BURST.window}`,
	'retry-after': String(BURST.window),
};
