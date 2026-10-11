// Issue a closed-beta key by hand (DR-010 §10 phase 2), until the portal exists.
//
//   npm run create-key -- <account> "<what it is for>"
//
// Prints the key once and the wrangler command that stores its hash. The key
// itself is never stored anywhere; lose it and issue another.
import { createHash, randomBytes } from 'node:crypto';

const [account, name] = process.argv.slice(2);
if (!account || !name) {
	console.error('Usage: npm run create-key -- <account> "<what it is for>"');
	process.exit(1);
}

const ALPHABET = '0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz';
let n = BigInt('0x' + randomBytes(32).toString('hex'));
let body = '';
while (n > 0n) {
	body = ALPHABET[Number(n % 62n)] + body;
	n /= 62n;
}
const key = 'cpk_' + body.padStart(43, '0');
const hash = createHash('sha256').update(key).digest('hex');
const entry = JSON.stringify({ account, tier: 'beta', status: 'active', name });

console.log(`Key (shown once):\n  ${key}\n`);
console.log('Store it:');
console.log(`  npx wrangler kv key put --binding=KEYS --remote 'key:${hash}' '${entry}'\n`);
console.log('Revoke it later by putting the same entry with "status":"revoked".');
