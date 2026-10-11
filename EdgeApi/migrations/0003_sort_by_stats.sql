-- Sorting by the headline stats (DR-010 §12, slice 1). Metric values, sent by
-- the Projector as sort_values. Additive: the columns start null and fill as
-- the next sweep re-pushes every record with its stats.
ALTER TABLE attractions ADD COLUMN height REAL;
ALTER TABLE attractions ADD COLUMN speed  REAL;
ALTER TABLE attractions ADD COLUMN length REAL;

-- A stat sort lists only attractions that record the stat, so each index
-- leaves the nulls out: a row without the stat costs no index write, and the
-- Worker writes both conditions into the SQL so SQLite can match them.
CREATE INDEX attractions_latest_by_height  ON attractions (height, id) WHERE in_latest = 1 AND height IS NOT NULL;
CREATE INDEX attractions_latest_by_speed   ON attractions (speed, id)  WHERE in_latest = 1 AND speed IS NOT NULL;
CREATE INDEX attractions_latest_by_length  ON attractions (length, id) WHERE in_latest = 1 AND length IS NOT NULL;
CREATE INDEX attractions_settled_by_height ON attractions (height, id) WHERE in_settled = 1 AND height IS NOT NULL;
CREATE INDEX attractions_settled_by_speed  ON attractions (speed, id)  WHERE in_settled = 1 AND speed IS NOT NULL;
CREATE INDEX attractions_settled_by_length ON attractions (length, id) WHERE in_settled = 1 AND length IS NOT NULL;
