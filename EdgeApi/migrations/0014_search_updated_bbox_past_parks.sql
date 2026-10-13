-- The list parameters the contract has promised since slice 0 (DR-010 §12.19):
-- updated_since and sort=updated, bbox, q and /v1/search, and a park's former
-- attractions. Everything here is derived from what is already stored, so the
-- Projector sends nothing new.
--
-- Additive: indexes, generated columns, and tables filled here from the rows
-- already stored, then kept by triggers that the old Worker's ingest fires too.

-- ---- updated ----------------------------------------------------------------

-- updated_since and sort=updated on attractions. The other kinds are small
-- enough (2,200 parks at most) that their name index answers both.
CREATE INDEX attractions_latest_by_updated  ON attractions (updated_at, id) WHERE in_latest = 1;
CREATE INDEX attractions_settled_by_updated ON attractions (updated_at, id) WHERE in_settled = 1;

-- ---- bbox ---------------------------------------------------------------------

-- Coordinates as the record holds them. cell is a 1° grid square, so a box is
-- a short list of squares on an index rather than a latitude band across the
-- world: 180 rows of 360, clamped at the poles and the antimeridian.
ALTER TABLE attractions ADD COLUMN lat REAL GENERATED ALWAYS AS (json_extract(record, '$.location.coordinates.lat')) VIRTUAL;
ALTER TABLE attractions ADD COLUMN lon REAL GENERATED ALWAYS AS (json_extract(record, '$.location.coordinates.lon')) VIRTUAL;
ALTER TABLE attractions ADD COLUMN cell INTEGER GENERATED ALWAYS AS
    (min(CAST(lat + 90 AS INTEGER), 179) * 360 + min(CAST(lon + 180 AS INTEGER), 359)) VIRTUAL;
CREATE INDEX attractions_latest_by_cell  ON attractions (cell, sort_name, id) WHERE in_latest = 1;
CREATE INDEX attractions_settled_by_cell ON attractions (cell, sort_name, id) WHERE in_settled = 1;

-- Parks: 2,200 rows, filtered on the name index walk, as their other filters are.
ALTER TABLE parks ADD COLUMN lat REAL GENERATED ALWAYS AS (json_extract(record, '$.coordinates.lat')) VIRTUAL;
ALTER TABLE parks ADD COLUMN lon REAL GENERATED ALWAYS AS (json_extract(record, '$.coordinates.lon')) VIRTUAL;

-- ---- names: q and /v1/search ---------------------------------------------------

-- Every name a record answers to, one row each: its own name (current = 1),
-- and for an attraction the past names in history.names. Kept by triggers on
-- each kind's table. display and url are the record's, for /v1/search.
CREATE TABLE search_names (
    rid         INTEGER PRIMARY KEY,
    kind        TEXT    NOT NULL,
    id          INTEGER NOT NULL,
    in_latest   INTEGER NOT NULL,
    in_settled  INTEGER NOT NULL,
    name        TEXT    NOT NULL,
    current     INTEGER NOT NULL,
    display     TEXT    NOT NULL,
    sort_name   TEXT    NOT NULL,
    url         TEXT
);
CREATE INDEX search_names_by_record ON search_names (kind, id, in_latest);

-- Case- and accent-blind, token by token; the Worker turns each word of q into
-- a prefix ("bat" finds Batman). External content: the names live once, above.
CREATE VIRTUAL TABLE name_fts USING fts5(
    name, content = 'search_names', content_rowid = 'rid', tokenize = 'unicode61 remove_diacritics 2'
);

CREATE TRIGGER search_names_insert AFTER INSERT ON search_names
BEGIN
    INSERT INTO name_fts (rowid, name) VALUES (NEW.rid, NEW.name);
END;

CREATE TRIGGER search_names_delete AFTER DELETE ON search_names
BEGIN
    INSERT INTO name_fts (name_fts, rowid, name) VALUES ('delete', OLD.rid, OLD.name);
END;

-- Attractions: the name, and each past name that differs from it.
CREATE TRIGGER attractions_names_insert AFTER INSERT ON attractions
BEGIN
    INSERT INTO search_names (kind, id, in_latest, in_settled, name, current, display, sort_name, url)
    SELECT 'attraction', NEW.id, NEW.in_latest, NEW.in_settled, n.name, n.current,
           json_extract(NEW.summary, '$.name'), NEW.sort_name, json_extract(NEW.record, '$.url')
    FROM (SELECT json_extract(NEW.summary, '$.name') AS name, 1 AS current
          UNION
          SELECT DISTINCT json_extract(h.value, '$.name'), 0 FROM json_each(NEW.record, '$.history.names') h
          WHERE json_extract(h.value, '$.name') IS NOT json_extract(NEW.summary, '$.name')) n
    WHERE n.name IS NOT NULL AND json_extract(NEW.summary, '$.name') IS NOT NULL;
END;

CREATE TRIGGER attractions_names_delete AFTER DELETE ON attractions
BEGIN
    DELETE FROM search_names WHERE kind = 'attraction' AND id = OLD.id AND in_latest = OLD.in_latest;
END;

CREATE TRIGGER attractions_names_update AFTER UPDATE ON attractions
WHEN OLD.in_latest IS NOT NEW.in_latest OR OLD.in_settled IS NOT NEW.in_settled OR OLD.sort_name IS NOT NEW.sort_name
  OR json_extract(OLD.summary, '$.name') IS NOT json_extract(NEW.summary, '$.name')
  OR json_extract(OLD.record, '$.history.names') IS NOT json_extract(NEW.record, '$.history.names')
  OR json_extract(OLD.record, '$.url') IS NOT json_extract(NEW.record, '$.url')
BEGIN
    DELETE FROM search_names WHERE kind = 'attraction' AND id = OLD.id AND in_latest = OLD.in_latest;
    INSERT INTO search_names (kind, id, in_latest, in_settled, name, current, display, sort_name, url)
    SELECT 'attraction', NEW.id, NEW.in_latest, NEW.in_settled, n.name, n.current,
           json_extract(NEW.summary, '$.name'), NEW.sort_name, json_extract(NEW.record, '$.url')
    FROM (SELECT json_extract(NEW.summary, '$.name') AS name, 1 AS current
          UNION
          SELECT DISTINCT json_extract(h.value, '$.name'), 0 FROM json_each(NEW.record, '$.history.names') h
          WHERE json_extract(h.value, '$.name') IS NOT json_extract(NEW.summary, '$.name')) n
    WHERE n.name IS NOT NULL AND json_extract(NEW.summary, '$.name') IS NOT NULL;
END;

-- The other kinds: the name only. A row without a name (only ever a test's) has none.
CREATE TRIGGER parks_names_insert AFTER INSERT ON parks
BEGIN
    INSERT INTO search_names (kind, id, in_latest, in_settled, name, current, display, sort_name, url)
    SELECT 'park', NEW.id, NEW.in_latest, NEW.in_settled, json_extract(NEW.summary, '$.name'), 1,
           json_extract(NEW.summary, '$.name'), NEW.sort_name, json_extract(NEW.record, '$.url')
    WHERE json_extract(NEW.summary, '$.name') IS NOT NULL;
END;
CREATE TRIGGER parks_names_delete AFTER DELETE ON parks
BEGIN
    DELETE FROM search_names WHERE kind = 'park' AND id = OLD.id AND in_latest = OLD.in_latest;
END;
CREATE TRIGGER parks_names_update AFTER UPDATE ON parks
WHEN OLD.in_latest IS NOT NEW.in_latest OR OLD.in_settled IS NOT NEW.in_settled OR OLD.sort_name IS NOT NEW.sort_name
  OR json_extract(OLD.summary, '$.name') IS NOT json_extract(NEW.summary, '$.name')
  OR json_extract(OLD.record, '$.url') IS NOT json_extract(NEW.record, '$.url')
BEGIN
    DELETE FROM search_names WHERE kind = 'park' AND id = OLD.id AND in_latest = OLD.in_latest;
    INSERT INTO search_names (kind, id, in_latest, in_settled, name, current, display, sort_name, url)
    SELECT 'park', NEW.id, NEW.in_latest, NEW.in_settled, json_extract(NEW.summary, '$.name'), 1,
           json_extract(NEW.summary, '$.name'), NEW.sort_name, json_extract(NEW.record, '$.url')
    WHERE json_extract(NEW.summary, '$.name') IS NOT NULL;
END;

CREATE TRIGGER manufacturers_names_insert AFTER INSERT ON manufacturers
BEGIN
    INSERT INTO search_names (kind, id, in_latest, in_settled, name, current, display, sort_name, url)
    SELECT 'manufacturer', NEW.id, NEW.in_latest, NEW.in_settled, json_extract(NEW.summary, '$.name'), 1,
           json_extract(NEW.summary, '$.name'), NEW.sort_name, json_extract(NEW.record, '$.url')
    WHERE json_extract(NEW.summary, '$.name') IS NOT NULL;
END;
CREATE TRIGGER manufacturers_names_delete AFTER DELETE ON manufacturers
BEGIN
    DELETE FROM search_names WHERE kind = 'manufacturer' AND id = OLD.id AND in_latest = OLD.in_latest;
END;
CREATE TRIGGER manufacturers_names_update AFTER UPDATE ON manufacturers
WHEN OLD.in_latest IS NOT NEW.in_latest OR OLD.in_settled IS NOT NEW.in_settled OR OLD.sort_name IS NOT NEW.sort_name
  OR json_extract(OLD.summary, '$.name') IS NOT json_extract(NEW.summary, '$.name')
  OR json_extract(OLD.record, '$.url') IS NOT json_extract(NEW.record, '$.url')
BEGIN
    DELETE FROM search_names WHERE kind = 'manufacturer' AND id = OLD.id AND in_latest = OLD.in_latest;
    INSERT INTO search_names (kind, id, in_latest, in_settled, name, current, display, sort_name, url)
    SELECT 'manufacturer', NEW.id, NEW.in_latest, NEW.in_settled, json_extract(NEW.summary, '$.name'), 1,
           json_extract(NEW.summary, '$.name'), NEW.sort_name, json_extract(NEW.record, '$.url')
    WHERE json_extract(NEW.summary, '$.name') IS NOT NULL;
END;

CREATE TRIGGER products_names_insert AFTER INSERT ON products
BEGIN
    INSERT INTO search_names (kind, id, in_latest, in_settled, name, current, display, sort_name, url)
    SELECT 'product', NEW.id, NEW.in_latest, NEW.in_settled, json_extract(NEW.summary, '$.name'), 1,
           json_extract(NEW.summary, '$.name'), NEW.sort_name, json_extract(NEW.record, '$.url')
    WHERE json_extract(NEW.summary, '$.name') IS NOT NULL;
END;
CREATE TRIGGER products_names_delete AFTER DELETE ON products
BEGIN
    DELETE FROM search_names WHERE kind = 'product' AND id = OLD.id AND in_latest = OLD.in_latest;
END;
CREATE TRIGGER products_names_update AFTER UPDATE ON products
WHEN OLD.in_latest IS NOT NEW.in_latest OR OLD.in_settled IS NOT NEW.in_settled OR OLD.sort_name IS NOT NEW.sort_name
  OR json_extract(OLD.summary, '$.name') IS NOT json_extract(NEW.summary, '$.name')
  OR json_extract(OLD.record, '$.url') IS NOT json_extract(NEW.record, '$.url')
BEGIN
    DELETE FROM search_names WHERE kind = 'product' AND id = OLD.id AND in_latest = OLD.in_latest;
    INSERT INTO search_names (kind, id, in_latest, in_settled, name, current, display, sort_name, url)
    SELECT 'product', NEW.id, NEW.in_latest, NEW.in_settled, json_extract(NEW.summary, '$.name'), 1,
           json_extract(NEW.summary, '$.name'), NEW.sort_name, json_extract(NEW.record, '$.url')
    WHERE json_extract(NEW.summary, '$.name') IS NOT NULL;
END;

CREATE TRIGGER park_companies_names_insert AFTER INSERT ON park_companies
BEGIN
    INSERT INTO search_names (kind, id, in_latest, in_settled, name, current, display, sort_name, url)
    SELECT 'park_company', NEW.id, NEW.in_latest, NEW.in_settled, json_extract(NEW.summary, '$.name'), 1,
           json_extract(NEW.summary, '$.name'), NEW.sort_name, json_extract(NEW.record, '$.url')
    WHERE json_extract(NEW.summary, '$.name') IS NOT NULL;
END;
CREATE TRIGGER park_companies_names_delete AFTER DELETE ON park_companies
BEGIN
    DELETE FROM search_names WHERE kind = 'park_company' AND id = OLD.id AND in_latest = OLD.in_latest;
END;
CREATE TRIGGER park_companies_names_update AFTER UPDATE ON park_companies
WHEN OLD.in_latest IS NOT NEW.in_latest OR OLD.in_settled IS NOT NEW.in_settled OR OLD.sort_name IS NOT NEW.sort_name
  OR json_extract(OLD.summary, '$.name') IS NOT json_extract(NEW.summary, '$.name')
  OR json_extract(OLD.record, '$.url') IS NOT json_extract(NEW.record, '$.url')
BEGIN
    DELETE FROM search_names WHERE kind = 'park_company' AND id = OLD.id AND in_latest = OLD.in_latest;
    INSERT INTO search_names (kind, id, in_latest, in_settled, name, current, display, sort_name, url)
    SELECT 'park_company', NEW.id, NEW.in_latest, NEW.in_settled, json_extract(NEW.summary, '$.name'), 1,
           json_extract(NEW.summary, '$.name'), NEW.sort_name, json_extract(NEW.record, '$.url')
    WHERE json_extract(NEW.summary, '$.name') IS NOT NULL;
END;

-- What is already stored.
INSERT INTO search_names (kind, id, in_latest, in_settled, name, current, display, sort_name, url)
SELECT 'attraction', a.id, a.in_latest, a.in_settled, n.name, n.current, json_extract(a.summary, '$.name'), a.sort_name,
       json_extract(a.record, '$.url')
FROM attractions a,
     (SELECT json_extract(a2.summary, '$.name') AS name, 1 AS current, a2.id AS aid, a2.in_latest AS alatest FROM attractions a2
      UNION
      SELECT DISTINCT json_extract(h.value, '$.name'), 0, a3.id, a3.in_latest FROM attractions a3, json_each(a3.record, '$.history.names') h
      WHERE json_extract(h.value, '$.name') IS NOT json_extract(a3.summary, '$.name')) n
WHERE n.aid = a.id AND n.alatest = a.in_latest AND n.name IS NOT NULL AND json_extract(a.summary, '$.name') IS NOT NULL;
INSERT INTO search_names (kind, id, in_latest, in_settled, name, current, display, sort_name, url)
SELECT 'park', id, in_latest, in_settled, json_extract(summary, '$.name'), 1, json_extract(summary, '$.name'), sort_name, json_extract(record, '$.url') FROM parks WHERE json_extract(summary, '$.name') IS NOT NULL;
INSERT INTO search_names (kind, id, in_latest, in_settled, name, current, display, sort_name, url)
SELECT 'manufacturer', id, in_latest, in_settled, json_extract(summary, '$.name'), 1, json_extract(summary, '$.name'), sort_name, json_extract(record, '$.url') FROM manufacturers WHERE json_extract(summary, '$.name') IS NOT NULL;
INSERT INTO search_names (kind, id, in_latest, in_settled, name, current, display, sort_name, url)
SELECT 'product', id, in_latest, in_settled, json_extract(summary, '$.name'), 1, json_extract(summary, '$.name'), sort_name, json_extract(record, '$.url') FROM products WHERE json_extract(summary, '$.name') IS NOT NULL;
INSERT INTO search_names (kind, id, in_latest, in_settled, name, current, display, sort_name, url)
SELECT 'park_company', id, in_latest, in_settled, json_extract(summary, '$.name'), 1, json_extract(summary, '$.name'), sort_name, json_extract(record, '$.url') FROM park_companies WHERE json_extract(summary, '$.name') IS NOT NULL;

-- ---- a park's former attractions ------------------------------------------------

-- One row per attraction row and park it had a past stint at
-- (history.locations[].park.id), for /v1/parks/{id}/attractions?include=former.
-- A park lists its own range; the attraction is joined back for everything else.
CREATE TABLE attraction_past_parks (
    park       INTEGER NOT NULL,
    id         INTEGER NOT NULL,
    in_latest  INTEGER NOT NULL,
    PRIMARY KEY (park, id, in_latest)
) WITHOUT ROWID;
CREATE INDEX attraction_past_parks_by_attraction ON attraction_past_parks (id, in_latest);

INSERT INTO attraction_past_parks (park, id, in_latest)
SELECT DISTINCT json_extract(l.value, '$.park.id'), a.id, a.in_latest
FROM attractions a, json_each(a.record, '$.history.locations') l
WHERE json_type(l.value, '$.park.id') = 'integer';

CREATE TRIGGER attractions_past_parks_insert AFTER INSERT ON attractions
BEGIN
    INSERT INTO attraction_past_parks (park, id, in_latest)
    SELECT DISTINCT json_extract(l.value, '$.park.id'), NEW.id, NEW.in_latest
    FROM json_each(NEW.record, '$.history.locations') l
    WHERE json_type(l.value, '$.park.id') = 'integer';
END;

CREATE TRIGGER attractions_past_parks_delete AFTER DELETE ON attractions
BEGIN
    DELETE FROM attraction_past_parks WHERE id = OLD.id AND in_latest = OLD.in_latest;
END;

CREATE TRIGGER attractions_past_parks_update AFTER UPDATE ON attractions
WHEN OLD.in_latest IS NOT NEW.in_latest
  OR json_extract(OLD.record, '$.history.locations') IS NOT json_extract(NEW.record, '$.history.locations')
BEGIN
    DELETE FROM attraction_past_parks WHERE id = OLD.id AND in_latest = OLD.in_latest;
    INSERT INTO attraction_past_parks (park, id, in_latest)
    SELECT DISTINCT json_extract(l.value, '$.park.id'), NEW.id, NEW.in_latest
    FROM json_each(NEW.record, '$.history.locations') l
    WHERE json_type(l.value, '$.park.id') = 'integer';
END;
