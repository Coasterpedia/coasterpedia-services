// The developer portal's Worker (DR-010 §13). Static assets answer every
// public page before this runs; what reaches here is sign-in, Your keys,
// review and the showcase.
import { Hono } from 'hono';
import { csrf } from 'hono/csrf';
import { HTTPException } from 'hono/http-exception';
import { secureHeaders } from 'hono/secure-headers';
import { type AppEnv, message } from './app';
import { accounts } from './accounts';
import { auth } from './auth';
import { daily } from './cron';
import type { Env } from './env';
import { keys } from './keys';
import { listings } from './listings';
import { review } from './review';
import { showcase } from './showcase';

const app = new Hono<AppEnv>();

app.use('*', secureHeaders({
	contentSecurityPolicy: {
		defaultSrc: ["'self'"],
		// data: for the photo placeholder's glyph, a mask the primitive inlines.
		imgSrc: ["'self'", 'data:'],
		scriptSrc: ["'self'"],
		styleSrc: ["'self'"],
		formAction: ["'self'"],
		frameAncestors: ["'none'"],
		baseUri: ["'none'"],
	},
	referrerPolicy: 'strict-origin-when-cross-origin',
}));
// Forms post only from the portal's own pages.
app.use('*', async (c, next) => csrf({ origin: c.env.PORTAL_ORIGIN })(c, next));

app.route('/auth', auth);
app.route('/account/keys/:id/listing', listings);
app.route('/account/keys', keys);
app.route('/review/accounts', accounts);
app.route('/review', review);
app.route('/', showcase);

app.get('/account', (c) => c.redirect('/account/keys', 301));

app.notFound((c) => message(c, 404, 'Not found', <p>There’s nothing at this address. <a href="/">Home</a> · <a href="/docs/">Docs</a></p>));
app.onError((err, c) => {
	// A refusal a middleware raised, such as a cross-site form: its own answer.
	if (err instanceof HTTPException) return err.getResponse();
	console.error(err);
	return message(c, 500, 'Something went wrong', <p>The error has been logged. Try again in a minute.</p>);
});

export default {
	fetch: app.fetch,
	async scheduled(_controller: ScheduledController, env: Env, ctx: ExecutionContext) {
		ctx.waitUntil(daily(env));
	},
} satisfies ExportedHandler<Env>;
