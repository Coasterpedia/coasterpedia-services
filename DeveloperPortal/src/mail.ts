// Mail through Amazon SES's HTTPS API (DR-010 §13.7): SESv2 SendEmail,
// signed by aws4fetch, as an IAM user allowed only ses:SendEmail from
// MAIL_FROM. Callers send it with waitUntil, so a mail failure never fails a
// submission or a review: the portal is the record, the mail only points at it.
import { AwsClient } from 'aws4fetch';
import type { Env } from './env';

export interface Mail {
	to: string[];
	subject: string;
	text: string;
}

export async function sendMail(env: Env, mail: Mail): Promise<void> {
	if (!env.SES_ACCESS_KEY_ID || !env.SES_SECRET_ACCESS_KEY) {
		console.warn(`mail not sent, SES is not configured: ${mail.subject}`);
		return;
	}
	if (mail.to.length === 0) return;
	const aws = new AwsClient({
		accessKeyId: env.SES_ACCESS_KEY_ID,
		secretAccessKey: env.SES_SECRET_ACCESS_KEY,
		service: 'ses',
		region: env.SES_REGION,
	});
	const res = await aws.fetch(`https://email.${env.SES_REGION}.amazonaws.com/v2/email/outbound-emails`, {
		method: 'POST',
		headers: { 'content-type': 'application/json' },
		body: JSON.stringify({
			FromEmailAddress: `Coasterpedia for developers <${env.MAIL_FROM}>`,
			Destination: { ToAddresses: mail.to },
			Content: {
				Simple: {
					Subject: { Data: mail.subject, Charset: 'UTF-8' },
					Body: { Text: { Data: mail.text, Charset: 'UTF-8' } },
				},
			},
		}),
	});
	if (!res.ok) console.error(`SES ${res.status}: ${await res.text()}`);
}

/** Every reviewer who has signed in, so the portal holds their address. */
export async function reviewerAddresses(env: Env): Promise<string[]> {
	const ids = reviewerIds(env);
	if (ids.length === 0) return [];
	const { results } = await env.ACCOUNTS.prepare(
		`SELECT email FROM accounts WHERE id IN (${ids.map(() => '?').join(',')})`,
	).bind(...ids).all<{ email: string }>();
	return results.map((r) => r.email);
}

export function reviewerIds(env: Env): number[] {
	return env.SHOWCASE_REVIEWERS.split(',').map((s) => Number(s.trim())).filter((n) => Number.isInteger(n) && n > 0);
}
