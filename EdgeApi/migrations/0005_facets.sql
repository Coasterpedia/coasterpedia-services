-- /v1/attractions/facets (DR-010 §12.6). Attractions per view and
-- (type, status, country), kept by triggers like the /v1/meta counters, so a
-- facets call reads these cells (a few hundred) instead of grouping the table
-- three times. Every filter the facets take is one of the three columns; a new
-- filterable column means rebuilding this table with it.
--
-- Additive: a new table, filled here from what is stored, then kept by
-- triggers. The old Worker's ingest fires them as well, so it doesn't matter
-- whether this runs before or after the code deploy.
--
-- Blank status or country is '' here, so the primary key holds (SQLite lets a
-- NULL repeat in one). The Worker leaves it out of the lists, as no filter can
-- select it, but it still counts toward the total and the other dimensions.
CREATE TABLE attraction_facets (
    view          TEXT    NOT NULL,
    type          TEXT    NOT NULL,
    status        TEXT    NOT NULL,
    country       TEXT    NOT NULL,
    -- The wiki's own name for the country, from the newest summary counted here.
    country_name  TEXT,
    n             INTEGER NOT NULL,
    PRIMARY KEY (view, type, status, country)
) WITHOUT ROWID;

INSERT INTO attraction_facets (view, type, status, country, country_name, n)
SELECT 'latest', type, IFNULL(status, ''), IFNULL(country, ''), MAX(json_extract(summary, '$.country.name')), COUNT(*)
FROM attractions WHERE in_latest = 1 GROUP BY type, IFNULL(status, ''), IFNULL(country, '');

INSERT INTO attraction_facets (view, type, status, country, country_name, n)
SELECT 'settled', type, IFNULL(status, ''), IFNULL(country, ''), MAX(json_extract(summary, '$.country.name')), COUNT(*)
FROM attractions WHERE in_settled = 1 GROUP BY type, IFNULL(status, ''), IFNULL(country, '');

-- A cell that empties stays at 0 rather than being deleted: the Worker skips
-- it, and the next attraction to land there reuses it.
CREATE TRIGGER attractions_facets_insert AFTER INSERT ON attractions
BEGIN
    INSERT INTO attraction_facets (view, type, status, country, country_name, n)
        SELECT 'latest', NEW.type, IFNULL(NEW.status, ''), IFNULL(NEW.country, ''), json_extract(NEW.summary, '$.country.name'), 1
        WHERE NEW.in_latest = 1
        ON CONFLICT (view, type, status, country) DO UPDATE SET n = n + 1, country_name = excluded.country_name;
    INSERT INTO attraction_facets (view, type, status, country, country_name, n)
        SELECT 'settled', NEW.type, IFNULL(NEW.status, ''), IFNULL(NEW.country, ''), json_extract(NEW.summary, '$.country.name'), 1
        WHERE NEW.in_settled = 1
        ON CONFLICT (view, type, status, country) DO UPDATE SET n = n + 1, country_name = excluded.country_name;
END;

CREATE TRIGGER attractions_facets_delete AFTER DELETE ON attractions
BEGIN
    UPDATE attraction_facets SET n = n - 1
        WHERE OLD.in_latest = 1 AND view = 'latest'
          AND type = OLD.type AND status = IFNULL(OLD.status, '') AND country = IFNULL(OLD.country, '');
    UPDATE attraction_facets SET n = n - 1
        WHERE OLD.in_settled = 1 AND view = 'settled'
          AND type = OLD.type AND status = IFNULL(OLD.status, '') AND country = IFNULL(OLD.country, '');
END;

-- Ingest's upsert sets every column on every push, so the WHEN keeps an edit
-- that moves no facet from writing anything here.
CREATE TRIGGER attractions_facets_update AFTER UPDATE OF in_latest, in_settled, type, status, country ON attractions
WHEN OLD.in_latest IS NOT NEW.in_latest OR OLD.in_settled IS NOT NEW.in_settled
  OR OLD.type IS NOT NEW.type OR OLD.status IS NOT NEW.status OR OLD.country IS NOT NEW.country
BEGIN
    UPDATE attraction_facets SET n = n - 1
        WHERE OLD.in_latest = 1 AND view = 'latest'
          AND type = OLD.type AND status = IFNULL(OLD.status, '') AND country = IFNULL(OLD.country, '');
    UPDATE attraction_facets SET n = n - 1
        WHERE OLD.in_settled = 1 AND view = 'settled'
          AND type = OLD.type AND status = IFNULL(OLD.status, '') AND country = IFNULL(OLD.country, '');
    INSERT INTO attraction_facets (view, type, status, country, country_name, n)
        SELECT 'latest', NEW.type, IFNULL(NEW.status, ''), IFNULL(NEW.country, ''), json_extract(NEW.summary, '$.country.name'), 1
        WHERE NEW.in_latest = 1
        ON CONFLICT (view, type, status, country) DO UPDATE SET n = n + 1, country_name = excluded.country_name;
    INSERT INTO attraction_facets (view, type, status, country, country_name, n)
        SELECT 'settled', NEW.type, IFNULL(NEW.status, ''), IFNULL(NEW.country, ''), json_extract(NEW.summary, '$.country.name'), 1
        WHERE NEW.in_settled = 1
        ON CONFLICT (view, type, status, country) DO UPDATE SET n = n + 1, country_name = excluded.country_name;
END;
