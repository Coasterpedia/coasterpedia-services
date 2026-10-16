// The daily run (DR-010 §13.10): roll Analytics Engine into D1, then check
// yesterday's Worker requests against the alert threshold. The 90-day rule
// needs no step of its own: the showcase reads last_used_at as it renders.
import type { Env } from './env';
import { reviewerAddresses, sendMail } from './mail';
import { rollUp, workerRequests } from './usage';

export async function daily(env: Env, now = new Date()): Promise<void> {
	try {
		const keys = await rollUp(env);
		console.log(`rollup: ${keys} keys updated`);
	} catch (e) {
		console.error(`rollup failed: ${(e as Error).message}`);
	}

	const yesterday = new Date(now.getTime() - 86400_000).toISOString().slice(0, 10);
	try {
		const requests = await workerRequests(env, yesterday);
		const threshold = Number(env.USAGE_ALERT_DAILY);
		console.log(`worker requests on ${yesterday}: ${requests}`);
		if (threshold > 0 && requests > threshold) {
			await sendMail(env, {
				to: await reviewerAddresses(env),
				subject: `Workers usage: ${requests.toLocaleString('en-GB')} requests on ${yesterday}`,
				text: [
					`Every Worker on the account together served ${requests.toLocaleString('en-GB')} requests on ${yesterday} (UTC).`,
					`The alert is set at ${threshold.toLocaleString('en-GB')} a day; the plan includes about 330,000 a day (10 million a month), with no cap above it.`,
					'',
					'The Workers overview in the Cloudflare dashboard shows which script, and DR-010 §13.10 lists the guards.',
				].join('\n'),
			});
		}
	} catch (e) {
		console.error(`usage alert check failed: ${(e as Error).message}`);
	}
}
