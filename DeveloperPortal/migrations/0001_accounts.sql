-- Developers, keys and showcase listings (DR-010 §7.2, §13.5).

-- One row per wiki user who has signed in. The ID is the OAuth profile's
-- sub, the wiki user ID, so a rename keeps the account.
CREATE TABLE accounts (
	id INTEGER PRIMARY KEY,
	username TEXT NOT NULL,
	email TEXT NOT NULL,
	tier TEXT NOT NULL DEFAULT 'free',
	created_at TEXT NOT NULL,
	signed_in_at TEXT NOT NULL
);

-- The key itself is never stored: only its SHA-256 and its first eight
-- characters. The API sees the key through KV (key:<hash>), which the portal
-- writes beside this row.
CREATE TABLE keys (
	id TEXT PRIMARY KEY,
	account_id INTEGER NOT NULL REFERENCES accounts (id),
	hash TEXT NOT NULL UNIQUE,
	prefix TEXT NOT NULL,
	name TEXT NOT NULL,
	description TEXT NOT NULL,
	terms_version TEXT NOT NULL,
	created_at TEXT NOT NULL,
	revoked_at TEXT,
	-- Kept by the daily rollup from Analytics Engine (§13.10).
	last_used_at TEXT,
	uses TEXT
);

CREATE INDEX keys_by_account ON keys (account_id, created_at);
-- Analytics Engine records a request's key by its prefix, so a prefix is
-- unique within an account, revoked keys included.
CREATE UNIQUE INDEX keys_prefix_in_account ON keys (account_id, prefix);

-- One listing per key (§13.4). An edit to a listed project waits in pending
-- while the listed version stays up.
CREATE TABLE listings (
	key_id TEXT PRIMARY KEY REFERENCES keys (id),
	state TEXT NOT NULL CHECK (state IN ('draft', 'review', 'listed', 'rejected')),
	name TEXT NOT NULL,
	description TEXT NOT NULL,
	link TEXT NOT NULL,
	platform TEXT NOT NULL,
	credit_url TEXT NOT NULL,
	show_maker INTEGER NOT NULL DEFAULT 0,
	screenshot TEXT,
	pending TEXT,
	note TEXT,
	submitted_at TEXT,
	listed_at TEXT,
	reviewed_at TEXT,
	reviewed_by INTEGER
);

CREATE INDEX listings_by_state ON listings (state, listed_at);
