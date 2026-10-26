# Raid Zero

An extraction shooter: Unity client and game server (Fish-Net), plus a C# backend that owns everything persistent.
Design docs: *Raid Zero: Backend API & Data Model* and *Raid Zero: Unity Client & Game Server Plan*.

## Layout

```
shared/Game.Shared/        Code compiled by BOTH Unity and .NET: item tree, catalog, validation, UUIDv7
shared/catalog/            item_catalog.json, exported from Unity's ItemDefinition assets
backend/Game.Backend/      ASP.NET Core backend: SQLite + Dapper, migrations, catalog loading
tests/Game.Shared.Tests/   Rules tests (fit, stacking, slots, IDs, catalog hashing)
tests/Game.Backend.Tests/  Catalog loading, migrations and database behaviour against a real SQLite file
unity/                     The Unity project goes here (unity/RaidZero)
```

Build output goes to `artifacts/` at the repo root, never next to the projects (see `Directory.Build.props`).

## Getting started

Requires the .NET 10 SDK.

```sh
dotnet test                                   # everything should pass before you change anything
dotnet run --project backend/Game.Backend     # http://localhost:5080
curl http://localhost:5080/healthz            # {"status":"ok","catalog_version":"...","schema_version":1}
```

On first start the backend creates `backend/Game.Backend/data/raidzero.db`, switches it to WAL mode and applies
the migrations. Delete the `data/` folder to start over.

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
"com.raidzero.shared": "file:../../../shared/Game.Shared"
```

Unity writes `.meta` files into that folder the first time it imports it. Commit them.

## What's next (backend)

1. Dev login, profile and a starter kit grant
2. `POST /v1/me/inventory/ops` with optimistic concurrency on `profile_version`
3. Raid tickets, join, outcomes, close, and the lease-expiry abort job
4. A fake game server test harness that plays whole raids over HTTP
