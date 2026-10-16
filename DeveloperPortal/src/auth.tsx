// Sign in with Coasterpedia (DR-010 §7.2, §13.11): OAuth 2 with PKCE against
// the wiki, identity only. The tokens are used once, to read the profile,
// and thrown away; the portal's own signed cookie is the session.
import { Hono } from 'hono';
import { type AppEnv, ClosedBeta, CONTACT, type Ctx, isoNow, message, USER_AGENT } from './app';
import { b64url, clearOAuth, clearSession, randomToken, readOAuth, readSession, writeOAuth, writeSession } from './cookies';

export const auth = new Hono<AppEnv>();

const callbackUrl = (c: Ctx) => `${c.env.PORTAL_ORIGIN}/auth/callback`;

/** Only a path on this site: never //elsewhere or an absolute URL. */
export function safeReturn(value: string | undefined): string {
	return value && value.startsWith('/') && !value.startsWith('//') && !value.startsWith('/\\') ? value : '/account/keys';
}

async function challenge(verifier: string): Promise<string> {
	return b64url(new Uint8Array(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(verifier))));
}

auth.get('/sign-in', async (c) => {
	const returnTo = safeReturn(c.req.query('return'));
	if (await readSession(c)) return c.redirect(returnTo, 303);
	if (!c.env.OAUTH_CLIENT_ID || !c.env.OAUTH_CLIENT_SECRET) {
		return message(c, 503, 'Sign-in isn’t open yet', <ClosedBeta />);
	}

	const state = randomToken(16);
	const verifier = randomToken(32);
	await writeOAuth(c, { state, verifier, returnTo });
	const url = new URL(`${c.env.WIKI_ORIGIN}/w/rest.php/oauth2/authorize`);
	url.search = new URLSearchParams({
		response_type: 'code',
		client_id: c.env.OAUTH_CLIENT_ID,
		redirect_uri: callbackUrl(c),
		state,
		code_challenge: await challenge(verifier),
		code_challenge_method: 'S256',
	}).toString();
	return c.redirect(url.toString(), 303);
});

interface Profile {
	sub: number | string;
	username: string;
	blocked?: boolean;
	confirmed_email?: boolean;
	email?: string;
}

auth.get('/callback', async (c) => {
	const pending = await readOAuth(c);
	clearOAuth(c);
	const again = <p><a href="/auth/sign-in">Sign in again</a>.</p>;
	const ours = (
		<>
			<p>Sorry, this is probably our mistake. Try signing in again in a few minutes.</p>
			<p>If it still doesn’t work, email <a href={`mailto:${CONTACT}`}>{CONTACT}</a>.</p>
		</>
	);

	const error = c.req.query('error');
	if (error) {
		// OAuth's own wording is for the log, not the reader.
		console.warn(`sign-in refused: ${error}: ${c.req.query('error_description') ?? ''}`);
		if (error === 'access_denied') {
			return message(c, 400, 'You weren’t signed in', <><p>You didn’t allow access on the wiki, so we can’t sign you in.</p>{again}</>);
		}
		return message(c, 502, 'You weren’t signed in', ours);
	}
	if (!pending || c.req.query('state') !== pending.state) {
		return message(c, 400, 'That sign-in has expired', <><p>Sign-in has to finish within ten minutes, in the browser you started it in.</p>{again}</>);
	}
	const code = c.req.query('code');
	if (!code) return message(c, 400, 'You weren’t signed in', again);

	// Every request to the wiki names itself: the zone's WAF refuses an empty
	// user agent on every host (§13.10).
	const token = await fetch(`${c.env.WIKI_ORIGIN}/w/rest.php/oauth2/access_token`, {
		method: 'POST',
		headers: { 'content-type': 'application/x-www-form-urlencoded', accept: 'application/json', 'user-agent': USER_AGENT },
		body: new URLSearchParams({
			grant_type: 'authorization_code',
			code,
			redirect_uri: callbackUrl(c),
			client_id: c.env.OAUTH_CLIENT_ID,
			client_secret: c.env.OAUTH_CLIENT_SECRET ?? '',
			code_verifier: pending.verifier,
		}),
	});
	if (!token.ok) {
		console.error(`token exchange ${token.status}: ${await token.text()}`);
		return message(c, 502, 'You weren’t signed in', ours);
	}
	const { access_token } = (await token.json()) as { access_token: string };

	const res = await fetch(`${c.env.WIKI_ORIGIN}/w/rest.php/oauth2/resource/profile`, {
		headers: { authorization: `Bearer ${access_token}`, accept: 'application/json', 'user-agent': USER_AGENT },
	});
	if (!res.ok) {
		console.error(`profile ${res.status}: ${await res.text()}`);
		return message(c, 502, 'You weren’t signed in', ours);
	}
	const profile = (await res.json()) as Profile;
	const sub = Number(profile.sub);
	if (!Number.isInteger(sub) || sub <= 0 || !profile.username) {
		console.error(`profile without a user: ${JSON.stringify(profile)}`);
		return message(c, 502, 'You weren’t signed in', ours);
	}

	if (profile.blocked) {
		return message(c, 403, 'Blocked accounts can’t have keys', <p>Your wiki account is blocked, so we can’t give it keys. If you think the block is wrong, you can appeal it on the wiki.</p>);
	}
	if (!profile.confirmed_email) {
		return message(c, 403, 'Confirm your email address first', <>
			<p>We write to developers about changes to the API, limits and problems with a key, so an account needs a confirmed address.</p>
			<p><a href={`${c.env.WIKI_ORIGIN}/wiki/Special:ConfirmEmail`}>Confirm it on the wiki</a>, then <a href="/auth/sign-in">sign in again</a>.</p>
		</>);
	}
	if (!profile.email) {
		// The consumer wasn't registered with access to email addresses (§13.7).
		console.error('profile has no email: the OAuth consumer lacks email access');
		return message(c, 500, 'Sign-in isn’t working', <><p>The wiki didn’t send us your email address. Sorry, this is our mistake.</p><p>Try again later, or email <a href={`mailto:${CONTACT}`}>{CONTACT}</a>.</p></>);
	}

	const now = isoNow();
	await c.env.ACCOUNTS.prepare(
		`INSERT INTO accounts (id, username, email, created_at, signed_in_at) VALUES (?1, ?2, ?3, ?4, ?4)
		ON CONFLICT (id) DO UPDATE SET username = ?2, email = ?3, signed_in_at = ?4`,
	).bind(sub, profile.username, profile.email, now).run();

	await writeSession(c, sub, profile.username);
	return c.redirect(safeReturn(pending.returnTo), 303);
});

auth.post('/sign-out', (c) => {
	clearSession(c);
	return c.redirect('/', 303);
});
