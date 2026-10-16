-- A park's past names in q and /v1/search (DR-010 §12.22), as attractions'
-- are (0014): the name, and each name in history.names that differs from it.
--
-- Additive: the old Worker never reads search_names' extra rows, and a park
-- without history.names keeps the one row it has. The sweep that brings
-- history.names re-pushes the ~195 renamed parks, and the update trigger
-- adds their names; the backfill covers any already stored.
DROP TRIGGER parks_names_insert;
DROP TRIGGER parks_names_update;

CREATE TRIGGER parks_names_insert AFTER INSERT ON parks
BEGIN
    INSERT INTO search_names (kind, id, in_latest, in_settled, name, current, display, sort_name, url)
    SELECT 'park', NEW.id, NEW.in_latest, NEW.in_settled, n.name, n.current,
           json_extract(NEW.summary, '$.name'), NEW.sort_name, json_extract(NEW.record, '$.url')
    FROM (SELECT json_extract(NEW.summary, '$.name') AS name, 1 AS current
          UNION
          SELECT DISTINCT json_extract(h.value, '$.name'), 0 FROM json_each(NEW.record, '$.history.names') h
          WHERE json_extract(h.value, '$.name') IS NOT json_extract(NEW.summary, '$.name')) n
    WHERE n.name IS NOT NULL AND json_extract(NEW.summary, '$.name') IS NOT NULL;
END;

CREATE TRIGGER parks_names_update AFTER UPDATE ON parks
WHEN OLD.in_latest IS NOT NEW.in_latest OR OLD.in_settled IS NOT NEW.in_settled OR OLD.sort_name IS NOT NEW.sort_name
  OR json_extract(OLD.summary, '$.name') IS NOT json_extract(NEW.summary, '$.name')
  OR json_extract(OLD.record, '$.history.names') IS NOT json_extract(NEW.record, '$.history.names')
  OR json_extract(OLD.record, '$.url') IS NOT json_extract(NEW.record, '$.url')
BEGIN
    DELETE FROM search_names WHERE kind = 'park' AND id = OLD.id AND in_latest = OLD.in_latest;
    INSERT INTO search_names (kind, id, in_latest, in_settled, name, current, display, sort_name, url)
    SELECT 'park', NEW.id, NEW.in_latest, NEW.in_settled, n.name, n.current,
           json_extract(NEW.summary, '$.name'), NEW.sort_name, json_extract(NEW.record, '$.url')
    FROM (SELECT json_extract(NEW.summary, '$.name') AS name, 1 AS current
          UNION
          SELECT DISTINCT json_extract(h.value, '$.name'), 0 FROM json_each(NEW.record, '$.history.names') h
          WHERE json_extract(h.value, '$.name') IS NOT json_extract(NEW.summary, '$.name')) n
    WHERE n.name IS NOT NULL AND json_extract(NEW.summary, '$.name') IS NOT NULL;
END;

INSERT INTO search_names (kind, id, in_latest, in_settled, name, current, display, sort_name, url)
SELECT DISTINCT 'park', p.id, p.in_latest, p.in_settled, json_extract(h.value, '$.name'), 0,
       json_extract(p.summary, '$.name'), p.sort_name, json_extract(p.record, '$.url')
FROM parks p, json_each(p.record, '$.history.names') h
WHERE json_extract(h.value, '$.name') IS NOT json_extract(p.summary, '$.name')
  AND json_extract(p.summary, '$.name') IS NOT NULL;
