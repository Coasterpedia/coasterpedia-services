-- Bounded reads for two attraction paths that weren't (DR-010 §12.12),
-- measured on the live data 2026-10-12:
--
-- * A stat sort under a type filter read every row of the type and sorted it:
--   type=ride&sort=-height 15,285 rows for a page of 100. Type first, then the
--   stat, so the page is a seek and a walk in order. The type-filtered
--   histograms read the same index and stop at the type's rows with the stat.
-- * A status filter walked the whole name index: status=planned 21,072 rows
--   for 6. Status first, then name.
--
-- Additive: indexes only. The Worker's SQL already carries the literals each
-- partial index needs (the view flag, the stat's IS NOT NULL).
CREATE INDEX attractions_latest_by_type_height   ON attractions (type, height, id) WHERE in_latest = 1 AND height IS NOT NULL;
CREATE INDEX attractions_latest_by_type_speed    ON attractions (type, speed, id)  WHERE in_latest = 1 AND speed IS NOT NULL;
CREATE INDEX attractions_latest_by_type_length   ON attractions (type, length, id) WHERE in_latest = 1 AND length IS NOT NULL;
CREATE INDEX attractions_settled_by_type_height  ON attractions (type, height, id) WHERE in_settled = 1 AND height IS NOT NULL;
CREATE INDEX attractions_settled_by_type_speed   ON attractions (type, speed, id)  WHERE in_settled = 1 AND speed IS NOT NULL;
CREATE INDEX attractions_settled_by_type_length  ON attractions (type, length, id) WHERE in_settled = 1 AND length IS NOT NULL;

CREATE INDEX attractions_latest_by_status  ON attractions (status, sort_name, id) WHERE in_latest = 1;
CREATE INDEX attractions_settled_by_status ON attractions (status, sort_name, id) WHERE in_settled = 1;
