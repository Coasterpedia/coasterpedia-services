-- /v1/products (DR-010 §12.10). The same shape as manufacturers (0007): one
-- row per record per view, two only while a change waits to settle, and every
-- read filters on one flag written as a literal so the partial indexes match.
--
-- Additive: new tables, and counters that start at 0. The first sweep after
-- the services image ships fills it: 1,092 products on 2026-10-12.
CREATE TABLE products (
    id          INTEGER NOT NULL,
    in_latest   INTEGER NOT NULL,
    in_settled  INTEGER NOT NULL,
    type        TEXT    NOT NULL,
    status      TEXT,
    sort_name   TEXT    NOT NULL,
    revision    INTEGER NOT NULL,
    updated_at  TEXT    NOT NULL,
    settles_at  TEXT,
    summary     TEXT    NOT NULL,
    record      TEXT    NOT NULL,
    PRIMARY KEY (id, in_latest)
);

-- Name only, as manufacturers: a type or status filter walks the name index,
-- bounded by a table of about 1,100 rows (test/products.test.ts pins it).
CREATE INDEX products_latest_by_name  ON products (sort_name, id) WHERE in_latest = 1;
CREATE INDEX products_settled_by_name ON products (sort_name, id) WHERE in_settled = 1;

-- /v1/products?manufacturer= and /v1/manufacturers/{id}/products, kept from
-- the summary's manufacturers as attraction_manufacturers is (0008): a
-- product can name several (9 do on 2026-10-12). The one index carries the
-- view flag and the filters, so a manufacturer's page reads the index alone
-- and joins back once per row returned.
CREATE TABLE product_manufacturers (
    id            INTEGER NOT NULL,
    in_latest     INTEGER NOT NULL,
    manufacturer  INTEGER NOT NULL,
    in_settled    INTEGER NOT NULL,
    type          TEXT    NOT NULL,
    status        TEXT,
    sort_name     TEXT    NOT NULL,
    PRIMARY KEY (id, in_latest, manufacturer)
) WITHOUT ROWID;

CREATE INDEX product_manufacturers_by_name ON product_manufacturers (manufacturer, sort_name, id, in_settled, type, status);

-- DISTINCT, not OR IGNORE, for the reason 0008 gives: two links to one page
-- are one row, and inside a trigger fired by an upsert, OR IGNORE is overridden.
CREATE TRIGGER products_manufacturers_insert AFTER INSERT ON products
BEGIN
    INSERT INTO product_manufacturers (id, in_latest, manufacturer, in_settled, type, status, sort_name)
    SELECT NEW.id, NEW.in_latest, m.manufacturer, NEW.in_settled, NEW.type, NEW.status, NEW.sort_name
    FROM (SELECT DISTINCT json_extract(value, '$.id') AS manufacturer FROM json_each(NEW.summary, '$.manufacturers')
          WHERE json_type(value, '$.id') = 'integer') m;
END;

CREATE TRIGGER products_manufacturers_delete AFTER DELETE ON products
BEGIN
    DELETE FROM product_manufacturers WHERE id = OLD.id AND in_latest = OLD.in_latest;
END;

-- Ingest sets every column on every push; the WHEN keeps an edit that changes
-- nothing here from writing anything.
CREATE TRIGGER products_manufacturers_update AFTER UPDATE ON products
WHEN OLD.in_latest IS NOT NEW.in_latest OR OLD.in_settled IS NOT NEW.in_settled
  OR OLD.type IS NOT NEW.type OR OLD.status IS NOT NEW.status OR OLD.sort_name IS NOT NEW.sort_name
  OR json_extract(OLD.summary, '$.manufacturers') IS NOT json_extract(NEW.summary, '$.manufacturers')
BEGIN
    DELETE FROM product_manufacturers WHERE id = OLD.id AND in_latest = OLD.in_latest;
    INSERT INTO product_manufacturers (id, in_latest, manufacturer, in_settled, type, status, sort_name)
    SELECT NEW.id, NEW.in_latest, m.manufacturer, NEW.in_settled, NEW.type, NEW.status, NEW.sort_name
    FROM (SELECT DISTINCT json_extract(value, '$.id') AS manufacturer FROM json_each(NEW.summary, '$.manufacturers')
          WHERE json_type(value, '$.id') = 'integer') m;
END;

-- /v1/meta's product counts, kept like the others (0002).
INSERT OR IGNORE INTO state (key, value) VALUES ('count_products_latest', '0');
INSERT OR IGNORE INTO state (key, value) VALUES ('count_products_settled', '0');

CREATE TRIGGER products_count_insert AFTER INSERT ON products
BEGIN
    UPDATE state SET value = CAST(value AS INTEGER) + 1 WHERE key = 'count_products_latest' AND NEW.in_latest = 1;
    UPDATE state SET value = CAST(value AS INTEGER) + 1 WHERE key = 'count_products_settled' AND NEW.in_settled = 1;
END;

CREATE TRIGGER products_count_delete AFTER DELETE ON products
BEGIN
    UPDATE state SET value = CAST(value AS INTEGER) - 1 WHERE key = 'count_products_latest' AND OLD.in_latest = 1;
    UPDATE state SET value = CAST(value AS INTEGER) - 1 WHERE key = 'count_products_settled' AND OLD.in_settled = 1;
END;

CREATE TRIGGER products_count_update AFTER UPDATE OF in_latest, in_settled ON products
WHEN OLD.in_latest <> NEW.in_latest OR OLD.in_settled <> NEW.in_settled
BEGIN
    UPDATE state SET value = CAST(value AS INTEGER) + NEW.in_latest - OLD.in_latest
        WHERE key = 'count_products_latest' AND OLD.in_latest <> NEW.in_latest;
    UPDATE state SET value = CAST(value AS INTEGER) + NEW.in_settled - OLD.in_settled
        WHERE key = 'count_products_settled' AND OLD.in_settled <> NEW.in_settled;
END;
