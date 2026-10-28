# Testing Instructions

To test the client using Multiplayer Play Mode:

1. Open the Multiplayer Play Mode menu with `Window > Multiplayer > Multiplayer Play Mode`
2. Check boxes in menu to activate extra players.
3. Once extra player windows appear, start Play Mode
4. Choose one player or the editor window to be the server and click the `Server` button in the top left.
5. Click `Client` on each other player.

If you change any code or update any prefab, you must deactivate and reactivate each additional player in the Multiplayer Play Mode menu to get the changes.

## Playing against the backend

The game server gets each player's gear from the backend and reports back when they extract or die, so
run the backend first, from the repo root:

```sh
dotnet run --project backend/Game.Backend
```

- In the editor, the server finds the backend through `UserSettings/RaidZeroDev.json`. It's created on the
  first run with settings for the local backend, and it isn't committed, so each of you can point yours
  elsewhere. Outside the editor, use `--backend-url` and `--server-token`, or the `RAIDZERO_BACKEND_URL` and
  `RAIDZERO_SERVER_TOKEN` environment variables.
- Every client logs in as a new dev player (`dev-12345`) with the starter kit, and they all join the same
  raid. The console shows `[raid] Server ready`, then a `joined raid` line for each player.
- Press F1 in play mode for the debug inventory: your gear, anything you have open, a button that opens
  the nearest crate, and Take and Drop buttons. Crates are objects with a `LootContainer`.
- Pressing Stop ends the raid, so the dev server is free again straight away. Anyone who didn't extract
  loses their gear; it doesn't matter, because each dev player is new each time.

## Raid lifecycle

The raid waits until someone joins, then runs its clock. It ends when the clock runs out, when everyone who
joined has extracted or died, or when nobody inside has been connected for 3 minutes. Anyone still inside
then is missing in action, and the server stops; start it again for a new raid.

To set it up in a scene:

- Add `RaidSessionClient` to the NetworkManager, next to `RaidInventoryClient`. The HUD reads the clock and
  the extraction countdown from it, and the end-of-raid screen listens to its `LeftRaid` event.
- Add one or more `ExtractionZone`s to the level: an empty GameObject with the component, sized with the
  green gizmo so the box covers where players stand. Stand in it for 8 seconds to extract.
- For testing, set `Raid Minutes` on `RaidServer` to 2 or so, so the clock runs out quickly. A dedicated
  server takes `--raid-minutes` on the command line instead.

What to check (the top right of the screen shows the raid's phase, the clock, any extraction countdown, and
how you left):

1. The clock starts at the full raid length when the first client joins and counts down; a second client
   joining later sees the same time left.
2. Standing in a zone starts the countdown; stepping out cancels it; staying in extracts you. You see
   "you left: Extracted", then the client disconnects about 3 seconds later.
3. With two clients, the raid keeps going after one extracts, and ends once the other extracts too.
4. Letting the clock run out with a client inside shows "you left: MissingInAction", and the server stops.
5. Disconnecting every client (and not coming back) ends the raid after 3 minutes.

In each case, `GET /v1/me/raids/{raid_id}` on the backend (or the admin tool) shows the outcome: extracted,
or died for missing in action.

