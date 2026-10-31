-- What a key is for, so a reviewer has something to check usage against.
-- Null on keys made before this: Your keys asks their owners to add it.
-- kind is a slug from KINDS in src/data.ts. link is null when the project
-- has none, or isn't public yet. commercial is 'no', 'later' or 'yes'; a key
-- can't be created as 'yes', but an owner can change it to 'yes' later.
ALTER TABLE keys ADD COLUMN kind TEXT;
ALTER TABLE keys ADD COLUMN link TEXT;
ALTER TABLE keys ADD COLUMN commercial TEXT;
-- The last time a reviewer emailed the owner a question about this key.
ALTER TABLE keys ADD COLUMN asked_at TEXT;
ALTER TABLE keys ADD COLUMN asked_by INTEGER;

-- The wiki account behind the portal account, from list=users, kept by the
-- daily run and on each new key. registered is null for accounts older than
-- the wiki's record of it.
ALTER TABLE accounts ADD COLUMN wiki_edits INTEGER;
ALTER TABLE accounts ADD COLUMN wiki_registered TEXT;
