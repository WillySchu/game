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
- The editor tests on a bad connection, as the plan asks: Latency Simulator on the NetworkManager's Transport
  Manager is ticked (100 ms of latency and 2% packet loss each way). Untick it to compare. The console says so at
  startup. Builds never simulate, whatever the box says, unless started with `--simulate-lag`.
- Every client logs in as a new dev player (`dev-12345`) with the starter kit, and they all join the same
  raid. The console shows `[raid] Server ready`, then a `joined raid` line for each player.
- Press F1 in play mode for the debug inventory: your gear, anything you have open, a button that opens
  the nearest crate, and Take and Drop buttons. Crates are objects with a `LootContainer`.
- Pressing Stop ends the raid, so the dev server is free again straight away. Anyone who didn't extract
  loses their gear; it doesn't matter, because each dev player is new each time.

## Inventory screen

`SampleScene` already has one (the `InventoryScreen` object); in another scene, add `InventoryScreen` (assembly
`Game.UI`) to any GameObject. It makes its own UIDocument from `Assets/_Project/UI/Resources/InventoryPanelSettings` and styles
itself with `InventoryScreen.uss` beside it, so there's nothing else to wire up. Join a raid as above, then:

- **Tab** opens and closes the screen. The mouse is freed and the player's input switches to the `UI` action
  map, so the camera stays still; closing puts both back and closes anything you had open.
- **F** opens the nearest crate (within 2.5 m of the camera) and the screen with it. This is a stand-in until
  interaction exists.
- **Drag** an item; **R** while dragging turns it; **Escape** cancels the drag (or closes the screen).
- **Ctrl+click** (Cmd+click on a Mac) quick-moves an item.
- **Shift+drag** a stack to split it: let go where part of it should go, and it asks how many (Enter splits,
  Escape or clicking outside the box cancels).
- **Double-click** a container that's inside another (a backpack lying in a crate, a pouch in your backpack) to
  open it as its own panel on the right.

What to check:

1. Your gear is on the left: the five slot boxes, then a panel each for the rig and backpack grids. The crate
   is on the right, with a × that closes it.
2. Dragging shows a ghost that follows the pointer from where you grabbed it, and the cells it would land on
   in green, or red where it won't fit. Letting go on green moves it; the item dims until the server answers.
   Letting go on red changes nothing and the status line says why.
3. R turns a 2x1 (the graphics card) mid-drag; the ghost and the cells follow, and it lands turned.
4. Dropping ammo on the same ammo merges; ammo on a magazine loads it (the magazine's corner shows rounds,
   like `30/30`, and so does a rifle holding it); a magazine on the rifle loads it; anything on the backpack
   puts it inside. The target gets a green or red outline.
5. Dragging the rifle onto the empty primary box equips it; dragging something that doesn't fit a slot goes
   red with a reason.
6. Letting go over the dimmed backdrop, outside the window, drops the item; with two clients, the other one
   sees it go.
7. Ctrl+click on a crate item puts it in an empty slot that takes it, else the rig, else the backpack;
   Ctrl+click on your own item puts it in the crate.
8. Closing and reopening the screen, and opening the crate from the F1 overlay, keep it in step: anything
   that changes on the server shows up without reopening.
9. Shift+drag 60 rounds to an empty cell and pick 20: a stack of 20 lands there and 40 stay. Shift+drag onto
   another stack or a magazine moves only the number you pick (no more than the magazine holds). Picking all
   of them is an ordinary move. Shift+dragging out of the window says to drop the whole stack.
10. Put a backpack in the crate and double-click it: it opens on the right with its own ×, and items can be
    dragged in and out of it. Ctrl+click on your own gear sends it there first. Equipping that backpack, or
    closing the crate, closes its panel; so does closing the screen.
11. Rest the pointer on an item: after a moment a tooltip shows its name and category, then only the lines
    that apply: Condition (the rifle, armor), Stack (`37 / 60`), Magazine and Rounds (a rifle: which magazine,
    and `24 / 30 (5.45x39 PS)`; a magazine: its rounds), Contents (the backpack: `3 items, 7 / 20 cells`), and
    the asset's Description if it has one. It follows the pointer, flips near the screen edges, disappears while
    you drag, and updates if the item changes under it.
12. Items without an icon have the plain look: their category in small capitals over the name, on their
    category's colour. See Icons below for testing the rendered look.

Positions of a container's pockets: each grid on an item asset has **Place On Screen** and **Position** (in
cells). Grids with it ticked go there; the rest flow left to right in their fill order, wrapping, below the
placed ones. It's presentation only, so it isn't in the catalog and changing it needs no export.

## Hideout

`SampleScene` has one (the `HideoutScreen` object); in another scene, add `HideoutScreen` (assembly `Game.UI`) to
its own GameObject, not inside the NetworkManager prefab. With the local backend running, press Play: the Hideout
fills the screen whenever you're not in a raid.

1. **Log in:** type a name and press Log in. Dev login only knows names, so the same name is the same player
   (a new name starts with the starter kit). The name is remembered, and next time it logs straight back in.
2. **Gear and stash:** your gear on the left, the stash (10x30) on the right. Everything works as in a raid
   (drag, R, Shift+drag, Ctrl+click, double-click, tooltips) except dropping, as there's no ground. Moved items
   dim until the backend answers. Refresh reloads; Log out goes back to the name.
3. **Deploy:** gets a ticket for `greybox_01` (Map Id on the component) and connects to the server the ticket
   names, as the F1 overlay does. The Hideout hides once the connection starts. Start a server first (Multiplayer
   Play Mode's Server button in another player, or a dedicated server), or you'll get "No raid server is free" or
   a connection that comes straight back.
4. **Back from the raid:** extract (or wait out the clock). The Hideout comes back, says it's waiting for the
   result, then shows it: Extracted / Lost in the zone / Raid called off, what came home and what was lost.
   OK dismisses it for good; a result you never dismissed shows again at the next login.
5. **Still in a raid:** if you leave a raid by quitting, the player bar says your gear is in a raid and the button
   says Rejoin (it works while that raid runs and this game remembers the ticket).

Scene flow can take over Deploy: anything listening to `HideoutScreen.DeployRequested` gets the map ID and the
screen doesn't connect by itself. Untick **Show When Not In A Raid** to show and hide it from code instead.

## Icons

An item's icon is its asset's **Icon** if you've drawn one, else the one **Raid Zero > Items > Render Icons**
made from its **World Prefab**, else the plain look. Nothing has a model yet, so to try it:

1. Make a stand-in model: a ProBuilder shape works (export it as an asset, as with the tree), or a free CC0
   model (Kenney, Quaternius). Lay it lengthwise along X: the icon looks at the side, along +Z, with +X to the
   right and +Y up.
2. Make a prefab of it and set it as an item's **World Prefab** (under Presentation), say the rifle's.
3. **Raid Zero > Items > Render Icons** renders every item with a world model; **Render Icon** in an item
   asset's ⋮ menu renders just that one. The PNGs go in `Assets/_Project/Data/ItemIcons`, at 64 pixels per cell
   (the rifle is 256x128), with a transparent background, and each item's **Rendered Icon** points at its own.
4. On the inventory screen the item shows the picture, with a small name in the top-right corner and the count
   in the bottom-right. Rotate it (R while dragging): the picture turns with it, in the grid and on the ghost.
5. If a model faces the wrong way, set **Icon Angles** on the item (degrees) and render again. If a render
   doesn't read well, draw one and set it as **Icon**: a hand-made icon always wins.

Check the PNGs went into LFS before pushing (`git lfs ls-files`); `.png` files do by the repo's rules.

## Loot tables

Crates roll their contents from loot tables at the start of each raid. A crate with anything in its
**Contents** list uses that list instead, for testing, so to see the tables, select the crate in the scene and
empty its Contents (or remove the entries one by one).

- The tables are in `Assets/_Project/Data/Loot`. `loot_wooden_crate` is the wooden crate's default (set on the
  `crate_wooden` item asset, under Server); it picks one to four times from the ammo, valuables, medical,
  weapons and gear tables, with some chance of nothing.
- Select a table: the inspector lists any problems under the entries, and **Roll it 1,000 times** shows what
  a container gets. Try raising Max Rolls on `loot_wooden_crate` to 10 and simulating: it warns that items
  didn't fit.
- The server's console shows `[raid] Loot seed N` when the raid starts. Put N in **Loot Seed** on `RaidServer`
  (on the NetworkManager in the scene; a dedicated server takes `--loot-seed N`) and the crates come out the same next time; set it back to 0 for new loot each raid.

What to check:

1. Opening the crate in two raids in a row shows different loot (and sometimes none).
2. With a fixed Loot Seed, it's the same both times.
3. Weapons sometimes come with a magazine and rounds, and their condition varies; ammo comes in varied stacks.
4. A crate with its own table (the **Loot Table** field on its `LootContainer`) uses that instead of the default.

## Raid lifecycle

The raid waits until someone joins, then runs its clock. It ends when the clock runs out, when everyone who
joined has extracted or died, or when nobody inside has been connected for 3 minutes. Anyone still inside
then is missing in action, and the server stops; start it again for a new raid.

To set it up in a scene:

- Add `RaidHud` (assembly `Game.UI`) to its own GameObject, as with the inventory and Hideout screens. It shows
  the raid clock, the extraction countdown and the card as you leave, from the `RaidSessionClient` on the
  NetworkManager, which it adds by itself if the scene doesn't have one.
- `SampleScene` has one `ExtractionZone`, over the extraction pad in the far corner. Elsewhere, add one or more:
  an empty GameObject with the component, sized with the green gizmo so the box covers where players stand.
  Stand in it for 8 seconds to extract.
- For testing, set `Raid Minutes` on `RaidServer` to 2 or so, so the clock runs out quickly. A dedicated
  server takes `--raid-minutes` on the command line instead.

What to check (the HUD shows the clock at the top and the countdown low in the middle; the F1 overlay's top
right shows the same as text):

1. The clock starts at the full raid length when the first client joins and counts down; a second client
   joining later sees the same time left. In the last minute it turns red. It stays visible with the inventory
   open.
2. Standing in a zone starts the countdown ("Extracting at ExtractionTrigger: 8" and a bar filling); stepping
   out cancels it; staying in extracts you. The "Extracted" card comes up, the client disconnects about 3
   seconds later, and the card stays a moment longer over the Hideout coming back.
3. With two clients, the raid keeps going after one extracts, and ends once the other extracts too.
4. Letting the clock run out with a client inside shows the "Missing in action" card, and the server stops.
5. Disconnecting every client (and not coming back) ends the raid after 3 minutes.

In each case, `GET /v1/me/raids/{raid_id}` on the backend (or the admin tool) shows the outcome: extracted,
or died for missing in action.

## Deploying from the overlay

When the client isn't connected, the F1 overlay has a **Deploy** button. It goes the way the Hideout will: a
dev login, a ticket for `map_id` from `UserSettings/RaidZeroDev.json`, then a connection to whatever server the
ticket names. Against the local backend that's this editor's server (start it first with the `Server` button);
with `backend_url` pointing at a machine running `deploy/compose.local.yaml` (see `deploy/LOCAL.md`) or the VPS,
it's the dedicated server there.

The editor stays logged in as the same dev player between Play sessions, so Deploy brings them back: into the
raid they dropped out of, or with whatever they extracted with. A dev player who died or went missing in action
has no gear left, so Deploy starts a fresh one with the starter kit instead.

## A client build from CI

The **Game client** workflow builds Windows and macOS clients on every push to `main` that touches the Unity
project (or by hand: Actions > Game client > Run workflow). Open the run on GitHub and download
`RaidZero-Windows-<commit>` or `RaidZero-macOS-<commit>` from the Artifacts list at the bottom; they're kept
for 14 days. They're Development builds, so they show "Development Build" in the corner and the F1 overlay
works.

- **Windows:** unzip and run `RaidZero.exe`. SmartScreen warns about an unrecognised app: More info > Run anyway.
- **macOS:** unzip, then in Terminal, in the folder with `RaidZero-macOS.tar.gz`:

  ```sh
  tar -xzf RaidZero-macOS.tar.gz
  xattr -dr com.apple.quarantine RaidZero.app   # downloaded from the internet: let it open
  codesign --force --deep -s - RaidZero.app      # Apple silicon only runs signed code: sign it for this Mac
  open RaidZero.app
  ```

Then press F1. There's no RaidZeroDev.json in a build, so type the backend's address (the server PC's
Tailscale address, `http://100.x.y.z:5080`) in the field above Deploy, press **Use this backend** (it's
remembered next time), and press **Deploy**. `--backend-url <address>` on the command line works too.

What to check:

1. Both builds start, and F1 shows the backend you set and the same network code as the server's log.
2. Deploy joins the PC's dedicated server; the inventory screen (Tab), looting and extraction work as in the
   editor.
3. Restarting the client keeps the backend address.

## A dedicated server build

CI builds the dedicated server on every change to the Unity project (`.github/workflows/server.yml`). To build
one yourself, install the **Linux Dedicated Server Build Support** module in Unity Hub, pick **Linux Server** in
the Build Profiles window, and build to `build/StandaloneLinux64` at the repo root under the name
`RaidZeroServer`. Then, from the repo root, with the backend running:

```sh
docker build -f unity/server.Dockerfile -t raidzero-gameserver build/StandaloneLinux64
docker run --rm -p 7770:7770/udp -e RAIDZERO_BACKEND_URL=http://host.docker.internal:5080 \
  -e RAIDZERO_SERVER_TOKEN='3ogBGujpU2KY3j673e4CUY3CYXusmi72kOaph+xeRQs=' raidzero-gameserver --raid-minutes 5
```

The build is x86-64, so on an Apple Silicon Mac Docker runs it under emulation (add `--platform linux/amd64` to
both commands); it's slow but fine for a check. Wait for `[raid] Server ready`, then use **Deploy** in the
editor's overlay against the local backend.

CI's raid test runs the same way, with the same image as the player. To run it yourself against the server
above, start that server with `--test-extract-at-spawn` (an extraction zone wherever each player spawns), then:

```sh
docker run --rm -e RAIDZERO_BACKEND_URL=http://host.docker.internal:5080 raidzero-gameserver \
  --ci-client --ci-expect extracted
```

It logs in as a new dev player, deploys, stands still, and ends with `[ci] PASS` or `[ci] FAIL` and why. For the
other test, start the server with `--raid-minutes 0.5` instead and pass `--ci-expect missing_in_action`. (The
ticket names the server as 127.0.0.1 from the dev backend's settings, so this only works where that address
reaches the server: on Linux, add `--network host` to both containers, as CI does.)
