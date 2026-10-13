-- Where an attraction is, and when it opened (DR-010 §12.14): the filter and
-- sort columns the Projector now sends, their indexes, and location_kind as a
-- facet dimension.
--
-- Additive: new columns start null and new indexes start empty. The old
-- Worker ignores the new filter keys, so deploy the edge before the services
-- image; the guarded backfill below covers a services image that went first.
ALTER TABLE attractions ADD COLUMN park INTEGER;
ALTER TABLE attractions ADD COLUMN location_kind TEXT;
ALTER TABLE attractions ADD COLUMN state TEXT;
ALTER TABLE attractions ADD COLUMN opened_earliest TEXT;
ALTER TABLE attractions ADD COLUMN opened_latest TEXT;

UPDATE attractions SET
    park = json_extract(summary, '$.park.id'),
    location_kind = json_extract(summary, '$.location_kind'),
    state = json_extract(record, '$.location.state'),
    opened_earliest = json_extract(summary, '$.opened.earliest'),
    opened_latest = json_extract(summary, '$.opened.latest')
WHERE json_type(summary, '$.location_kind') IS NOT NULL;

-- A park's attractions, by name: a park has at most a few hundred.
CREATE INDEX attractions_latest_by_park   ON attractions (park, sort_name, id) WHERE in_latest = 1 AND park IS NOT NULL;
CREATE INDEX attractions_settled_by_park  ON attractions (park, sort_name, id) WHERE in_settled = 1 AND park IS NOT NULL;

-- One kind in name order. Skewed as status is (14,800 at parks, 3,300
-- travelling, 68 in storage on 2026-10-12), so the Worker keeps it out of every
-- other plan with unary + (§12.12).
CREATE INDEX attractions_latest_by_location_kind  ON attractions (location_kind, sort_name, id) WHERE in_latest = 1;
CREATE INDEX attractions_settled_by_location_kind ON attractions (location_kind, sort_name, id) WHERE in_settled = 1;

CREATE INDEX attractions_latest_by_state  ON attractions (state, sort_name, id) WHERE in_latest = 1 AND state IS NOT NULL;
CREATE INDEX attractions_settled_by_state ON attractions (state, sort_name, id) WHERE in_settled = 1 AND state IS NOT NULL;

-- sort=opened and opened_from: by the earliest day it can be, so an opening
-- with no earliest ("[..1989]") cannot be ranked, as an unknown height cannot.
CREATE INDEX attractions_latest_by_opened  ON attractions (opened_earliest, id) WHERE in_latest = 1 AND opened_earliest IS NOT NULL;
CREATE INDEX attractions_settled_by_opened ON attractions (opened_earliest, id) WHERE in_settled = 1 AND opened_earliest IS NOT NULL;
-- opened_to alone is a bound on the latest day: "[..1989]" has no earliest but
-- certainly opened by 1989.
CREATE INDEX attractions_latest_by_opened_latest  ON attractions (opened_latest, id) WHERE in_latest = 1 AND opened_latest IS NOT NULL;
CREATE INDEX attractions_settled_by_opened_latest ON attractions (opened_latest, id) WHERE in_settled = 1 AND opened_latest IS NOT NULL;

-- The facet cells gain location_kind (0005's table, rebuilt): a new filterable
-- column means a new key. A few thousand cells; the triggers are 0005's with
-- the column added.
DROP TRIGGER attractions_facets_insert;
DROP TRIGGER attractions_facets_delete;
DROP TRIGGER attractions_facets_update;
DROP TABLE attraction_facets;

CREATE TABLE attraction_facets (
    view           TEXT    NOT NULL,
    type           TEXT    NOT NULL,
    status         TEXT    NOT NULL,
    country        TEXT    NOT NULL,
    location_kind  TEXT    NOT NULL,
    country_name   TEXT,
    n              INTEGER NOT NULL,
    PRIMARY KEY (view, type, status, country, location_kind)
) WITHOUT ROWID;

INSERT INTO attraction_facets (view, type, status, country, location_kind, country_name, n)
SELECT 'latest', type, IFNULL(status, ''), IFNULL(country, ''), IFNULL(location_kind, ''), MAX(json_extract(summary, '$.country.name')), COUNT(*)
FROM attractions WHERE in_latest = 1 GROUP BY type, IFNULL(status, ''), IFNULL(country, ''), IFNULL(location_kind, '');
INSERT INTO attraction_facets (view, type, status, country, location_kind, country_name, n)
SELECT 'settled', type, IFNULL(status, ''), IFNULL(country, ''), IFNULL(location_kind, ''), MAX(json_extract(summary, '$.country.name')), COUNT(*)
FROM attractions WHERE in_settled = 1 GROUP BY type, IFNULL(status, ''), IFNULL(country, ''), IFNULL(location_kind, '');

CREATE TRIGGER attractions_facets_insert AFTER INSERT ON attractions
BEGIN
    INSERT INTO attraction_facets (view, type, status, country, location_kind, country_name, n)
        SELECT 'latest', NEW.type, IFNULL(NEW.status, ''), IFNULL(NEW.country, ''), IFNULL(NEW.location_kind, ''),
               json_extract(NEW.summary, '$.country.name'), 1
        WHERE NEW.in_latest = 1
        ON CONFLICT (view, type, status, country, location_kind) DO UPDATE SET n = n + 1, country_name = excluded.country_name;
    INSERT INTO attraction_facets (view, type, status, country, location_kind, country_name, n)
        SELECT 'settled', NEW.type, IFNULL(NEW.status, ''), IFNULL(NEW.country, ''), IFNULL(NEW.location_kind, ''),
               json_extract(NEW.summary, '$.country.name'), 1
        WHERE NEW.in_settled = 1
        ON CONFLICT (view, type, status, country, location_kind) DO UPDATE SET n = n + 1, country_name = excluded.country_name;
END;

CREATE TRIGGER attractions_facets_delete AFTER DELETE ON attractions
BEGIN
    UPDATE attraction_facets SET n = n - 1
        WHERE OLD.in_latest = 1 AND view = 'latest' AND type = OLD.type AND status = IFNULL(OLD.status, '')
          AND country = IFNULL(OLD.country, '') AND location_kind = IFNULL(OLD.location_kind, '');
    UPDATE attraction_facets SET n = n - 1
        WHERE OLD.in_settled = 1 AND view = 'settled' AND type = OLD.type AND status = IFNULL(OLD.status, '')
          AND country = IFNULL(OLD.country, '') AND location_kind = IFNULL(OLD.location_kind, '');
END;

CREATE TRIGGER attractions_facets_update AFTER UPDATE OF in_latest, in_settled, type, status, country, location_kind ON attractions
WHEN OLD.in_latest IS NOT NEW.in_latest OR OLD.in_settled IS NOT NEW.in_settled
  OR OLD.type IS NOT NEW.type OR OLD.status IS NOT NEW.status OR OLD.country IS NOT NEW.country
  OR OLD.location_kind IS NOT NEW.location_kind
BEGIN
    UPDATE attraction_facets SET n = n - 1
        WHERE OLD.in_latest = 1 AND view = 'latest' AND type = OLD.type AND status = IFNULL(OLD.status, '')
          AND country = IFNULL(OLD.country, '') AND location_kind = IFNULL(OLD.location_kind, '');
    UPDATE attraction_facets SET n = n - 1
        WHERE OLD.in_settled = 1 AND view = 'settled' AND type = OLD.type AND status = IFNULL(OLD.status, '')
          AND country = IFNULL(OLD.country, '') AND location_kind = IFNULL(OLD.location_kind, '');
    INSERT INTO attraction_facets (view, type, status, country, location_kind, country_name, n)
        SELECT 'latest', NEW.type, IFNULL(NEW.status, ''), IFNULL(NEW.country, ''), IFNULL(NEW.location_kind, ''),
               json_extract(NEW.summary, '$.country.name'), 1
        WHERE NEW.in_latest = 1
        ON CONFLICT (view, type, status, country, location_kind) DO UPDATE SET n = n + 1, country_name = excluded.country_name;
    INSERT INTO attraction_facets (view, type, status, country, location_kind, country_name, n)
        SELECT 'settled', NEW.type, IFNULL(NEW.status, ''), IFNULL(NEW.country, ''), IFNULL(NEW.location_kind, ''),
               json_extract(NEW.summary, '$.country.name'), 1
        WHERE NEW.in_settled = 1
        ON CONFLICT (view, type, status, country, location_kind) DO UPDATE SET n = n + 1, country_name = excluded.country_name;
END;
