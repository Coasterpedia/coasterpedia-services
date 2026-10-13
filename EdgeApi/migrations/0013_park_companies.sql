-- /v1/park-companies (DR-010 §12.16). The records, shaped as manufacturers
-- (0007), and the link from a company to its parks, kept by triggers from the
-- owner and operator history each park record carries.
--
-- Additive: two new tables and counters that start at 0. The old Worker's
-- ingest fires the park triggers too, so the order of migration and deploy
-- doesn't matter. The first sweep after the services image ships fills both:
-- 68 companies, and about 1,700 history entries on about 800 parks.
CREATE TABLE park_companies (
    id          INTEGER NOT NULL,
    in_latest   INTEGER NOT NULL,
    in_settled  INTEGER NOT NULL,
    status      TEXT,
    country     TEXT,
    sort_name   TEXT    NOT NULL,
    revision    INTEGER NOT NULL,
    updated_at  TEXT    NOT NULL,
    settles_at  TEXT,
    summary     TEXT    NOT NULL,
    record      TEXT    NOT NULL,
    PRIMARY KEY (id, in_latest)
);

-- Name only, as manufacturers: 68 companies on 2026-10-13.
CREATE INDEX park_companies_latest_by_name  ON park_companies (sort_name, id) WHERE in_latest = 1;
CREATE INDEX park_companies_settled_by_name ON park_companies (sort_name, id) WHERE in_settled = 1;

INSERT OR IGNORE INTO state (key, value) VALUES ('count_park_companies_latest', '0');
INSERT OR IGNORE INTO state (key, value) VALUES ('count_park_companies_settled', '0');

CREATE TRIGGER park_companies_count_insert AFTER INSERT ON park_companies
BEGIN
    UPDATE state SET value = CAST(value AS INTEGER) + 1 WHERE key = 'count_park_companies_latest' AND NEW.in_latest = 1;
    UPDATE state SET value = CAST(value AS INTEGER) + 1 WHERE key = 'count_park_companies_settled' AND NEW.in_settled = 1;
END;

CREATE TRIGGER park_companies_count_delete AFTER DELETE ON park_companies
BEGIN
    UPDATE state SET value = CAST(value AS INTEGER) - 1 WHERE key = 'count_park_companies_latest' AND OLD.in_latest = 1;
    UPDATE state SET value = CAST(value AS INTEGER) - 1 WHERE key = 'count_park_companies_settled' AND OLD.in_settled = 1;
END;

CREATE TRIGGER park_companies_count_update AFTER UPDATE OF in_latest, in_settled ON park_companies
WHEN OLD.in_latest <> NEW.in_latest OR OLD.in_settled <> NEW.in_settled
BEGIN
    UPDATE state SET value = CAST(value AS INTEGER) + NEW.in_latest - OLD.in_latest
        WHERE key = 'count_park_companies_latest' AND OLD.in_latest <> NEW.in_latest;
    UPDATE state SET value = CAST(value AS INTEGER) + NEW.in_settled - OLD.in_settled
        WHERE key = 'count_park_companies_settled' AND OLD.in_settled <> NEW.in_settled;
END;

-- One row per park row and history entry naming a page: the park's
-- history.owners and history.operators, keyed by the entry's place in its
-- list (seq), so a company with two spells at one park has two rows. Unlinked
-- names and red links (no id) have none. The company may be any page: a
-- manufacturer or a park can own a park, and /park-companies/{id}/parks only
-- answers for an ID it holds as a company.
--
-- Not per view, as attraction_manufacturers (0008): in_latest is in the key
-- and a read checks its flag. Start and end are the entry's DateValue JSON.
CREATE TABLE park_company_parks (
    company     INTEGER NOT NULL,
    park        INTEGER NOT NULL,
    in_latest   INTEGER NOT NULL,
    role        TEXT    NOT NULL,
    seq         INTEGER NOT NULL,
    in_settled  INTEGER NOT NULL,
    park_name   TEXT    NOT NULL,
    sort_name   TEXT    NOT NULL,
    start       TEXT,
    "end"       TEXT,
    current     INTEGER NOT NULL,
    PRIMARY KEY (company, park, in_latest, role, seq)
) WITHOUT ROWID;

-- A company's parks read its key range (Six Flags, the largest: about 80
-- entries). The triggers delete by park.
CREATE INDEX park_company_parks_by_park ON park_company_parks (park, in_latest);

-- Parks pushed by a services image that went first already carry history.
INSERT INTO park_company_parks (company, park, in_latest, role, seq, in_settled, park_name, sort_name, start, "end", current)
SELECT json_extract(h.value, '$.company.id'), p.id, p.in_latest, r.role, h.key, p.in_settled,
       json_extract(p.summary, '$.name'), p.sort_name,
       json_extract(h.value, '$.start'), json_extract(h.value, '$.end'), COALESCE(json_extract(h.value, '$.current'), 0)
FROM parks p,
     (SELECT 'owner' AS role, '$.history.owners' AS path UNION ALL SELECT 'operator', '$.history.operators') r,
     json_each(p.record, r.path) h
WHERE json_type(h.value, '$.company.id') = 'integer';

CREATE TRIGGER parks_companies_insert AFTER INSERT ON parks
BEGIN
    INSERT INTO park_company_parks (company, park, in_latest, role, seq, in_settled, park_name, sort_name, start, "end", current)
    SELECT json_extract(h.value, '$.company.id'), NEW.id, NEW.in_latest, r.role, h.key, NEW.in_settled,
           json_extract(NEW.summary, '$.name'), NEW.sort_name,
           json_extract(h.value, '$.start'), json_extract(h.value, '$.end'), COALESCE(json_extract(h.value, '$.current'), 0)
    FROM (SELECT 'owner' AS role, '$.history.owners' AS path UNION ALL SELECT 'operator', '$.history.operators') r,
         json_each(NEW.record, r.path) h
    WHERE json_type(h.value, '$.company.id') = 'integer';
END;

CREATE TRIGGER parks_companies_delete AFTER DELETE ON parks
BEGIN
    DELETE FROM park_company_parks WHERE park = OLD.id AND in_latest = OLD.in_latest;
END;

-- Ingest's upsert sets every column on every push; the WHEN keeps an edit
-- that changes nothing here from writing anything. A promote or a discard
-- moves in_latest, which is in the key.
CREATE TRIGGER parks_companies_update AFTER UPDATE ON parks
WHEN OLD.in_latest IS NOT NEW.in_latest OR OLD.in_settled IS NOT NEW.in_settled
  OR OLD.sort_name IS NOT NEW.sort_name
  OR json_extract(OLD.summary, '$.name') IS NOT json_extract(NEW.summary, '$.name')
  OR json_extract(OLD.record, '$.history') IS NOT json_extract(NEW.record, '$.history')
BEGIN
    DELETE FROM park_company_parks WHERE park = OLD.id AND in_latest = OLD.in_latest;
    INSERT INTO park_company_parks (company, park, in_latest, role, seq, in_settled, park_name, sort_name, start, "end", current)
    SELECT json_extract(h.value, '$.company.id'), NEW.id, NEW.in_latest, r.role, h.key, NEW.in_settled,
           json_extract(NEW.summary, '$.name'), NEW.sort_name,
           json_extract(h.value, '$.start'), json_extract(h.value, '$.end'), COALESCE(json_extract(h.value, '$.current'), 0)
    FROM (SELECT 'owner' AS role, '$.history.owners' AS path UNION ALL SELECT 'operator', '$.history.operators') r,
         json_each(NEW.record, r.path) h
    WHERE json_type(h.value, '$.company.id') = 'integer';
END;
