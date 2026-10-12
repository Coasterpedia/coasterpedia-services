-- /v1/manufacturers (DR-010 §12.8). The same shape as parks (0006): one row
-- per record per view, two only while a change waits to settle, and every
-- read filters on one flag written as a literal so the partial indexes match.
--
-- Additive: a new table, and counters that start at 0. The first sweep after
-- the services image ships fills it, 261 manufacturers at roughly 5 writes
-- each (the row, its key, a name index per view, a change-log line).
CREATE TABLE manufacturers (
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

-- Name only: 261 manufacturers on 2026-10-12, so a status or country filter
-- walking the name index is bounded by a table smaller than one D1 page of
-- reads, and a filter index would cost a write on every row for nothing.
CREATE INDEX manufacturers_latest_by_name  ON manufacturers (sort_name, id) WHERE in_latest = 1;
CREATE INDEX manufacturers_settled_by_name ON manufacturers (sort_name, id) WHERE in_settled = 1;

-- /v1/meta's manufacturer counts, kept like the attraction ones (0002).
INSERT OR IGNORE INTO state (key, value) VALUES ('count_manufacturers_latest', '0');
INSERT OR IGNORE INTO state (key, value) VALUES ('count_manufacturers_settled', '0');

CREATE TRIGGER manufacturers_count_insert AFTER INSERT ON manufacturers
BEGIN
    UPDATE state SET value = CAST(value AS INTEGER) + 1 WHERE key = 'count_manufacturers_latest' AND NEW.in_latest = 1;
    UPDATE state SET value = CAST(value AS INTEGER) + 1 WHERE key = 'count_manufacturers_settled' AND NEW.in_settled = 1;
END;

CREATE TRIGGER manufacturers_count_delete AFTER DELETE ON manufacturers
BEGIN
    UPDATE state SET value = CAST(value AS INTEGER) - 1 WHERE key = 'count_manufacturers_latest' AND OLD.in_latest = 1;
    UPDATE state SET value = CAST(value AS INTEGER) - 1 WHERE key = 'count_manufacturers_settled' AND OLD.in_settled = 1;
END;

CREATE TRIGGER manufacturers_count_update AFTER UPDATE OF in_latest, in_settled ON manufacturers
WHEN OLD.in_latest <> NEW.in_latest OR OLD.in_settled <> NEW.in_settled
BEGIN
    UPDATE state SET value = CAST(value AS INTEGER) + NEW.in_latest - OLD.in_latest
        WHERE key = 'count_manufacturers_latest' AND OLD.in_latest <> NEW.in_latest;
    UPDATE state SET value = CAST(value AS INTEGER) + NEW.in_settled - OLD.in_settled
        WHERE key = 'count_manufacturers_settled' AND OLD.in_settled <> NEW.in_settled;
END;
