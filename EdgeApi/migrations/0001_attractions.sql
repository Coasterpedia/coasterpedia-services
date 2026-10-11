-- One row per attraction, as the Projector last sent it. The edge derives
-- nothing: summary and record are stored as sent, and the other columns are
-- the Projector's own filter and sort values.
--
-- The settled view (DR-010 §6.3) will need a second row for a record with a
-- pending change. That is a new table shape, and this one is disposable, so it
-- is not anticipated here.
CREATE TABLE attractions (
    id          INTEGER PRIMARY KEY,
    type        TEXT    NOT NULL,
    status      TEXT,
    country     TEXT,
    sort_name   TEXT    NOT NULL,
    revision    INTEGER NOT NULL,
    updated_at  TEXT    NOT NULL,
    summary     TEXT    NOT NULL,
    record      TEXT    NOT NULL
);
CREATE INDEX attractions_by_name    ON attractions (sort_name, id);
CREATE INDEX attractions_by_type    ON attractions (type, sort_name, id);
CREATE INDEX attractions_by_country ON attractions (country, sort_name, id);

-- A page that became a redirect to another: its old ID answers 301 (DR-010 §5).
CREATE TABLE merges (
    kind     TEXT    NOT NULL,
    old_id   INTEGER NOT NULL,
    new_id   INTEGER NOT NULL,
    at       TEXT    NOT NULL,
    PRIMARY KEY (kind, old_id)
);

-- Every change applied, in order: the future /v1/changes, recorded from day one.
CREATE TABLE changes (
    seq          INTEGER PRIMARY KEY AUTOINCREMENT,
    kind         TEXT    NOT NULL,
    id           INTEGER NOT NULL,
    op           TEXT    NOT NULL,
    revision     INTEGER,
    merged_into  INTEGER,
    at           TEXT    NOT NULL
);

CREATE TABLE state (
    key    TEXT PRIMARY KEY,
    value  TEXT NOT NULL
);
