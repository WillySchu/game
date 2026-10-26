-- migrator: foreign_keys=off
--
-- 0002: adds the 'discarded' item state, for items a player threw away. Keeping it apart from 'lost'
-- lets analytics tell "threw it away" from "died with it".
--
-- SQLite can't alter a CHECK constraint, so this rebuilds item_instances the documented way: create the
-- new table, copy the rows, drop the old one, rename the new one, and recreate its indexes. Foreign keys
-- are off while it runs (the directive on the first line), and the migrator runs a foreign key check
-- before committing, so the rebuild can't leave a dangling reference behind.
--
-- Everything except the state CHECK is identical to 0001.

CREATE TABLE item_instances_new (
    id             TEXT PRIMARY KEY,
    def_id         TEXT NOT NULL,
    owner_id       TEXT REFERENCES players (id) DEFERRABLE INITIALLY DEFERRED,
    state          TEXT NOT NULL CHECK (state IN ('stash', 'in_raid', 'lost', 'consumed', 'sold', 'discarded')),
    raid_id        TEXT REFERENCES raids (id) DEFERRABLE INITIALLY DEFERRED,
    parent_id      TEXT REFERENCES item_instances (id) DEFERRABLE INITIALLY DEFERRED,
    slot           TEXT,
    grid_x         INTEGER,
    grid_y         INTEGER,
    rotated        BOOLEAN NOT NULL DEFAULT FALSE,
    quantity       INTEGER NOT NULL DEFAULT 1 CHECK (quantity >= 1),
    durability     REAL CHECK (durability IS NULL OR (durability >= 0 AND durability <= 1)),
    attrs          TEXT,
    origin         TEXT NOT NULL CHECK (origin IN ('starter', 'raid_loot', 'purchase', 'craft', 'admin')),
    origin_raid_id TEXT REFERENCES raids (id) DEFERRABLE INITIALLY DEFERRED,
    created_at     TIMESTAMP NOT NULL,
    updated_at     TIMESTAMP NOT NULL,

    CHECK ((state = 'in_raid') = (raid_id IS NOT NULL)),
    CHECK (state <> 'in_raid' OR owner_id IS NULL),
    CHECK (state <> 'stash' OR owner_id IS NOT NULL),
    CHECK ((grid_x IS NULL) = (grid_y IS NULL)),
    CHECK (grid_x IS NULL OR slot = 'grid')
);

INSERT INTO item_instances_new (
    id, def_id, owner_id, state, raid_id, parent_id, slot, grid_x, grid_y, rotated,
    quantity, durability, attrs, origin, origin_raid_id, created_at, updated_at)
SELECT
    id, def_id, owner_id, state, raid_id, parent_id, slot, grid_x, grid_y, rotated,
    quantity, durability, attrs, origin, origin_raid_id, created_at, updated_at
FROM item_instances;

DROP TABLE item_instances;

ALTER TABLE item_instances_new RENAME TO item_instances;

CREATE INDEX ix_items_owner  ON item_instances (owner_id, state);
CREATE INDEX ix_items_parent ON item_instances (parent_id);
CREATE INDEX ix_items_raid   ON item_instances (raid_id) WHERE state = 'in_raid';

CREATE UNIQUE INDEX ux_items_named_slot ON item_instances (parent_id, slot)
    WHERE slot <> 'grid' AND state IN ('stash', 'in_raid');
