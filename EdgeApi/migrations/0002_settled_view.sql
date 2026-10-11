-- The settled view (DR-010 §6.3). A record is one row while its latest and
-- settled versions agree, and two while a change waits to settle: the latest
-- row (in_latest = 1, in_settled = 0) and the settled one it will replace
-- (in_latest = 0, in_settled = 1). A record that has never settled has only
-- the latest row. Every read filters on one flag.
--
-- Not additive: the key changes, so the table is rebuilt. Between this
-- migration and the deploy that follows it, the old Worker's ingest fails
-- (its ON CONFLICT (id) no longer matches a key) and the Projector's outbox
-- holds the batches until the new Worker takes them. Reads keep working:
-- every existing row becomes both views, as the Projector's own state says.
CREATE TABLE attraction_rows (
    id          INTEGER NOT NULL,
    in_latest   INTEGER NOT NULL,
    in_settled  INTEGER NOT NULL,
    type        TEXT    NOT NULL,
    status      TEXT,
    country     TEXT,
    sort_name   TEXT    NOT NULL,
    revision    INTEGER NOT NULL,
    updated_at  TEXT    NOT NULL,
    -- On a latest row waiting to settle: when it will, if nothing changes.
    settles_at  TEXT,
    summary     TEXT    NOT NULL,
    record      TEXT    NOT NULL,
    PRIMARY KEY (id, in_latest)
);

INSERT INTO attraction_rows (id, in_latest, in_settled, type, status, country, sort_name, revision, updated_at, settles_at, summary, record)
SELECT id, 1, 1, type, status, country, sort_name, revision, updated_at, NULL, summary, record FROM attractions;

DROP TABLE attractions;
ALTER TABLE attraction_rows RENAME TO attractions;

-- Partial indexes, one set per view: the Worker writes the flag into the SQL
-- as a literal, so SQLite can match the index's WHERE.
CREATE INDEX attractions_latest_by_name     ON attractions (sort_name, id)          WHERE in_latest = 1;
CREATE INDEX attractions_latest_by_type     ON attractions (type, sort_name, id)    WHERE in_latest = 1;
CREATE INDEX attractions_latest_by_country  ON attractions (country, sort_name, id) WHERE in_latest = 1;
CREATE INDEX attractions_settled_by_name    ON attractions (sort_name, id)          WHERE in_settled = 1;
CREATE INDEX attractions_settled_by_type    ON attractions (type, sort_name, id)    WHERE in_settled = 1;
CREATE INDEX attractions_settled_by_country ON attractions (country, sort_name, id) WHERE in_settled = 1;

-- Which views each change touched. /v1/changes?view=settled reads only its
-- own, so a vandal edit and its revert never appear there.
--
-- Rebuilt rather than altered, to drop AUTOINCREMENT: it writes a second row
-- (sqlite_sequence) on every change. Nothing deletes from this table, so a
-- plain INTEGER PRIMARY KEY still only ever goes up.
CREATE TABLE change_rows (
    seq          INTEGER PRIMARY KEY,
    kind         TEXT    NOT NULL,
    id           INTEGER NOT NULL,
    op           TEXT    NOT NULL,
    revision     INTEGER,
    merged_into  INTEGER,
    at           TEXT    NOT NULL,
    in_latest    INTEGER NOT NULL DEFAULT 1,
    in_settled   INTEGER NOT NULL DEFAULT 1
);

INSERT INTO change_rows (seq, kind, id, op, revision, merged_into, at)
SELECT seq, kind, id, op, revision, merged_into, at FROM changes;

DROP TABLE changes;
ALTER TABLE change_rows RENAME TO changes;

-- Records per view, kept by triggers so /v1/meta reads two rows instead of
-- counting the table: it needs no key, and a COUNT(*) read every row each
-- call (12,600 rows on 2026-10-11). Each trigger writes only when a flag
-- actually changes, so an ordinary edit costs nothing extra.
INSERT INTO state (key, value) SELECT 'count_latest', COUNT(*) FROM attractions WHERE in_latest = 1;
INSERT INTO state (key, value) SELECT 'count_settled', COUNT(*) FROM attractions WHERE in_settled = 1;

CREATE TRIGGER attractions_count_insert AFTER INSERT ON attractions
BEGIN
    UPDATE state SET value = CAST(value AS INTEGER) + 1 WHERE key = 'count_latest' AND NEW.in_latest = 1;
    UPDATE state SET value = CAST(value AS INTEGER) + 1 WHERE key = 'count_settled' AND NEW.in_settled = 1;
END;

CREATE TRIGGER attractions_count_delete AFTER DELETE ON attractions
BEGIN
    UPDATE state SET value = CAST(value AS INTEGER) - 1 WHERE key = 'count_latest' AND OLD.in_latest = 1;
    UPDATE state SET value = CAST(value AS INTEGER) - 1 WHERE key = 'count_settled' AND OLD.in_settled = 1;
END;

CREATE TRIGGER attractions_count_update AFTER UPDATE OF in_latest, in_settled ON attractions
WHEN OLD.in_latest <> NEW.in_latest OR OLD.in_settled <> NEW.in_settled
BEGIN
    UPDATE state SET value = CAST(value AS INTEGER) + NEW.in_latest - OLD.in_latest
        WHERE key = 'count_latest' AND OLD.in_latest <> NEW.in_latest;
    UPDATE state SET value = CAST(value AS INTEGER) + NEW.in_settled - OLD.in_settled
        WHERE key = 'count_settled' AND OLD.in_settled <> NEW.in_settled;
END;
