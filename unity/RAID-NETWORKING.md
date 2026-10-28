# Raid networking (draft for review)

This connects the Unity project to the raid code that lives in `shared/`:

- the backend client: joining, heartbeats, outcome reports;
- the raid's inventory rules: who can touch what, looting, drops, bodies;
- the inventory protocol: the bytes clients and the server exchange.

All the logic is plain C# with its own tests. These scripts are the thin Fish-Net layer around it. They
have been compiled against Unity and Fish-Net 4.7.4 stand-ins but never run inside Unity, so expect to
fix small things on the first try.

## What's here

All in `Assets/_Project/Scripts/Net` (assembly `Game.Net`, which `Assembly-CSharp` can use directly):

| Script | Runs on | What it does |
| --- | --- | --- |
| `RaidAuthenticator` | both | Client: sends its raid ticket on connect. Server: redeems it with the backend, which moves the player's gear into the raid, then lets them in |
| `RaidServer` | server | Owns the backend client and the raid inventory. Fills crates at start, relays inventory messages, checks reach by distance. Gameplay calls `PlayerDied`, `PlayerExtracted`, `Consume`, `EndRaidAsync` |
| `RaidInventoryClient` | client | Sends inventory requests and keeps `View` (what the player can see) up to date. The inventory screen reads `View` and calls `Request`, `PickUp`, `Drop`, `Open`, `Close` |
| `LootContainer` | both | Put it on a crate. Sets its contents, and gives it an ID both sides derive from the scene, so nothing needs syncing |
| `ContainerRegistry` | server | Where each container is in the world, for the reach check. Crates register themselves; bodies and ground piles should too |
| `RaidTickets` | client | Where the client's ticket comes from. The Hideout will set `RaidTickets.Next`. In the editor it gets a dev ticket by itself |
| `DevSettings` / `ServerConfig` | both | Settings. A server reads `--backend-url` / `RAIDZERO_BACKEND_URL` and `--server-token` / `RAIDZERO_SERVER_TOKEN`; in the editor it falls back to `UserSettings/RaidZeroDev.json`, which is created for the local backend and never committed |
| `DevInventoryOverlay` | client | Press F1 in play mode: your gear, open containers, Take/Drop buttons. A stand-in until the real inventory screen exists |
| `Editor/CatalogBuildStep` | editor | Copies `shared/catalog/item_catalog.json` into builds |

## Setting it up (once)

1. Pull and open the project. Unity imports the two new packages, plus Unity's Newtonsoft JSON package,
   which one of them depends on. Commit the updated `Packages/packages-lock.json`.
2. Select the NetworkManager object in the scene. Add `RaidServer`, `RaidAuthenticator` and
   `RaidInventoryClient`. Then, on the ServerManager component, set **Authenticator** to the
   `RaidAuthenticator`.

   The scene's NetworkManager is currently an instance of Fish-Net's demo prefab. Unpack it, or save it
   as our own prefab, before deleting `FishNet/Demos`.
3. Add `DevInventoryOverlay` to any object in the scene.
4. To try looting, give a greybox cube a `LootContainer` and list a few items, e.g. `loot_gpu`,
   `ammo_545_ps` x 60 and `medkit_basic`. Every item ID is in `shared/catalog/item_catalog.json`.

## Playing

1. Start the backend: `dotnet run --project backend/Game.Backend` from the repo root.
2. Play as in `TESTING-INSTRUCTIONS.md`: start the server, then clients. Each client logs in as a new dev
   player (`dev-12345`) with the starter kit and joins the same raid. The console should show
   `[raid] Server ready`, then a `joined raid` line for each player.
3. Press F1. Walk up to the crate and choose **Open the nearest crate**, then **Take** something. Try
   **Drop** too; the item goes to a pile on the ground.
4. Pressing Stop ends the raid properly in the editor, so the dev server is free again straight away.
   Anyone who didn't extract loses their gear. That's fine, because each dev player is fresh each time.

## What's still yours

- **Interaction:** looking at a crate or body and pressing a key calls `RaidInventoryClient.Instance.Open(id)`.
- **Ground items:** on `RaidServer.PileCreated`, spawn a networked pickup where the dropping player stands,
  register it in `ContainerRegistry` under the pile ID, and sync the pile ID to clients so they can
  `PickUp` from it. Despawn it on `PileRemoved`.
- **Bodies:** before calling `PlayerDied`, register the body object in `ContainerRegistry` under
  `CharacterIdOf(connection)`, and sync that ID so others can open it.
- **Extraction:** after the countdown completes, call `PlayerExtracted(connection, "zone_name")` and send
  them back to the Hideout.
- **Weapons:** call `Consume` for every shot, and keep the ammo count on the weapon for the HUD.
  Inventory views of your own gear only update about once a second while firing.
- **The real inventory screen**, replacing the F1 overlay.

## Questions for you

- Is a single `Game.Net` assembly all right for now? The plan's Shared/Server/Client/Gameplay split can
  come once there's more code to split.
- Should reach be 2.5 m? It's a field on `RaidServer`.
- When a player disconnects mid-raid, their character stays where it was. The raid's close counts them as
  dead, and the same ticket lets them back in until then. Is that how you want disconnects to work for now?
