-- /v1/products/{id}/installations, the attractions' product= filter and each
-- product's installations count (DR-010 §12.13). One row per attraction row
-- and linked product, kept by triggers from the products the Projector puts in
-- each summary, as attraction_manufacturers (0008) is from its manufacturers.
--
-- One index, by name: the largest product had about 135 installations on
-- 2026-10-12 (Zierer's Wave Swinger), so a stat sort reads the product's rows
-- and sorts them, still bounded by the product. Unlinked names have no row.
--
-- Additive: new tables, filled here from what is stored (nothing, until the
-- services image sends products), then kept by triggers.
CREATE TABLE attraction_products (
    id          INTEGER NOT NULL,
    in_latest   INTEGER NOT NULL,
    -- The product's page ID, as in the summary's products[].product.id.
    product     INTEGER NOT NULL,
    in_settled  INTEGER NOT NULL,
    -- As the page writes it; matched case-blind by ?variant=.
    variant     TEXT COLLATE NOCASE,
    type        TEXT    NOT NULL,
    status      TEXT,
    country     TEXT,
    sort_name   TEXT    NOT NULL,
    height      REAL,
    speed       REAL,
    length      REAL,
    PRIMARY KEY (id, in_latest, product)
) WITHOUT ROWID;

CREATE INDEX attraction_products_by_name ON attraction_products (product, sort_name, id, in_settled, type, status, country, variant);

-- GROUP BY product: two links can resolve to one page, which is one product,
-- so one row. Not OR IGNORE, for 0008's reason: inside a trigger fired by
-- ingest's upsert, the upsert's conflict handling replaces the trigger's.
INSERT INTO attraction_products
    (id, in_latest, product, in_settled, variant, type, status, country, sort_name, height, speed, length)
SELECT a.id, a.in_latest, json_extract(p.value, '$.product.id'), a.in_settled, MAX(json_extract(p.value, '$.variant')),
       a.type, a.status, a.country, a.sort_name, a.height, a.speed, a.length
FROM attractions a, json_each(a.summary, '$.products') p
WHERE json_type(p.value, '$.product.id') = 'integer'
GROUP BY a.id, a.in_latest, json_extract(p.value, '$.product.id');

-- Each product's installations per view, so a product list reads one row per
-- product for its counts, never COUNT(*) (§12.3). A row reaching 0 stays.
CREATE TABLE product_installations (
    product  INTEGER PRIMARY KEY,
    latest   INTEGER NOT NULL DEFAULT 0,
    settled  INTEGER NOT NULL DEFAULT 0
);

INSERT INTO product_installations (product, latest, settled)
SELECT product, SUM(in_latest), SUM(in_settled) FROM attraction_products GROUP BY product;

-- No upsert: the first statement adds a missing row, the second counts.
CREATE TRIGGER attraction_products_count_insert AFTER INSERT ON attraction_products
BEGIN
    INSERT INTO product_installations (product)
        SELECT NEW.product WHERE NOT EXISTS (SELECT 1 FROM product_installations WHERE product = NEW.product);
    UPDATE product_installations SET latest = latest + NEW.in_latest, settled = settled + NEW.in_settled
        WHERE product = NEW.product;
END;

CREATE TRIGGER attraction_products_count_delete AFTER DELETE ON attraction_products
BEGIN
    UPDATE product_installations SET latest = latest - OLD.in_latest, settled = settled - OLD.in_settled
        WHERE product = OLD.product;
END;

CREATE TRIGGER attractions_products_insert AFTER INSERT ON attractions
BEGIN
    INSERT INTO attraction_products
        (id, in_latest, product, in_settled, variant, type, status, country, sort_name, height, speed, length)
    SELECT NEW.id, NEW.in_latest, p.product, NEW.in_settled, p.variant, NEW.type, NEW.status, NEW.country,
           NEW.sort_name, NEW.height, NEW.speed, NEW.length
    FROM (SELECT json_extract(value, '$.product.id') AS product, MAX(json_extract(value, '$.variant')) AS variant
          FROM json_each(NEW.summary, '$.products') WHERE json_type(value, '$.product.id') = 'integer'
          GROUP BY json_extract(value, '$.product.id')) p;
END;

CREATE TRIGGER attractions_products_delete AFTER DELETE ON attractions
BEGIN
    DELETE FROM attraction_products WHERE id = OLD.id AND in_latest = OLD.in_latest;
END;

-- The WHEN as 0008's: an edit that changes nothing here writes nothing. A
-- promote or a discard moves a flag, so the counts follow it.
CREATE TRIGGER attractions_products_update AFTER UPDATE ON attractions
WHEN OLD.in_latest IS NOT NEW.in_latest OR OLD.in_settled IS NOT NEW.in_settled
  OR OLD.type IS NOT NEW.type OR OLD.status IS NOT NEW.status OR OLD.country IS NOT NEW.country
  OR OLD.sort_name IS NOT NEW.sort_name
  OR OLD.height IS NOT NEW.height OR OLD.speed IS NOT NEW.speed OR OLD.length IS NOT NEW.length
  OR json_extract(OLD.summary, '$.products') IS NOT json_extract(NEW.summary, '$.products')
BEGIN
    DELETE FROM attraction_products WHERE id = OLD.id AND in_latest = OLD.in_latest;
    INSERT INTO attraction_products
        (id, in_latest, product, in_settled, variant, type, status, country, sort_name, height, speed, length)
    SELECT NEW.id, NEW.in_latest, p.product, NEW.in_settled, p.variant, NEW.type, NEW.status, NEW.country,
           NEW.sort_name, NEW.height, NEW.speed, NEW.length
    FROM (SELECT json_extract(value, '$.product.id') AS product, MAX(json_extract(value, '$.variant')) AS variant
          FROM json_each(NEW.summary, '$.products') WHERE json_type(value, '$.product.id') = 'integer'
          GROUP BY json_extract(value, '$.product.id')) p;
END;
