-- 0004: adds the 'expired' participant state, for tickets nobody redeemed in time, and an index the
-- lease-expiry job uses to find raids whose game server went quiet.
--
-- Nothing references raid_participants, so this rebuild needs no foreign-keys-off directive.

CREATE TABLE raid_participants_new (
    raid_id           TEXT NOT NULL REFERENCES raids (id) DEFERRABLE INITIALLY DEFERRED,
    player_id         TEXT NOT NULL REFERENCES players (id) DEFERRABLE INITIALLY DEFERRED,
    squad_id          TEXT,
    state             TEXT NOT NULL CHECK (state IN ('ticketed', 'in_raid', 'extracted', 'died', 'refunded', 'expired')),
    join_token_hash   TEXT NOT NULL,
    ticket_expires_at TIMESTAMP NOT NULL,
    loadout_snapshot  TEXT,
    joined_at         TIMESTAMP,
    resolved_at       TIMESTAMP,
    PRIMARY KEY (raid_id, player_id)
);

INSERT INTO raid_participants_new (
    raid_id, player_id, squad_id, state, join_token_hash, ticket_expires_at, loadout_snapshot, joined_at, resolved_at)
SELECT
    raid_id, player_id, squad_id, state, join_token_hash, ticket_expires_at, loadout_snapshot, joined_at, resolved_at
FROM raid_participants;

DROP TABLE raid_participants;

ALTER TABLE raid_participants_new RENAME TO raid_participants;

CREATE UNIQUE INDEX ux_participants_one_live_raid ON raid_participants (player_id)
    WHERE state IN ('ticketed', 'in_raid');

CREATE UNIQUE INDEX ux_participants_token ON raid_participants (join_token_hash);

CREATE INDEX ix_raids_lease ON raids (state, lease_expires_at);
