# Raid Zero: Unity Client & Game Server Plan

Oct 26, 2026 · @user

## Overview

The Unity side owns everything that happens inside a raid: how the game feels, what the server decides, and what each client sees. Milestone 1 ends when one player can load in with gear, fight AI, loot, and either die or extract, with the result showing up in their stash. It is the companion to the backend API and data model doc; where the two meet (joining a raid, reporting outcomes, item IDs), that doc is the source of truth.

**What this side owns in Milestone 1**

- Player controller and camera, with client-side prediction
- Weapons, hit registration and damage
- AI enemies
- The greybox map, loot containers, extraction points and raid timer
- Raid inventory on the server, and the inventory UI on the client
- The dedicated server build

**Rules every system follows**

- **The server decides.** Damage, loot rolls, pickups, deaths and extraction run on the server. Clients send inputs and requests, never results.
- **Always networked.** Daily play uses Fish-Net host mode, so every feature runs through the network path from day one. There is no offline mode to keep in sync.
- **Predict only what the player controls.** Local movement and weapon feedback are predicted; everything else waits for the server.
- **Data lives in ScriptableObjects.** Weapons, items, AI types and loot tables are assets, not hardcoded numbers, so tuning never needs a code change.
- **Test on a bad connection.** Latency and packet-loss simulation stays on during normal development.

## Architecture

The client talks to the game server over Fish-Net and to the backend only from the Hideout; the game server is the only thing that reports raid results.

&#91;embedded content: client, game server and backend · Milestone 1\]

The client and server builds share `Game.Shared` for item data, fit checks and damage math, so the client's previews and the server's decisions can't drift apart.

## Project setup

Use the current Unity 6 LTS release with URP, one Git repo shared with the backend, and assembly definitions that keep server-only code out of the client build.

### Packages

| Package | Why |
| --- | --- |
| Fish-Net | Networking, prediction, lag compensation. Default Tugboat transport |
| Input System | Rebindable input; feeds the predicted controller cleanly |
| AI Navigation | NavMesh surfaces and agents for AI |
| ProBuilder | Greybox the map inside Unity |
| Cinemachine | Optional; first-person camera is simple enough without it |
| Newtonsoft JSON | Backend payloads and the item catalog export |
| Unity Test Framework | Edit-mode tests for inventory and damage logic |

### Folder and assembly layout

```
Assets/_Project/
  Scripts/
    Shared/      Game.Shared.asmdef   item defs, data types, inventory logic, damage math
    Server/      Game.Server.asmdef   raid inventory service, AI brains, loot spawning, BackendClient
    Client/      Game.Client.asmdef   UI, camera, audio, VFX, input
    Gameplay/    Game.Gameplay.asmdef NetworkBehaviours that glue the three together
  Data/          Items, Weapons, AI, LootTables (ScriptableObjects)
  Prefabs/
  Scenes/        Boot, Hideout, Raid_Greybox01
  UI/
Tests/           Game.Tests.asmdef
```

`Game.Shared` references nothing game-specific, so it is easy to test. `Game.Client` must never reference `Game.Server`. Wrap server-only code paths in `UNITY_SERVER` or Fish-Net's `IsServerInitialized` checks so client builds can't call them.

### Scenes

- **Boot:** the `NetworkManager` and long-lived services. Loads first, never unloads.
- **Hideout:** stash and equipment screens backed by the client API. No networking.
- **Raid\_Greybox01:** the map. Loaded by the server through Fish-Net's scene manager.

### Builds

- **Client:** Windows 64-bit.
- **Server:** Linux Dedicated Server build target (headless, no rendering). Build it in CI from week one so server-only breakages show up immediately.
- **Daily play:** host mode in the editor or a client build. A second editor instance (Unity's Multiplayer Play Mode) acts as a second client for sanity checks.

### Version control

- Git with LFS for textures, models, audio and other binaries.
- Editor settings: Force Text serialization, Visible Meta Files. Configure Unity's Smart Merge (UnityYAMLMerge) as the merge tool.
- Only one person edits a given scene at a time. Put most content in prefabs so scene conflicts stay rare.

### Network simulation

Turn on Fish-Net's latency simulator on the transport with about 100 ms latency and 2% packet loss, and leave it on. Turn it off only when profiling something unrelated.

## Player controller

Build a kinematic `CharacterController`-based controller driven by Fish-Net's prediction system: the owning client simulates its own inputs immediately, and the server's result corrects it when they disagree. Getting this right first matters more than anything else in the milestone, because every other system sits on top of it.

### How prediction fits together

- Gather input each tick into a small struct (move vector, look yaw and pitch, sprint, crouch, jump). This is Fish-Net's replicate data.
- A `[Replicate]` method applies one tick of input to the controller. It runs on the owner immediately and on the server when the input arrives.
- A `[Reconcile]` method takes the server's authoritative state (position, velocity, grounded, crouch state) and resets the client to it, after which Fish-Net replays the inputs the server hasn't processed yet.
- Keep the simulation inside the replicate method only. Anything that reads `Time.deltaTime` or `Update` instead of the tick delta will cause constant corrections.

### Scope for Milestone 1

- Walk, sprint, crouch, jump. No lean, slide or stamina yet.
- A fixed tick rate (start at 30 or 60 Hz and keep it constant once chosen; it affects everything downstream).
- First-person camera on the owner; other players see a simple capsule or placeholder model, interpolated smoothly.
- Look direction travels with each input tick, so the server knows exactly where the player was aiming.

### Done when

- With the latency simulator on, movement feels the same as with it off.
- Corrections only happen when something genuinely blocks you, and they're small.
- A second client sees your movement smoothly, without jitter.

## Weapons and hit registration

The shooter sees and hears their shot instantly, but only the server decides whether it hit. The server rewinds other players' hitboxes to where the shooter saw them, so a shot that looked right on screen counts.

### Fire flow

1. **Client (predicted):** plays muzzle flash, sound, recoil and decrements the displayed ammo. Sends a fire request with the tick it fired on and the exact aim ray.
2. **Server validates:** the weapon is equipped, fire rate allows it, the magazine in the server's raid inventory has a round, and the ray starts near the player's server position.
3. **Server rewinds:** uses Fish-Net's collider rollback to move hitboxes back to the shooter's tick, raycasts, then restores them. Cap rewind at about 200 ms so high-ping players can't shoot into the past indefinitely.
4. **Server applies:** consumes the round in the raid inventory, applies damage, and tells clients what happened (impact effects for everyone, a hit marker for the shooter).

Hit markers are server-confirmed in Milestone 1. They arrive one round trip late, which is honest and avoids showing hits that didn't count.

### Data

- `WeaponDefinition` (ScriptableObject, linked to its item `defId`): damage, rounds per minute, range falloff, spread, recoil pattern, compatible magazine defs.
- `Hitbox` components on a character's colliders with a zone (head, torso, limbs) and a damage multiplier.
- One health pool per character for now. Per-limb health is a later decision.

### Ammo and reloads

Ammo is real inventory: the round count lives in the ammo stack inside the magazine item inside the weapon. A reload swaps the weapon's magazine for a loaded one from the rig. That ties gunplay to the economy from day one, and the extraction report already carries the result.

### AI shooting

AI fires through the same server damage path, without rewind since it runs on the server already. One damage path means one place to balance and debug.

### Done when

- At 100 ms simulated latency, shots that visibly land on a moving target register.
- Firing faster than the weapon allows, or with an empty magazine, does nothing on the server.
- Ammo count after a raid matches what was fired.

## AI enemies

AI runs only on the server, perceives the world the same way a player would, and acts through the same movement and damage code as players. Clients just see a networked character.

### Brain and body

Split each AI into a **brain** (decides) and a **body** (acts). The brain outputs intents such as move to a point, aim at a target, or fire. The body carries them out using the same weapon and damage paths as a player.

This split is what makes player-like bots possible later: the same brain can drive a body that loots, carries a real inventory, and extracts.

### Perception

- **Sight:** view distance, a view cone, and a line-of-sight raycast to a few points on the target (head, chest). Run it at 5 to 10 Hz, not every frame.
- **Hearing:** gunshots raise a noise event with a radius; AI inside it get a position to investigate. Footsteps can come later.
- **Reaction time:** a short delay between first seeing a player and first firing. Without it AI feels unfair.
- No cheating: AI never reads a player's position unless perception produced it.

### States

&#91;embedded content: AI state machine · 4 states\]

From any state, health reaching zero ends the brain and turns the body into a loot container.

### Data

`AIProfile` ScriptableObject: health, weapon definition, accuracy curve over distance, reaction time, view distance and angle, and the loot table that fills its body when it dies.

### Movement

Server-side `NavMeshAgent` on a baked NavMesh. Clients receive position through a network transform and interpolate. The body's dead state turns it into a loot container, using the same container code as crates.

### Done when

- AI notices you by sight and by gunfire, fights, loses you, searches, and gives up.
- 8 to 10 AI on the map don't noticeably cost server frame time.
- Killing one leaves a lootable body.

## Raid flow and level

One small greybox map, one server-run raid timer, and two ways out: extract or die. Run one raid per server process, then exit; a fresh process per raid means no state leaks between raids, and it is exactly how container hosting will want to run it later.

### Greybox map

- Roughly 200 by 200 m, built in ProBuilder.
- 3 or 4 distinct points of interest, so routes and risk differ.
- 8 to 12 loot containers, weighted toward the riskier areas.
- 2 extraction points, far from the main spawn.
- AI spawn zones and 2 or 3 player spawn points.
- Bake the NavMesh from the greybox; rebake when geometry changes.

### Raid states on the server

| State | What happens | Leaves when |
| --- | --- | --- |
| Waiting | Scene loaded, loot rolled, AI spawned; players join with tokens | First player spawns, or a join timeout |
| In progress | Timer runs (start with 25 minutes); heartbeats every 10 s | Every player has extracted or died, or the timer hits zero |
| Ending | Anyone still inside dies (missing in action); outcome reports flush from the outbox | All reports acknowledged |
| Closed | Close report sent, process exits | n/a |

### Extraction

A player extracts by staying inside an extraction zone for about 8 seconds. The server tracks the countdown; leaving the zone or taking damage resets it. On completion the server freezes that player's raid inventory, writes the outcome report to the outbox, despawns the character, and tells the client to return to the Hideout, which then fetches the raid result.

### Death

On death the body becomes a loot container holding the player's raid inventory, the server sends a `died` outcome, and the client shows a death screen before returning to the Hideout.

### Loot

- `LootTable` ScriptableObject: weighted entries, each with an item def and a quantity range.
- Containers roll on the server at raid start from a per-raid seed, which makes bug reports reproducible.
- Every spawned item gets a UUIDv7 at spawn time, so its ID is stable from container to stash.

### Done when

- A full raid runs start to finish in host mode and as a dedicated server.
- Extract and death both land the right result in the stash.
- The timer running out kills anyone still inside.

## Inventory on the Unity side

Inventory is where the two halves of the project meet, so the rules live in one shared C# library used by the client, the game server and, by mirror image, the backend. The client only previews; the server and backend decide.

### Who builds what

| Piece | Owner | Notes |
| --- | --- | --- |
| Shared inventory model (`Game.Shared`) | Backend developer | Item tree, grid fit checks, slot rules, catalog loading |
| Item catalog export (editor script) | Backend developer | ScriptableObjects to `item_catalog.json` |
| `RaidInventoryService` and `BackendClient` (server) | Backend developer | Server-side truth during a raid, outbox, join and outcome calls |
| `PlayerInventoryNetwork` (RPCs and views) | Both, pair on it | The seam; agree on message shapes first |
| Inventory UI (raid and Hideout) | Unity developer | Drag and drop, grid display, tooltips |
| World containers, pickups, bodies | Unity developer | Interaction, open and loot flow |
| Item `ScriptableObject` authoring | Unity developer | Icons, sizes, stack limits, slots |

### In a raid

- The UI sends a request (move, split, merge, pick up, drop, loot from container) through a `[ServerRpc]` and greys the item out until the answer arrives.
- The server validates with `RaidInventoryService` and sends the owner the result plus a diff of their tree.
- The client uses the shared fit checks to show where an item can drop while dragging, but never applies a change until the server confirms it.
- At 100 ms latency inventory still feels fine without prediction; revisit only if playtests say otherwise.

### In the Hideout

The Hideout calls the client API directly over HTTP: fetch the profile, send inventory ops with the profile version, refetch on a version conflict. The same UI components work in both places; only the transport behind them differs.

### The contract

These must match exactly across Unity and the backend. Change them in one pull request that touches both sides.

- **Item tree shape:** `id`, `def_id`, `slot`, optional `x`, `y`, `rotated`, `quantity`, `durability`, `children`. The same shape as the backend's join and outcome payloads.
- **IDs:** UUIDv7 strings. The server mints them for raid loot; the backend mints them for everything else.
- **Slot names:** one shared constants file (`primary`, `secondary`, `armor`, `rig`, `backpack`, `magazine`, `rounds`, `grid`).
- **`defId` values:** stable forever once an item has existed in anyone's stash. Rename the display name, never the ID.

## Build order

Two tracks run in parallel until step 8, when they join: the Unity developer builds the game you can play, and the backend developer builds the inventory and persistence it plugs into. Each step ends with something you can check.

1. **Skeleton (both).** Repo, packages, assembly definitions, Boot scene, host mode, Linux server build in CI.
   - Check: a host and a second client connect in an empty scene, with the latency simulator on.
2. **Predicted movement (Unity).**
   - Check: the controller's done list above.
3. **Greybox map v0 and NavMesh (Unity).**
   - Check: you can walk every route; AI agents can path between points of interest.
4. **Hitscan weapon (Unity).** First against a static dummy, then against the second client.
   - Check: the weapons done list above.
5. **AI v1 (Unity).** Patrol and attack first, then investigate and search.
   - Check: the AI done list above.
6. **Shared inventory model and catalog export (backend), in parallel with 2 to 5.**
   - Check: unit tests cover fit, stacking, nesting and slot rules; the catalog exports from real ScriptableObjects.
7. **Raid inventory and loot (both).** `RaidInventoryService`, loot containers, pickup, and the in-raid inventory UI.
   - Check: loot a crate, equip a magazine, reload from it, and drop items, all at simulated latency.
8. **Join from the backend (pair).** Fish-Net authenticator, `/join`, spawn from the returned loadout.
   - Check: gear set up in the stash appears on the character in the raid.
9. **Extract, die, timer and outbox (pair).**
   - Check: the raid flow done list above, including killing the server mid-raid and confirming the refund.
10. **Hideout screens (Unity).** Stash and equipment UI over the client API.
    - Check: rearrange gear, start a raid, come back, and see the result.
11. **End-to-end playtest (both).** Several full raids at 100 ms and 2% loss, then once as a dedicated server on a cheap VPS.
    - Check: Milestone 1 is done.

## Testing and open questions

Automate what can be expressed as rules, and playtest what can only be felt.

### Testing

- **Edit-mode unit tests** for everything in `Game.Shared`: inventory fit and stacking, damage math, loot table rolls with a fixed seed.
- **A headless smoke test in CI:** start the server build, connect one scripted client, run a 60-second raid with an instant extraction, and assert the outcome report was written.
- **Network conditions:** test at 0 ms, 100 ms with 2% loss, and 200 ms with 5% loss before calling any gameplay feature done.
- **Two-client checks:** use Multiplayer Play Mode or two builds for anything another player can see.
- **Weekly playtest:** play several full raids together and write down what felt wrong, before fixing anything.

### Open questions

- [ ] Tick rate: 30 or 60 Hz? Higher feels better but costs bandwidth and server CPU per player.
- [ ] Grid inventory or slot-based? This decides how much UI work step 7 is.
- [ ] Disconnects: does the character stay in the world and allow reconnect, or count as death after a timeout?
- [ ] Hit markers: keep server-confirmed, or predict them once playtests show the delay matters?
- [ ] Third-person body for remote players: buy an animated asset pack now, or stay on capsules until the loop is fun?

