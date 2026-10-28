# Raid Zero

An extraction shooter: Unity client and game server (Fish-Net), plus a C# backend that owns everything persistent.
Design docs: *Raid Zero: Backend API & Data Model* and *Raid Zero: Unity Client & Game Server Plan*.

## Layout

```
shared/Game.Shared/        Code compiled by BOTH Unity and .NET: item tree, catalog, validation, UUIDv7, raid protocol
shared/Game.BackendClient/ The game server's connection to the backend: join, heartbeats, durable outbox
shared/Game.Raid/          The game server's raid rules: the raid's inventory, looting, drops, bodies, and
                           when a raid starts and ends
shared/catalog/            item_catalog.json, exported from Unity's ItemDefinition assets
backend/Game.Backend/      ASP.NET Core backend: SQLite + Dapper, migrations, catalog loading
tests/Game.Shared.Tests/   Rules tests (fit, stacking, slots, IDs, catalog hashing)
tests/Game.Backend.Tests/  Catalog loading, migrations and database behaviour against a real SQLite file
tests/Game.BackendClient.Tests/  The game server client against a fake backend, and whole raids against the real one
tests/Game.Raid.Tests/     Raid inventory rules, plus a raid with looting played through to the backend's stashes
unity/RaidZero/            The Unity project; unity/TESTING-INSTRUCTIONS.md covers playing it against the backend
                           Everything we make lives in Assets/_Project; the rest of Assets is third-party or tooling
unity/server.Dockerfile    The dedicated game server's image, around a Unity Linux Dedicated Server build
deploy/                    Running the backend and a game server on a VPS: Compose, Caddy, backups (see deploy/README.md)
.github/workflows/ci.yml   Tests every push and pull request; pushes the backend image to GHCR from main
.github/workflows/server.yml  Builds the game server with Unity, smoke tests it, pushes its image to GHCR from main
```

Build output goes to `artifacts/` at the repo root, never next to the projects (see `Directory.Build.props`).

## Getting started

Requires the .NET 10 SDK.

```sh
dotnet test                                   # everything should pass before you change anything
dotnet run --project backend/Game.Backend     # http://localhost:5080, Development mode
curl http://localhost:5080/healthz            # {"status":"ok","catalog_version":"...","schema_version":4}

# Log in (creates the player with the starter kit the first time), then read the profile
TOKEN=$(curl -s -X POST localhost:5080/v1/auth/dev-login -H 'Content-Type: application/json' \
          -d '{"display_name":"Sam"}' | jq -r .token)
curl -s localhost:5080/v1/me/profile -H "Authorization: Bearer $TOKEN" | jq

# Rearrange the stash: send the profile_version your view is based on, plus a batch of ops
curl -s -X POST localhost:5080/v1/me/inventory/ops -H "Authorization: Bearer $TOKEN" \
     -H 'Content-Type: application/json' \
     -d '{"expected_version":0,"ops":[{"op":"move","item_id":"<id>","to":{"parent_id":"<stash id>","slot":"grid","x":9,"y":29}}]}'
```

`POST /v1/me/inventory/ops` answers 200 with the new `profile_version` (and the IDs of any stacks a split
created), 409 `version_conflict` with the current version when the client's view is stale (refetch the
profile, then retry), or 422 `invalid_op` with the failing `op_index` and a `reason` such as `slot_occupied`.
Nothing is written unless the whole batch succeeds.

The profile also carries `active_raid_id`, set while the player holds a ticket or is in a raid (their stash and
gear are locked until it clears), and `last_raid_id`, the most recent raid they joined, so the Hideout can show
"in raid" or the last raid's result after a restart.

Open http://localhost:5080/admin for the admin tool and paste the `AdminToken` from
`appsettings.Development.json`. It looks up players (their gear and full ledger), traces any item's history,
grants items, lists and aborts live raids (refunding everyone inside), and takes database backups.

On first start the backend creates `backend/Game.Backend/App_Data/raidzero.db`, switches it to WAL mode and
applies the migrations. Delete the `App_Data/` folder to start over.

## Configuration

Settings live under `Backend` in `appsettings.json`, with Development overrides in `appsettings.Development.json`.

| Setting | Default | Notes |
| --- | --- | --- |
| `DatabasePath` | `App_Data/raidzero.db` | Relative to the project folder; git ignores `App_Data/` |
| `CatalogPath` | `../../shared/catalog/item_catalog.json` | |
| `StarterKitPath` | `starter_kit.json` | Validated against the catalog at startup |
| `DevLoginEnabled` | `false` (`true` in Development) | When off, `/v1/auth/dev-login` doesn't exist |
| `TokenSigningKey` | none (a dev-only key in Development) | Base64, 32+ bytes: `openssl rand -base64 32`. Required |
| `TokenLifetimeHours` | `72` | Tokens can't be revoked early, short of rotating the key |
| `AdminToken` | none (a dev-only token in Development) | 32+ chars. Turns on `/admin` and `/v1/admin`; unset means they don't exist |
| `BackupsToKeep` | `14` | Backups older than the newest N are deleted from `App_Data/backups` |
| `Servers` | none (`dev-local` in Development) | Game servers: `Id`, `Token` (32+ chars), and the `Host`/`Port` clients connect to |
| `Raids:TicketSeconds` | `60` | How long a player has to connect after getting a ticket |
| `Raids:LeaseSeconds` | `60` | A raid whose server sends no heartbeat for this long is aborted and refunded |
| `Raids:MaxPlayersPerRaid` | `4` | |
| `Raids:JoinWindowSeconds` | `120` | How long after its first player joins a raid still takes new tickets |
| `Raids:ReaperIntervalSeconds` | `10` | How often the abort job runs |

The key in `appsettings.Development.json` is for local runs only. Real deployments set
`Backend__TokenSigningKey` as an environment variable.

## Rules of the road

**Database**
- Item and profile changes go through `Database.WriteAsync`. It starts with `BEGIN IMMEDIATE` and commits only if
  your code returns normally.
- Inside a write, pass `transaction: tx` to every Dapper call. Microsoft.Data.Sqlite throws if you forget.
- Claims use conditional updates plus a row-count check, never read-then-write. See
  `DatabaseTests.ConditionalClaim_SucceedsOnlyOnce`.

**Migrations**
- Add `backend/Game.Backend/Migrations/000N_description.sql`. They're embedded in the assembly and run at startup.
- Never edit a migration that has run anywhere. The backend refuses to start if an applied migration's checksum
  changes. Line endings don't count, so a Windows checkout is fine.
- SQLite can't alter a CHECK constraint or a column type, so those changes rebuild the table. Start such a
  migration with `-- migrator: foreign_keys=off`; it then runs without foreign key enforcement and commits only if
  `PRAGMA foreign_key_check` finds nothing dangling. Copy `0002_discarded_state.sql` as the pattern.
- Item states live in `ItemStates`; a test fails if they drift from the database's CHECK constraint.

**Item catalog**
- Items are defined in Unity: one `ItemDefinitionAsset` per item in `unity/RaidZero/Assets/_Project/Data/Items`,
  holding the rules (size, stacking, tags, slots, grid) and the presentation (display name, description, icon,
  world prefab).
  Create one with Create > Raid Zero > Item Definition.
- Saving an item rewrites `shared/catalog/item_catalog.json` by itself, and refreshes the `ItemLibrary` that game
  code uses to find an item's name, icon and model (`ItemLibrary.Instance.Find(defId)`). The export refuses an
  invalid catalog and changes nothing; removing an item that's in the current file needs confirming through
  Raid Zero > Items > Export Catalog, since stashes holding one would stop loading.
- Don't edit the JSON by hand; a test fails if it isn't in the exporter's format. If someone did, run
  Raid Zero > Items > Import Catalog to bring the change into the assets.
- `catalog_version` is a hash of every rule-relevant field (sizes, stacks, tags, slots, grids). Display names are
  excluded, so renaming an item needs no redeploy. A rule change means deploying the backend and game servers from
  the same commit: the backend refuses servers built from another catalog.
- The backend and game servers read the JSON, never the assets, so all three always agree on the rules.
- `def_id`s are permanent once an item has existed in anyone's stash.

**Inventory operations**
- `InventoryTree` applies move, split, merge and discard in all-or-nothing batches and returns a `ChangeSet`
  (created, updated, removed with a reason). Its roots are its scope: the backend gives it one player's stash and
  character, the game server gives it a whole raid, so pickups and looting are moves between roots.
- A successful batch replaces the node objects. Keep item IDs, not `ItemNode` references.

**Players and auth**
- Endpoints that need a player sit under the `/v1/me` group, which requires a token; read the caller with
  `user.GetPlayerId()`. Dev login only issues tokens, so Steam login later replaces that one endpoint.
- New players get the starter kit (`starter_kit.json`) in the same transaction that creates them, with one
  `grant` ledger entry listing every item. Grid items in the kit may leave out x and y to take the next free spot.
- `ItemRows` converts between `item_instances` rows and item trees.
- Stash edits (`StashInventory`) run in one transaction: version check, `InventoryTree.Apply`, write the change
  list, bump `profile_version`, one `inventory` ledger entry. Moved items are lifted out of their old positions
  before any final position is written, because the one-item-per-slot index is checked after every statement.
  Keep that two-phase write if you touch this code; `SwappingMagazines_SavesWhateverOrderTheChangesComeIn` fails
  without it.

**Raids**
- Flow: `POST /v1/me/raid-tickets` → the game server calls `/v1/server/raids/{id}/join` when the player connects
  (their gear moves into the raid's pool) → `heartbeat` every 10 s → `outcomes` per player (extracted or died)
  → `close`. Players read the result at `GET /v1/me/raids/{id}`.
- Game server calls authenticate with the server's token and must send `X-Catalog-Version`. Calls that change
  things also need an `Idempotency-Key`: a retry with the same key replays the stored answer, and reusing a key
  for a different request is a 409. Write reports to a durable outbox and retry them with their original key.
- Player tokens don't work on `/v1/server`, and server tokens don't work on `/v1/me`.
- A player's stash is locked (409 `in_raid`) from ticket until their raid resolves.
- The character container never leaves home; checkout moves only its contents and unhooks the top-level ones,
  so gear lost in a raid can't block re-equipping.
- Extractions claim known IDs out of the pool with conditional updates and insert unknown IDs as raid loot.
  Like stash edits, they unhook old positions before writing new ones (`Extract_AfterSwappingMagazinesInTheRaid`).
- If a server stops heartbeating, `RaidReaper` aborts the raid: players still inside get their snapshot back,
  minus anything another player already extracted; extracted players keep their loot.
- Tickets join the server's open raid when the map matches and there's room, so co-op and looting other players
  work on one dev server. Each server runs one raid at a time, and a raid stops taking tickets two minutes
  after its first player joins, so nobody drops into a raid that's nearly over.

**Admin tool**
- `/v1/admin` takes only the admin token; player and server tokens don't work there, and the admin token
  doesn't work anywhere else. The `/admin` page is just static HTML that calls that API.
- Everything it changes is ledgered: grants record a `grant` entry with `reason: "admin"` and the note you typed.
- Backups use `VACUUM INTO`, so they're consistent copies taken while the backend keeps serving.

**Game server client** (`shared/Game.BackendClient`, namespace `RaidZero.BackendApi`)
- `BackendClient` is the game server's side of `/v1/server`: `JoinAsync` from the authenticator,
  `ReportExtracted` / `ReportDied` from gameplay, `EndRaidAsync` when the raid is over. Heartbeats run on their own
  from the first join until the close is answered.
- Reports go through a durable outbox: one append-only journal per raid in the outbox directory, flushed to disk
  before the call returns, delivered in order (the close always last) and retried with the same idempotency key
  until the backend answers. A new run resends whatever the last one left. Refused reports are kept as
  `{raid_id}.rejected.jsonl` and raise `ReportRejected`.
- A wrong server token or catalog (401, 403, `catalog_mismatch`) keeps reports waiting instead of dropping them,
  since fixing the server and restarting it will get them through.
- Settings come from the command line, then the environment, and startup fails if a required one is missing:
  `--backend-url` / `RAIDZERO_BACKEND_URL`, `--server-token` / `RAIDZERO_SERVER_TOKEN`, and optionally
  `--outbox-dir` / `RAIDZERO_OUTBOX_DIR`. Each server process needs its own outbox directory.
- Same rules as `Game.Shared` (Unity compiles it), plus one dependency: Newtonsoft.Json. JSON is built by hand
  with `JObject`, never by reflection, so IL2CPP code stripping can't break it.

**Raid inventory** (`shared/Game.Raid`, `RaidInventoryService`)
- One `InventoryTree` per raid. Its roots are every player's character, every loot container, every body
  and every pile on the ground, so looting, picking up and dropping are moves between roots and an item can't
  be in two places.
- A living player can touch their own gear, plus containers they have open (`Open`/`Close`) and piles on the
  ground (`PickUp`). The service doesn't know about distance: the game checks reach before calling `Open` or
  `PickUp`, and calls `Close` when the player walks away. Another living player's gear is never reachable.
- Players can't destroy items in a raid: `discard` is refused, and `Drop` puts the item in a new `ground`
  pile, which disappears once someone empties it. `Kill` turns a character into a body anyone can open.
  `Extract` hands back the tree for `BackendClient.ReportExtracted`.
- `Consume` uses up rounds and medkits. It's a method on `InventoryTree`, not an op, so no client request can
  ever ask for it, and it changes the node in place because it runs on every shot.
- Every change returns updates: for each player who can see a changed container, its new contents. Send
  whole containers to clients rather than item diffs; they're small, and clients just replace their view.

**Raid inventory over the network** (`RaidProtocol`, `RaidInventoryHost`, `RaidInventoryView`)
- The messages are in `shared/Game.Shared/Runtime/Raid`: requests from the client (inventory ops, pick up,
  drop, open, close) and views, closes and results from the server. `RaidProtocol` turns them into compact
  bytes and back; Fish-Net only carries those bytes, as broadcasts.
- `RaidInventoryHost` (in `Game.Raid`) is the server's end: give it each client's bytes along with the player
  ID from the *connection*, never from the message, and send what it returns. It decodes hostile input
  safely (caps on sizes and counts; anything malformed raises `ProtocolViolation` and is ignored), rate
  limits each player, and checks reach through the game's `CanReach` callback.
- Call `Tick` every frame: it closes containers players walked away from, and sends the gear views that
  `Consume` holds back so firing doesn't resend a player's gear on every shot. Ammo counts belong on the weapon.
- `RaidInventoryView` is the client's end, for the inventory screen: what the player can see, which items
  are waiting on a request (grey those out), and events when anything changes.
- The catalog must have `character` and `ground` definitions. Loot containers (such as `crate_wooden`) are
  ordinary definitions with a grid; their contents need fresh `Uuid7` IDs, which they keep into the stash.

**Raid lifecycle** (`RaidLifecycle` and `ExtractionTracker` in `Game.Raid`, `RaidPhase` in `Game.Shared`)
- A raid waits until its first join, then runs its clock (25 minutes by default). It ends when the clock
  runs out, when everyone who joined has extracted or died, or when nobody inside has been connected for the
  grace period (3 minutes). It doesn't end early while a join is still with the backend, and it refuses new
  joins once it's ending, before anyone's gear moves.
- When it ends, players still inside are missing in action: the server tells them, and the backend's close
  counts them as dead. A dedicated server then exits, one raid per process, so a supervisor can start a fresh
  one; in the editor the server just stops.
- Extraction is staying inside one zone for its countdown (8 seconds by default). Leaving cancels it, and
  taking damage starts it over.
- Both are plain C# with no engine in them, so every rule is unit tested; `RaidServer` drives them.

**Unity networking** (`unity/RaidZero/Assets/_Project/Scripts/Net`, assembly `Game.Net`)
- The thin Fish-Net layer over everything above. `RaidAuthenticator` lets a client in only with a raid ticket
  the backend accepts; `RaidServer` owns the backend client and the raid's inventory on the server;
  `RaidInventoryClient` is the client's end, with the `View` the inventory screen draws from.
- Gameplay calls `RaidServer.PlayerDied`, `Consume` and `InterruptExtraction` (on damage). Levels place
  `ExtractionZone`s, and crates carry a `LootContainer`; bodies and ground piles register in
  `ContainerRegistry` so the server can check reach. A dead player's body must be its own object: their
  connection closes a few seconds after they leave, and Fish-Net despawns whatever it owned.
- `RaidSessionClient` is the client's view of the raid: its phase, the clock, the extraction countdown, and
  a `LeftRaid` event saying how the player left. It's carried by three small broadcasts.
- Inventory messages travel as two Fish-Net broadcasts carrying `RaidProtocol` bytes, so the network layer
  never interprets them.
- Clients get their ticket from `RaidTickets.Next`, which the Hideout will set; in the editor they fetch a
  dev ticket from the local backend instead.

**Shared code**
- `shared/Game.Shared` targets netstandard2.1 with C# 9 and nullable off, because Unity compiles it too.
  The compiler enforces the language version; avoid .NET APIs newer than netstandard2.1
  (`Guid.CreateVersion7`, `Random.Shared`, and similar). Use `Uuid7.NewId()` for IDs.
- No package dependencies. JSON stays at the edges: the backend uses System.Text.Json and Unity uses Newtonsoft,
  both with a snake_case naming policy.

## Using the shared code from Unity

`shared/Game.Shared` is also a Unity package (`package.json` plus `Runtime/RaidZero.Shared.asmdef`). With the
Unity project at `unity/RaidZero`, add this to `unity/RaidZero/Packages/manifest.json`:

```json
"com.raidzero.shared": "file:../../../shared/Game.Shared",
"com.raidzero.backendclient": "file:../../../shared/Game.BackendClient",
"com.raidzero.raid": "file:../../../shared/Game.Raid"
```

The backend client pulls in Unity's `com.unity.nuget.newtonsoft-json` package by itself. Neither it nor the raid
rules are auto-referenced: add `RaidZero.BackendClient` and `RaidZero.Raid` to the references of
`Game.Server.asmdef` only, so client code can't reach them. The packages' `.meta` files are already committed;
commit any Unity adds for new files.

The Hideout talks to the backend through `PlayerSession` (in the backend client package; `Hideout.Session` in
`_Project/Scripts/Net` creates one). It keeps the player's profile and raises `ProfileChanged` whenever it
changes. `ApplyAsync(ops)` checks a stash edit with the same `InventoryTree` rules the backend uses, so the UI
can refuse a bad drop instantly without a round trip; sends it; and on success applies it locally, using the
IDs the backend gave any new stacks. If the stash changed elsewhere in the meantime (`version_conflict`), it
refreshes and sends the edit once more if it still fits. It never resends after a lost connection, since the
edit may already have landed: it refreshes instead. A 401 raises `LoggedOut`. `RequestTicketAsync(mapId)` and
`WaitForRaidResultAsync(raidId, timeout)` cover deploying and the post-raid screen; `Hideout.DeployAsync`
requests the ticket and connects to the server it names. Every call stays on Unity's main thread.

## CI and deployment

GitHub Actions builds and tests everything on every push and pull request, with warnings as errors. Every push
to `main` also builds `backend/Dockerfile` for amd64 and arm64 and pushes `ghcr.io/<owner>/<repo>/backend` tagged
`latest` and `sha-<commit>`. `deploy/README.md` covers running it on a VPS behind Caddy, plus backups and restores.

To build the image yourself, from the repo root: `docker build -f backend/Dockerfile -t raidzero-backend .`

A second workflow builds the dedicated game server whenever the Unity project or shared code changes: a Unity
Linux Dedicated Server build through [GameCI](https://game.ci), wrapped in `unity/server.Dockerfile`. It starts
the backend and the server together and checks the server comes up ready, then on `main` pushes
`ghcr.io/<owner>/<repo>/gameserver`. It needs a Unity license as repository secrets (`deploy/README.md` says
how); without them it skips the build. The game server tells the backend which map it has loaded on every join
(`RaidServer`'s Map Id), and the backend refuses a raid on another map (`wrong_map`) before anyone's gear moves.

## What's next (backend)

The Milestone 1 backend is complete: every endpoint in the API doc exists and is covered by tests, including
`RaidApiTests`, which plays whole raids over HTTP the way the game server will. The game server's side of that
conversation exists too, as `shared/Game.BackendClient`, and so do the raid's inventory rules, in
`shared/Game.Raid`, along with the inventory's network protocol, and the Hideout's side, as `PlayerSession`.
The thin Unity layers over them live in `unity/RaidZero/Assets/_Project/Scripts/Net`, including the raid's
lifecycle, and CI builds the dedicated server and deploys it next to the backend. Next are Steam login in place
of dev login, and a matchmaker that spreads raids across several servers.
