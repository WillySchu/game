# Raid Zero: Backend API & Data Model

Oct 26, 2026 · @user

## Overview

The backend owns everything that persists between raids; the game server owns everything inside a raid; the client owns nothing. Milestone 1 runs one player per raid on one map, but every table and endpoint below already supports many players per raid, so co-op and PvPvE need no schema rewrite.

**Scope for Milestone 1 (Raid Zero):** dev login, stash and equipment management, raid tickets, loadout checkout, extraction and death reports, raid close. Out of scope: real accounts, matchmaking, traders, crafting, insurance, wipes.

**Design principles**

- **Every item is an instance with a UUID.** Weapons, armor, ammo stacks and currency all live in one `item_instances` table. One code path handles loot, extraction and loss.
- **Every instance is in exactly one place.** In a stash, in a raid, or gone (lost, consumed, sold). No operation can make an item exist in two places.
- **Items in a raid belong to the raid, not a player.** Checkout moves gear into the raid's pool; extraction claims items out of it; whatever is unclaimed when the raid closes is lost. This is what makes picking up another player's gear work later.
- **Raid results are transactions.** Each report from the game server is idempotent and either commits fully or not at all.
- **Optimistic concurrency on profiles.** Stash edits carry the profile version they were based on; stale edits are rejected, never merged.
- **Start on SQLite, stay Postgres-compatible.** Plain SQL types, no SQLite-only features, so moving to Postgres is a config change.

## Raid lifecycle

Gear moves into the raid pool only at step 3, after the player has actually connected, and only steps 5 and 6 decide what comes out.

&#91;embedded content: raid lifecycle · client, game server, backend\]

A ticket that is never redeemed expires without touching items. If the game server crashes between steps 3 and 6, the lease runs out and the backend refunds everyone still in the raid (see Game server API).

## Data model

Six tables carry Milestone 1; item definitions live in a JSON catalog exported from Unity, not in the database.

| Table | Holds | Notes |
| --- | --- | --- |
| `players` | One row per account | Points at two root containers: stash and character |
| `item_instances` | Every item that exists or existed | Items nest via `parent_id` (backpack → contents, rifle → magazine → ammo) |
| `raids` | One row per raid session | Has a lease the game server must keep renewing |
| `raid_participants` | One row per player per raid | Stores the loadout snapshot taken at checkout |
| `ledger_entries` | Append-only audit of every item change | Your best friend when chasing a dupe bug |
| `idempotency_keys` | Responses to already-processed server reports | Lets the game server retry safely |

### Item catalog (not a table)

Item definitions are ScriptableObjects in Unity. An editor script exports them to `item_catalog.json`, committed to the repo; the backend loads it at startup. Each definition has a stable string `def_id` (for example `rifle_ak_basic`, `ammo_545_ps`, `currency_credits`), grid size, max stack, which slots it fits, and an optional container grid. The catalog carries a `catalog_version`; the game server sends its version on every call, and the backend rejects mismatches so a stale build can't commit items it doesn't understand.

### Item location model

Every player has two root container instances, created at signup: a **stash** (big grid) and a **character** (named equipment slots such as `primary`, `secondary`, `armor`, `rig`, `backpack`). An item's position is `parent_id` + `slot` + optional grid `x`, `y`, `rotated`. Equipping is just moving an item under the character root. Checkout takes the character root's whole subtree into the raid.

Currency is a stackable item (`currency_credits`). That keeps money lootable and losable through the same path as everything else; revisit if traders make a separate balance column simpler.

### Schema

```sql
CREATE TABLE players (
  id               TEXT PRIMARY KEY,          -- UUIDv7
  display_name     TEXT NOT NULL,
  stash_root_id    TEXT NOT NULL,             -- item_instances.id
  character_root_id TEXT NOT NULL,            -- item_instances.id
  profile_version  INTEGER NOT NULL DEFAULT 0, -- bumps on every committed change
  created_at       TIMESTAMP NOT NULL
);

CREATE TABLE item_instances (
  id            TEXT PRIMARY KEY,             -- UUIDv7; minted by backend OR game server (raid loot)
  def_id        TEXT NOT NULL,                -- key into item_catalog.json
  owner_id      TEXT,                         -- players.id; NULL while in a raid pool
  state         TEXT NOT NULL,                -- 'stash' | 'in_raid' | 'lost' | 'consumed' | 'sold'
  raid_id       TEXT,                         -- set while state = 'in_raid'
  parent_id     TEXT,                         -- container/weapon this sits in; NULL for roots
  slot          TEXT,                         -- 'grid', 'primary', 'magazine', ...
  grid_x        INTEGER,
  grid_y        INTEGER,
  rotated       BOOLEAN NOT NULL DEFAULT FALSE,
  quantity      INTEGER NOT NULL DEFAULT 1,   -- stacks only
  durability    REAL,                         -- NULL if the def has none
  attrs         TEXT,                         -- JSON for rarely-used extras
  origin        TEXT NOT NULL,                -- 'starter' | 'raid_loot' | 'purchase' | 'craft' | 'admin'
  origin_raid_id TEXT,
  created_at    TIMESTAMP NOT NULL,
  updated_at    TIMESTAMP NOT NULL
);
CREATE INDEX ix_items_owner  ON item_instances(owner_id, state);
CREATE INDEX ix_items_parent ON item_instances(parent_id);
CREATE INDEX ix_items_raid   ON item_instances(raid_id) WHERE state = 'in_raid';

CREATE TABLE raids (
  id               TEXT PRIMARY KEY,
  map_id           TEXT NOT NULL,
  server_id        TEXT NOT NULL,
  state            TEXT NOT NULL,             -- 'pending' | 'active' | 'closed' | 'aborted'
  lease_expires_at TIMESTAMP NOT NULL,
  created_at       TIMESTAMP NOT NULL,
  started_at       TIMESTAMP,
  closed_at        TIMESTAMP
);

CREATE TABLE raid_participants (
  raid_id          TEXT NOT NULL,
  player_id        TEXT NOT NULL,
  squad_id         TEXT,                      -- unused in M1; co-op later
  state            TEXT NOT NULL,             -- 'ticketed' | 'in_raid' | 'extracted' | 'died' | 'refunded'
  join_token_hash  TEXT NOT NULL,
  ticket_expires_at TIMESTAMP NOT NULL,
  loadout_snapshot TEXT,                      -- JSON tree at checkout; used for refunds
  joined_at        TIMESTAMP,
  resolved_at      TIMESTAMP,
  PRIMARY KEY (raid_id, player_id)
);

CREATE TABLE ledger_entries (
  id          INTEGER PRIMARY KEY,            -- autoincrement / BIGSERIAL
  player_id   TEXT,
  raid_id     TEXT,
  kind        TEXT NOT NULL,                  -- 'checkout' | 'extract' | 'death' | 'raid_close' | 'refund' | 'move' | 'grant'
  detail      TEXT NOT NULL,                  -- JSON: item ids and before/after states
  created_at  TIMESTAMP NOT NULL
);

CREATE TABLE idempotency_keys (
  key           TEXT PRIMARY KEY,
  request_hash  TEXT NOT NULL,                -- same key + different body = 409
  response      TEXT NOT NULL,
  created_at    TIMESTAMP NOT NULL
);
```

## Client API

The client can read its profile, rearrange its own stash and equipment, and ask for a raid ticket. It can never create, destroy or move items in or out of a raid. All calls use `Authorization: Bearer <player token>`.

| Method | Path | Purpose |
| --- | --- | --- |
| POST | `/v1/auth/dev-login` | M1 only: name in, player token out. Replace with Steam auth later |
| GET | `/v1/me/profile` | Player, stash tree, character tree, `profile_version` |
| POST | `/v1/me/inventory/ops` | Move, split, merge or discard items in the stash and character |
| POST | `/v1/me/raid-tickets` | Reserve a raid slot; returns server address and join token |
| GET | `/v1/me/raids/{raid_id}` | Raid result: outcome, items gained, items lost |

### Inventory ops

Ops are applied in order inside one transaction. If any op fails validation (item not owned, not in `stash` state, doesn't fit, wrong slot), nothing is applied.

```json
POST /v1/me/inventory/ops
{
  "expected_version": 41,
  "ops": [
    { "op": "move",  "item_id": "0192...a1", "to": { "parent_id": "<character_root>", "slot": "primary" } },
    { "op": "split", "item_id": "0192...b7", "quantity": 30, "to": { "parent_id": "0192...c3", "slot": "grid", "x": 0, "y": 1, "rotated": false } },
    { "op": "merge", "item_id": "0192...d4", "into_id": "0192...b7" },
    { "op": "discard", "item_id": "0192...e9" }
  ]
}

200 { "profile_version": 42, "created": [{ "op_index": 1, "item_id": "0192...f0" }] }
409 { "error": "version_conflict", "profile_version": 43 }   // client refetches profile and retries
422 { "error": "invalid_op", "op_index": 0, "reason": "slot_mismatch" }
```

### Raid ticket

A ticket reserves a place but does **not** check out gear; checkout happens when the game server confirms the player actually connected. A ticket that isn't used within 60 seconds expires and nothing changes.

```json
POST /v1/me/raid-tickets
{ "map_id": "greybox_01" }

200 {
  "raid_id": "0192...r1",
  "server": { "host": "127.0.0.1", "port": 7770 },
  "join_token": "opaque-random-string",
  "expires_at": "2026-10-26T20:15:00Z"
}
409 { "error": "already_in_raid", "raid_id": "0192...r0" }
422 { "error": "loadout_invalid", "reason": "empty_primary" }   // optional M1 rule
```

In M1 the backend picks the one registered dev server and creates the raid row itself. Later a real matchmaker and server allocator sit behind this same endpoint, so the client never changes.

## Game server API

Only the game server can check gear into a raid and report what came out. All calls use `Authorization: Bearer <server token>` and send `X-Catalog-Version`. Every call that changes items also sends an `Idempotency-Key` header.

| Method | Path | Purpose |
| --- | --- | --- |
| POST | `/v1/server/raids/{raid_id}/join` | Redeem a player's join token; checks out their loadout and returns it |
| POST | `/v1/server/raids/{raid_id}/heartbeat` | Extend the raid lease (every 10 s; lease is 60 s) |
| POST | `/v1/server/raids/{raid_id}/outcomes` | Report one player extracted or died |
| POST | `/v1/server/raids/{raid_id}/close` | End the raid; unclaimed items become lost |

### Join (checkout)

In one transaction the backend verifies the token, marks the participant `in_raid`, saves a loadout snapshot, and moves the character root's subtree to `state = 'in_raid'`, `owner_id = NULL`, `raid_id = <raid>`. The first successful join flips the raid from `pending` to `active`.

```json
POST /v1/server/raids/0192...r1/join
Idempotency-Key: join-0192...r1-<token-hash>
{ "join_token": "opaque-random-string" }

200 {
  "player_id": "0192...p1",
  "display_name": "Sam",
  "loadout": {
    "id": "<character_root>", "def_id": "character", "children": [
      { "id": "0192...a1", "def_id": "rifle_ak_basic", "slot": "primary", "durability": 0.82,
        "children": [ { "id": "0192...m1", "def_id": "mag_ak_30", "slot": "magazine",
          "children": [ { "id": "0192...b7", "def_id": "ammo_545_ps", "slot": "rounds", "quantity": 30 } ] } ] },
      { "id": "0192...k2", "def_id": "backpack_small", "slot": "backpack", "children": [] }
    ]
  }
}
410 { "error": "ticket_expired" }
```

### Outcomes

An **extracted** report carries the player's full inventory tree as it stands at the extraction point. Items with known IDs must currently be in this raid's pool; items with unknown IDs are new raid loot minted by the server. The backend commits the whole tree back under the player's character root and puts any overflow in the stash.

A **died** report carries no items: the player's gear stays in the raid pool, where another player can still extract it. Whatever nobody claims becomes lost at close.

```json
POST /v1/server/raids/0192...r1/outcomes
Idempotency-Key: outcome-0192...r1-0192...p1
{
  "player_id": "0192...p1",
  "outcome": "extracted",            // or "died"
  "extract_point": "north_gate",
  "inventory": { "id": "<character_root>", "def_id": "character", "children": [ "...same shape as join..." ] },
  "stats": { "kills_ai": 3, "duration_s": 1240 }
}

200 { "committed": true, "items_gained": 7, "items_returned": 4 }
409 { "error": "item_not_in_raid_pool", "item_id": "0192...zz" }   // a bug; server logs and alerts
```

### Close and lease expiry

A normal **close** marks every item still in the raid pool as `lost` and every participant still `in_raid` as `died`.

If the lease expires because the server crashed, the backend **aborts** the raid instead: every participant still `in_raid` is refunded from their loadout snapshot, and raid loot nobody extracted disappears. Players who already extracted keep what they got. This is generous on purpose; a crash shouldn't cost players their gear.

## Integrity rules

These are the invariants that keep items from duplicating or vanishing. Write a test for each one before the game depends on it.

### Invariants

1. **One place per item.** Every `item_instances` row is in exactly one state. Every state change happens inside a database transaction and writes a `ledger_entries` row in that same transaction.
2. **One claim per item per raid.** An item in a raid pool can be claimed by at most one extraction. The commit does `UPDATE ... WHERE id = ? AND state = 'in_raid' AND raid_id = ?` and checks the affected row count; zero rows means someone else got it, so the whole report fails.
3. **One outcome per player per raid.** A participant moves from `in_raid` to `extracted` or `died` exactly once. The idempotency key is derived from raid ID and player ID, so a retried report returns the stored response instead of committing twice.
4. **Same key, same body.** A reused `Idempotency-Key` with a different request hash returns `409`. That catches server bugs that would otherwise silently drop data.
5. **New items only from raids.** Unknown item IDs are accepted only in an extraction report, only for a raid that is `active`, and are stored with `origin = 'raid_loot'` and `origin_raid_id`. Clients can never create items.
6. **Refunds never clone.** An abort refunds only snapshot items that are still in the raid pool. Anything another player already extracted stays with them.
7. **Catalog validation on every commit.** `def_id` exists, `quantity` is within max stack, children fit their parent's grid or slot. In a trusted server these catch bugs rather than cheats, which makes them more valuable, not less.

### Auth model

| Caller | Credential | Can do |
| --- | --- | --- |
| Client | Player token (dev login now, Steam session ticket later) | Read own profile, edit own stash and character, request tickets |
| Game server | Server token, one per deployed server | Join, heartbeat, outcomes, close for raids assigned to it |
| Admin tooling | Separate admin token | Grants, item inspection, manual refunds |

A raid only accepts server calls from the server it was assigned to.

**Host mode warning.** In M1 you'll run raids in Fish-Net host mode on your own machines, which means the server token lives on a player's PC. That's fine for development. For any public build, only dedicated servers you control may hold server tokens; if you ever allow player-hosted raids, treat them as unranked and keep their loot out of the shared economy.

## Unity and Fish-Net integration

The raid inventory is plain C# on the server; Fish-Net only carries requests in and views out. Keeping networking out of the inventory logic means you can unit-test it without starting a server.

### Item definitions

- `ItemDefinition : ScriptableObject` with `defId` (stable string, never renamed), `gridSize`, `maxStack`, `allowedSlots`, optional `containerGrid`, `hasDurability`.
- An editor menu item exports all definitions to `item_catalog.json` with a content hash as `catalog_version`. CI fails if the committed catalog is out of date.
- Clients and server load definitions by `defId`; the backend loads the same JSON.

### Server-side raid inventory

- `RaidInventoryService` (plain C#) owns every player's tree for the raid: `TryMove`, `TrySplit`, `TryMerge`, `TryPickUp`, `TryDrop`, `TryLootFrom(containerId)`. Each returns a result code, never throws for gameplay reasons.
- World loot containers and dropped items are also trees in this service, so a body, a crate and a backpack on the ground all work the same way.
- New loot gets a UUIDv7 minted on the server when it spawns, so IDs are stable across pickup, drop and re-pickup.

### Networking the inventory

- A `PlayerInventoryNetwork : NetworkBehaviour` on the player object exposes `[ServerRpc]` methods such as `RequestMove(itemId, target)`. The server validates through `RaidInventoryService` and answers.
- The owner receives their full inventory view through a `[TargetRpc]` (full snapshot on join, then diffs). Other clients only see what's visible: equipped weapon, armor, backpack model.
- Never use a `SyncVar` or `SyncList` as the source of truth for items. They are views of the service's state.

### Joining a raid

- A custom Fish-Net `Authenticator` sends the client's `join_token` on connect. The server calls `/join`; on success it authenticates the connection and spawns the character from the returned loadout. On failure it kicks the connection with a reason.

### Talking to the backend

- All backend calls are async and server-only, through one `BackendClient` class (`HttpClient` or `UnityWebRequest`).
- Outcome and close reports go through a **durable outbox**: write the report to disk with its idempotency key, then send with retries and backoff, then delete on `200`. If the server crashes after a player extracts, it resends on restart before the lease runs out.
- Heartbeats run on a timer independent of gameplay. If three in a row fail, show a warning in server logs; the backend will abort at lease expiry regardless.

## Future-proofing and open questions

Each later feature lands as new rows, columns or ledger kinds, not a redesign.

| Feature | What it adds |
| --- | --- |
| Co-op squads | Fill `raid_participants.squad_id`; tickets request a squad. Nothing else changes |
| PvPvE | Already works: a dead player's gear stays in the raid pool and another player's extraction claims it |
| Player-like bots | Bots get loadouts from the server, not the backend; their gear joins the pool as raid loot when they die |
| Insurance | An `insured_by` column; at close, unclaimed insured items return to their owner after a delay instead of becoming lost |
| Traders and crafting | New ledger kinds (`purchase`, `sell`, `craft`) and client endpoints, using the same transaction pattern |
| Wipes | A `season_id` on players and items, or archive and reset the tables |
| Real auth | Swap dev login for Steam session tickets; the player token shape stays the same |
| Multiple servers | A matchmaker and allocator behind `/v1/me/raid-tickets`; servers register with their own tokens |
| Scale | Move SQLite to Postgres when you have concurrent servers; the schema is already compatible |

### Open questions

- [ ] Disconnects: does a disconnected player's character stay in the world (vulnerable) and allow reconnect, or count as death after a timeout?
- [ ] Should players who died get anything back when a crashed raid is aborted, or only those still alive?
- [ ] Grid inventory (Tarkov-style Tetris) or slot-based? The schema supports both; grid costs more UI work.
- [ ] Currency as an item, or a separate balance once traders exist?
- [ ] Backend language and framework: pick whatever the two of you ship fastest in.

