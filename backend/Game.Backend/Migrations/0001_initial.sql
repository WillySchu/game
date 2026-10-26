-- 0001: Milestone 1 schema. See "Raid Zero: Backend API & Data Model".
--
-- Portability notes (SQLite now, Postgres later):
--   * IDs are UUIDv7 strings minted in code (RaidZero.Shared.Ids.Uuid7).
--   * Timestamps are ISO-8601 UTC strings in TIMESTAMP columns; Postgres parses them as-is.
--   * Foreign keys are DEFERRABLE INITIALLY DEFERRED so a player and their two root containers,
--     which point at each other, can be inserted in one transaction in any order. Both databases
--     support deferred keys; the Postgres port adds the players -> item_instances keys with
--     ALTER TABLE, since Postgres won't reference a table that doesn't exist yet.
--   * ledger_entries.id is SQLite's auto-increment rowid; Postgres would use BIGSERIAL.
--   * Never edit this file after it has run anywhere. Add 0002_*.sql instead; the migrator
--     refuses to start if an applied migration's checksum changes.

CREATE TABLE players (
    id                TEXT PRIMARY KEY,
    display_name      TEXT NOT NULL UNIQUE,
    stash_root_id     TEXT NOT NULL REFERENCES item_instances (id) DEFERRABLE INITIALLY DEFERRED,
    character_root_id TEXT NOT NULL REFERENCES item_instances (id) DEFERRABLE INITIALLY DEFERRED,
    profile_version   INTEGER NOT NULL DEFAULT 0,
    created_at        TIMESTAMP NOT NULL
);

CREATE TABLE raids (
    id               TEXT PRIMARY KEY,
    map_id           TEXT NOT NULL,
    server_id        TEXT NOT NULL,
    state            TEXT NOT NULL CHECK (state IN ('pending', 'active', 'closed', 'aborted')),
    lease_expires_at TIMESTAMP NOT NULL,
    created_at       TIMESTAMP NOT NULL,
    started_at       TIMESTAMP,
    closed_at        TIMESTAMP
);

CREATE TABLE item_instances (
    id             TEXT PRIMARY KEY,
    def_id         TEXT NOT NULL,
    owner_id       TEXT REFERENCES players (id) DEFERRABLE INITIALLY DEFERRED,
    state          TEXT NOT NULL CHECK (state IN ('stash', 'in_raid', 'lost', 'consumed', 'sold')),
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

    -- Every item is in exactly one place (invariant 1).
    CHECK ((state = 'in_raid') = (raid_id IS NOT NULL)),
    CHECK (state <> 'in_raid' OR owner_id IS NULL),
    CHECK (state <> 'stash' OR owner_id IS NOT NULL),
    -- Grid coordinates come as a pair, and only for grid placement.
    CHECK ((grid_x IS NULL) = (grid_y IS NULL)),
    CHECK (grid_x IS NULL OR slot = 'grid')
);

CREATE INDEX ix_items_owner  ON item_instances (owner_id, state);
CREATE INDEX ix_items_parent ON item_instances (parent_id);
CREATE INDEX ix_items_raid   ON item_instances (raid_id) WHERE state = 'in_raid';

-- A named slot (primary, magazine, ...) holds at most one live item.
CREATE UNIQUE INDEX ux_items_named_slot ON item_instances (parent_id, slot)
    WHERE slot <> 'grid' AND state IN ('stash', 'in_raid');

CREATE TABLE raid_participants (
    raid_id           TEXT NOT NULL REFERENCES raids (id) DEFERRABLE INITIALLY DEFERRED,
    player_id         TEXT NOT NULL REFERENCES players (id) DEFERRABLE INITIALLY DEFERRED,
    squad_id          TEXT,
    state             TEXT NOT NULL CHECK (state IN ('ticketed', 'in_raid', 'extracted', 'died', 'refunded')),
    join_token_hash   TEXT NOT NULL,
    ticket_expires_at TIMESTAMP NOT NULL,
    loadout_snapshot  TEXT,
    joined_at         TIMESTAMP,
    resolved_at       TIMESTAMP,
    PRIMARY KEY (raid_id, player_id)
);

-- A player is in at most one raid at a time; this backs the already_in_raid error.
CREATE UNIQUE INDEX ux_participants_one_live_raid ON raid_participants (player_id)
    WHERE state IN ('ticketed', 'in_raid');

CREATE UNIQUE INDEX ux_participants_token ON raid_participants (join_token_hash);

-- Append-only. Kinds are deliberately unchecked so new ones need no migration.
CREATE TABLE ledger_entries (
    id         INTEGER PRIMARY KEY,
    player_id  TEXT REFERENCES players (id) DEFERRABLE INITIALLY DEFERRED,
    raid_id    TEXT REFERENCES raids (id) DEFERRABLE INITIALLY DEFERRED,
    kind       TEXT NOT NULL,
    detail     TEXT NOT NULL,
    created_at TIMESTAMP NOT NULL
);

CREATE INDEX ix_ledger_player ON ledger_entries (player_id, id);
CREATE INDEX ix_ledger_raid   ON ledger_entries (raid_id, id);

CREATE TABLE idempotency_keys (
    key          TEXT PRIMARY KEY,
    request_hash TEXT NOT NULL,
    response     TEXT NOT NULL,
    created_at   TIMESTAMP NOT NULL
);
