-- Who revoked a key and why. Null for a developer revoking their own; set
-- when a reviewer revokes one from the accounts page, with the reason we
-- emailed them.
ALTER TABLE keys ADD COLUMN revoked_by INTEGER;
ALTER TABLE keys ADD COLUMN revoked_reason TEXT;

-- A sitewide wiki block on the account, as the daily run last read it. Null
-- when not blocked. blocked_at is when we first saw it; block_expiry is the
-- wiki's, an ISO time or "infinite". A block refuses new keys and revokes
-- nothing by itself (API terms).
ALTER TABLE accounts ADD COLUMN blocked_at TEXT;
ALTER TABLE accounts ADD COLUMN block_reason TEXT;
ALTER TABLE accounts ADD COLUMN block_expiry TEXT;
