# Raid Zero

An extraction shooter: Unity client and game server (Fish-Net), plus a C# backend that owns everything persistent.
Design docs: *Raid Zero: Backend API & Data Model* and *Raid Zero: Unity Client & Game Server Plan*.

## Layout

```
shared/Game.Shared/        Code compiled by BOTH Unity and .NET: item tree, catalog, validation, UUIDv7
shared/Game.BackendClient/ The game server's connection to the backend: join, heartbeats, durable outbox
shared/catalog/            item_catalog.json, exported from Unity's ItemDefinition assets
backend/Game.Backend/      ASP.NET Core backend: SQLite + Dapper, migrations, catalog loading
tests/Game.Shared.Tests/   Rules tests (fit, stacking, slots, IDs, catalog hashing)
tests/Game.Backend.Tests/  Catalog loading, migrations and database behaviour against a real SQLite file
tests/Game.BackendClient.Tests/  The game server client against a fake backend, and whole raids against the real one
unity/                     The Unity project goes here (unity/RaidZero)
deploy/                    Running the backend on a VPS: Compose, Caddy, backups (see deploy/README.md)
.github/workflows/ci.yml   Tests every push and pull request; pushes the backend image to GHCR from main
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
- `catalog_version` is a hash of every rule-relevant field (sizes, stacks, tags, slots, grids). Display names are
  excluded, so renaming an item needs no redeploy.
- The backend refuses to start if the declared version doesn't match the contents, and a test checks the same
  thing in CI. If you edit the file by hand, the error message tells you the correct value.
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
  work on one dev server. Each server runs one raid at a time.

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
"com.raidzero.backendclient": "file:../../../shared/Game.BackendClient"
```

The backend client pulls in Unity's `com.unity.nuget.newtonsoft-json` package by itself. Its assembly isn't
auto-referenced: add `RaidZero.BackendClient` to the references of `Game.Server.asmdef` only, so client code can't
reach it. Unity writes `.meta` files into both folders the first time it imports them. Commit them.

## CI and deployment

GitHub Actions builds and tests everything on every push and pull request, with warnings as errors. Every push
to `main` also builds `backend/Dockerfile` for amd64 and arm64 and pushes `ghcr.io/<owner>/<repo>/backend` tagged
`latest` and `sha-<commit>`. `deploy/README.md` covers running it on a VPS behind Caddy, plus backups and restores.

To build the image yourself, from the repo root: `docker build -f backend/Dockerfile -t raidzero-backend .`

## What's next (backend)

The Milestone 1 backend is complete: every endpoint in the API doc exists and is covered by tests, including
`RaidApiTests`, which plays whole raids over HTTP the way the game server will. The game server's side of that
conversation exists too, as `shared/Game.BackendClient`. Next is the thin Unity layer over it (the Fish-Net
authenticator and passing the server's settings in), then Steam login in place of dev login, and a matchmaker
that spreads raids across several servers.
