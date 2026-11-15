-- 0008: one login per player. session_id is the player's current login: each login gives them a new one,
-- and player tokens carry it, so a token from an earlier login (somewhere else) stops working. Null until the
-- player first logs in after this migration.

ALTER TABLE players ADD COLUMN session_id TEXT;
