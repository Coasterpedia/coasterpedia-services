-- A park's resort.parks on /v1/parks/{id} (DR-010 §12.20): the resort's
-- members in the wrapper template's own order, read through the resort index
-- (0006) and sorted on this column. No index of its own: a resort holds two
-- to six parks, so the sort is over those rows.
--
-- Additive: null until the services image sends filters.resort_order, and
-- the Worker sorts nulls after the ordered rows, then by name. Only the 88
-- parks in a resort change hash, so only they re-push.
ALTER TABLE parks ADD COLUMN resort_order INTEGER;
