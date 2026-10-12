-- The attractions list's manufacturer= filter (DR-010 §12.9). One row per
-- attraction row and linked manufacturer, kept by triggers from the
-- manufacturers the Projector already puts in each summary. An attraction can
-- have several, so this can't be a column on attractions.
--
-- Each row carries the attraction's filter and sort columns, so a filtered
-- page seeks (manufacturer, sort key) to its cursor and reads its page, and
-- the other filters are checked here without touching attractions. The join
-- back for the summary is one key lookup per row returned.
--
-- Not per view: in_latest is in the key, so a record waiting to settle has
-- two sets of rows, and a read checks its flag. Only the rare pending record
-- is read twice, so one index per sort serves both views.
--
-- Additive: a new table, filled here from what is stored, then kept by
-- triggers. The old Worker's ingest fires them too, so the order of migration
-- and deploy doesn't matter. Unlinked names (a null id) have no row.
CREATE TABLE attraction_manufacturers (
    id            INTEGER NOT NULL,
    in_latest     INTEGER NOT NULL,
    -- The manufacturer's page ID, as in the summary's manufacturers[].id.
    manufacturer  INTEGER NOT NULL,
    in_settled    INTEGER NOT NULL,
    type          TEXT    NOT NULL,
    status        TEXT,
    country       TEXT,
    sort_name     TEXT    NOT NULL,
    height        REAL,
    speed         REAL,
    length        REAL,
    PRIMARY KEY (id, in_latest, manufacturer)
) WITHOUT ROWID;

-- Zamperla, the largest, has about 1,900 attractions on 2026-10-12: a filter
-- on type, status or country walks one manufacturer's range, never the table.
-- Each index carries the view flag and the filter columns (the key's id and
-- in_latest come with it), so that walk, and the facet counts, read the index
-- alone: one row read per link instead of two, for the same one write.
CREATE INDEX attraction_manufacturers_by_name   ON attraction_manufacturers (manufacturer, sort_name, id, in_settled, type, status, country);
CREATE INDEX attraction_manufacturers_by_height ON attraction_manufacturers (manufacturer, height, id, in_settled, type, status, country) WHERE height IS NOT NULL;
CREATE INDEX attraction_manufacturers_by_speed  ON attraction_manufacturers (manufacturer, speed, id, in_settled, type, status, country)  WHERE speed IS NOT NULL;
CREATE INDEX attraction_manufacturers_by_length ON attraction_manufacturers (manufacturer, length, id, in_settled, type, status, country) WHERE length IS NOT NULL;

-- DISTINCT: two links can resolve to one page ([[Arrow Development]] and
-- [[Arrow Dynamics]]), which is one manufacturer, so one row. Not OR IGNORE:
-- inside a trigger fired by ingest's upsert, the upsert's conflict handling
-- replaces the trigger's, and the duplicate fails the whole batch.
INSERT INTO attraction_manufacturers
    (id, in_latest, manufacturer, in_settled, type, status, country, sort_name, height, speed, length)
SELECT DISTINCT a.id, a.in_latest, json_extract(m.value, '$.id'), a.in_settled, a.type, a.status, a.country, a.sort_name,
       a.height, a.speed, a.length
FROM attractions a, json_each(a.summary, '$.manufacturers') m
WHERE json_type(m.value, '$.id') = 'integer';

CREATE TRIGGER attractions_manufacturers_insert AFTER INSERT ON attractions
BEGIN
    INSERT INTO attraction_manufacturers
        (id, in_latest, manufacturer, in_settled, type, status, country, sort_name, height, speed, length)
    SELECT NEW.id, NEW.in_latest, m.manufacturer, NEW.in_settled, NEW.type, NEW.status, NEW.country,
           NEW.sort_name, NEW.height, NEW.speed, NEW.length
    FROM (SELECT DISTINCT json_extract(value, '$.id') AS manufacturer FROM json_each(NEW.summary, '$.manufacturers')
          WHERE json_type(value, '$.id') = 'integer') m;
END;

CREATE TRIGGER attractions_manufacturers_delete AFTER DELETE ON attractions
BEGIN
    DELETE FROM attraction_manufacturers WHERE id = OLD.id AND in_latest = OLD.in_latest;
END;

-- Ingest's upsert sets every column on every push, so the WHEN keeps an edit
-- that changes nothing here from writing anything. Anything else rewrites the
-- row's links: a promote or a discard moves in_latest, which is in the key.
CREATE TRIGGER attractions_manufacturers_update AFTER UPDATE ON attractions
WHEN OLD.in_latest IS NOT NEW.in_latest OR OLD.in_settled IS NOT NEW.in_settled
  OR OLD.type IS NOT NEW.type OR OLD.status IS NOT NEW.status OR OLD.country IS NOT NEW.country
  OR OLD.sort_name IS NOT NEW.sort_name
  OR OLD.height IS NOT NEW.height OR OLD.speed IS NOT NEW.speed OR OLD.length IS NOT NEW.length
  OR json_extract(OLD.summary, '$.manufacturers') IS NOT json_extract(NEW.summary, '$.manufacturers')
BEGIN
    DELETE FROM attraction_manufacturers WHERE id = OLD.id AND in_latest = OLD.in_latest;
    INSERT INTO attraction_manufacturers
        (id, in_latest, manufacturer, in_settled, type, status, country, sort_name, height, speed, length)
    SELECT NEW.id, NEW.in_latest, m.manufacturer, NEW.in_settled, NEW.type, NEW.status, NEW.country,
           NEW.sort_name, NEW.height, NEW.speed, NEW.length
    FROM (SELECT DISTINCT json_extract(value, '$.id') AS manufacturer FROM json_each(NEW.summary, '$.manufacturers')
          WHERE json_type(value, '$.id') = 'integer') m;
END;
