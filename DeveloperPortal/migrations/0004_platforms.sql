-- Where a project runs, as a list, on keys and listings alike, so a listing
-- can start from its key's answers and an app can be on iOS and Android.
-- Both hold a JSON array of PLATFORMS slugs (src/data.ts).

-- 0003's website, app and bot kinds become one 'public' kind; where they ran
-- moves to platforms. An app's platform wasn't asked, so its owner adds it
-- on the key's details page.
ALTER TABLE keys ADD COLUMN platforms TEXT;
UPDATE keys SET kind = 'public', platforms = '["web"]' WHERE kind = 'website';
UPDATE keys SET kind = 'public', platforms = '["discord_bot"]' WHERE kind = 'bot';
UPDATE keys SET kind = 'public' WHERE kind = 'app';

-- platform stays, holding the first of platforms, for showcase.json readers
-- that read it. A pending edit saved before this has only platform, and
-- pendingOf reads it as a list of one.
ALTER TABLE listings ADD COLUMN platforms TEXT;
UPDATE listings SET platforms = json_array(platform);
