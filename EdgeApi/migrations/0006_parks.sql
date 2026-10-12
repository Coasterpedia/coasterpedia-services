-- /v1/parks (DR-010 §12.7). The same shape as attractions after 0002: one row
-- per record per view, two only while a change waits to settle, and every
-- read filters on one flag written as a literal so the partial indexes match.
--
-- Additive: a new table, and counters that start at 0. The first sweep after
-- the services image ships fills it, about 2,200 parks at roughly 5 writes
-- each (the row, its key, two partial indexes, a change-log line).
CREATE TABLE parks (
    id          INTEGER NOT NULL,
    in_latest   INTEGER NOT NULL,
    in_settled  INTEGER NOT NULL,
    status      TEXT,
    country     TEXT,
    -- US states and territories only, as the wiki's park infobox spells them.
    state       TEXT,
    -- The resort's ID: the page ID of the wrapper template that defines it.
    resort      INTEGER,
    sort_name   TEXT    NOT NULL,
    revision    INTEGER NOT NULL,
    updated_at  TEXT    NOT NULL,
    settles_at  TEXT,
    summary     TEXT    NOT NULL,
    record      TEXT    NOT NULL,
    PRIMARY KEY (id, in_latest)
);

-- No status or state index: a filter on either walks the name index, and the
-- table is small enough (2,166 parks on 2026-10-12) that the walk is bounded
-- by it, and either matches enough parks to fill a page early. Country gets
-- one, as it does on attractions, because most clients start there.
CREATE INDEX parks_latest_by_name     ON parks (sort_name, id)          WHERE in_latest = 1;
CREATE INDEX parks_latest_by_country  ON parks (country, sort_name, id) WHERE in_latest = 1;
CREATE INDEX parks_settled_by_name    ON parks (sort_name, id)          WHERE in_settled = 1;
CREATE INDEX parks_settled_by_country ON parks (country, sort_name, id) WHERE in_settled = 1;

-- A resort has two to six parks, so a resort filter walking the name index
-- would read the whole table to find them. 88 parks are in one; the
-- others cost no write here.
CREATE INDEX parks_latest_by_resort  ON parks (resort, sort_name, id) WHERE in_latest = 1 AND resort IS NOT NULL;
CREATE INDEX parks_settled_by_resort ON parks (resort, sort_name, id) WHERE in_settled = 1 AND resort IS NOT NULL;

-- /v1/meta's park counts, kept like the attraction ones (0002).
INSERT OR IGNORE INTO state (key, value) VALUES ('count_parks_latest', '0');
INSERT OR IGNORE INTO state (key, value) VALUES ('count_parks_settled', '0');

CREATE TRIGGER parks_count_insert AFTER INSERT ON parks
BEGIN
    UPDATE state SET value = CAST(value AS INTEGER) + 1 WHERE key = 'count_parks_latest' AND NEW.in_latest = 1;
    UPDATE state SET value = CAST(value AS INTEGER) + 1 WHERE key = 'count_parks_settled' AND NEW.in_settled = 1;
END;

CREATE TRIGGER parks_count_delete AFTER DELETE ON parks
BEGIN
    UPDATE state SET value = CAST(value AS INTEGER) - 1 WHERE key = 'count_parks_latest' AND OLD.in_latest = 1;
    UPDATE state SET value = CAST(value AS INTEGER) - 1 WHERE key = 'count_parks_settled' AND OLD.in_settled = 1;
END;

CREATE TRIGGER parks_count_update AFTER UPDATE OF in_latest, in_settled ON parks
WHEN OLD.in_latest <> NEW.in_latest OR OLD.in_settled <> NEW.in_settled
BEGIN
    UPDATE state SET value = CAST(value AS INTEGER) + NEW.in_latest - OLD.in_latest
        WHERE key = 'count_parks_latest' AND OLD.in_latest <> NEW.in_latest;
    UPDATE state SET value = CAST(value AS INTEGER) + NEW.in_settled - OLD.in_settled
        WHERE key = 'count_parks_settled' AND OLD.in_settled <> NEW.in_settled;
END;
