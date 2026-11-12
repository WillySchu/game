# Testing Instructions

To test the client using Multiplayer Play Mode:

1. Open the `core` scene: every game starts there, and it loads the rest.
2. Open the Multiplayer Play Mode menu with `Window > Multiplayer > Multiplayer Play Mode`, and check the boxes
   to activate extra players.
3. Before pressing Play, pick each player's multiplayer role in the role dropdown on its toolbar (next to Play
   in the main editor, and at the top of each extra player's window):
   - **Client and Server** for the one that hosts: it starts the raid server when the map loads, and you play
     on it too.
   - **Client** for each other player.
   - **Server** for a server with nobody playing on it.
4. Press Play. The console says `[core] Starting as ...` with the role each one took. Every player starts in
   the Hideout: log in and Deploy, host first.

The editor loads scenes from the active build profile's scene list, or the shared list when no profile is
active (File > Build Profiles). Play with no profile active (the plain platform, using the shared list, which has
all four scenes), or with one that lists `core`, `core.client`, `core.server` and `SampleScene`, like John's
`Windows` profile. The Client and Server profiles list only their own half, so under the macOS Client profile a
host stops with "Scene 'core.server' couldn't be loaded".

Only one player can host: two set to Client and Server both try to listen on port 7770, and the second can't.
The role used to come from Server and Client buttons on screen; it's the toolbar dropdown now, and in a build
it's the build's profile (`BUILD_CLIENT`, `BUILD_SERVER`). The editor compiles both halves whatever build
profile is active.

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
  startup. Builds never simulate, whatever the box says, unless started with `--simulate-lag`: that goes for client
  builds and the dedicated server alike. Each editor instance simulates on its own, so with two MPPM players (one
  the server) the delay is doubled, while a host (Client and Server) playing simulates it once.
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

- **Tab** opens and closes the screen. The mouse is freed and the player's input is turned off, so the camera
  stays still; closing puts both back and closes anything you had open. After a raid, the Hideout must still
  take clicks: if it doesn't, something has turned off the input actions' UI map (see `InventoryView.EnsureUiInput`).
- **F** uses what you're looking at: it opens a crate (and the screen with it) or takes an item off the ground.
  The prompt under the middle of the screen says which. See "Items on the ground" below.
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
   names, as the F1 overlay does. The Hideout hides once the connection starts. Start a server first (a player
   whose role is Client and Server or Server, or a dedicated server), or you'll get "No raid server is free" or
   a connection that comes straight back.
4. **Back from the raid:** extract (or wait out the clock). The Hideout comes back, says it's waiting for the
   result, then shows it: Extracted / Lost in the zone / Raid called off, what came home and what was lost.
   OK dismisses it for good; a result you never dismissed shows again at the next login.
5. **Still in a raid:** if you leave a raid by quitting, the player bar says your gear is in a raid and the button
   says Rejoin (it works while that raid runs and this game remembers the ticket).

Scene flow can take over Deploy: anything listening to `HideoutScreen.DeployRequested` gets the map ID and the
screen doesn't connect by itself. Untick **Show When Not In A Raid** to show and hide it from code instead.

### Trader

The backend needs the trader endpoints (the backend from the "Traders" commit or later). Prices and stock are
in `backend/Game.Backend/traders.json`; item values are each item asset's **Value**.

1. **Open it:** the player bar shows your money; **Trader** opens the General Store as a third column (the button
   then closes it). Its header shows your money too.
2. **Buy:** each offer shows its picture, what comes with it ("with PM magazine"), how many are left, and the
   price. Stacks and limited offers get − and +. The price goes red when you can't afford it; sold-out offers
   dim and say when more come. **Buy** puts it in the first free spot in your stash (the status line says so),
   takes the money, and the stock left goes down. Try buying more pistols than the stock (2): the third is refused
   with how long until more.
3. **Sell:** drag an item onto the Sell list (it switches tabs by itself on right-click; dragging needs the Sell
   tab showing). Its border goes green, or red with the reason in the status line for money, an item with money
   in it, or something the trader doesn't buy. Right-click an item to list it, and again to take it off. Listed
   items stay where they are, with a gold border; the list shows each one's price and the total. Listing a rifle
   takes its magazine off the list if it was on it (it sells with the rifle). **Sell for ...** sells them all;
   the money tops up your money stack.
4. **Tooltips:** while the trader is open, an item's tooltip ends with what the trader pays for it, contents
   included, less for worn gear.
5. **Locked in a raid:** with your gear in a raid, Buy and Sell are off.
6. **Stock is per player:** log in under another name and the trader shows that player's stock.

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

### Models from pictures

**Raid Zero > Items > Model From Picture** (or **Model From Picture** in an item asset's ⋮ menu) makes a quick
model from a side-on picture, the trace-and-extrude way.

1. Import a side view of the item into the project: lengthwise, left to right, on a transparent or plain
   background. A PNG or JPG is read straight from the file, whatever its import settings. Use your own photos or
   ones whose licence allows it: the repository is public.
2. Choose it as the **Picture**, and an **Item** if the model is for one. Set **Length** to the real length end
   to end (an AR-15 is about 0.9 m) and **Thickness** (about 0.05 m for a rifle).
3. The preview shows the cutout with the traced outline in orange. If background is left on, raise **Colour
   tolerance**; if parts of the item go missing, lower it. If a pale part of the item is cut out as a hole,
   untick **Cut enclosed holes**. **Outline detail** trades accuracy for triangles: 1 to 2 is plenty for a gun.
4. Optional: paint a **Thickness map**, a copy of the picture in greys: white for the thickest parts, black for
   the thinnest (**Thinnest**, as a share of Thickness), grey between. A thin magazine and grip on a thick
   receiver reads much better than one slab.
5. **Make Model** writes the mesh, texture, material and prefab into `Assets/_Project/Models/FromPictures/<name>`.
   With an item chosen, it becomes the item's **World Prefab**, its **Icon Angles** are reset (the model already
   faces the icon's way) and its icon renders. Check the icon on the inventory screen.
6. Drop the item in a raid: it lies on the ground on its side, the picture facing up.
7. Change a setting and **Make Model** again with the same name: the files update in place.

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
then is missing in action, and the server stops. In the editor (or any game that isn't a dedicated server) it
starts again a second later with a fresh raid, so you can Deploy again without leaving Play mode.

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

**Start as server**, above Deploy, makes this game the raid's server again after **Stop server**. It's only
there in a game that can be one: a player whose role includes Server, or a server build started with
`--server-token` (the editor takes the token from `RaidZeroDev.json`). A host starts its server on its own when
the map loads. While it runs, the overlay says so and has **Stop server**, which ends the
raid properly. Press Deploy after starting it to play on that server from the same game (host mode): against
the local backend, the ticket names this machine.

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

## Items on the ground

Dropping (drag an item out of the inventory window) and loose loot both put items on the ground, where anyone
can take them with F.

To have loose loot to try, add an empty GameObject to `SampleScene` where an item should lie (on a table, the
floor) and give it a `LooseLootSpot` (assembly `Game.Net`). Either set **Fixed Def Id** to an item ID such as
`ak74` for the same item every raid, or give it a **Loot Table** and a **Chance**. The item lands on the first
surface below the spot. Keep spots' names unique, like crates.

What to check, with two players (a host and an MPPM clone):

1. At raid start, the server's log line `[raid] Server ready: ...` ends with how many loose items it placed, and
   each spot's item is lying there for both players. Items without a world model show as a flat box the size of
   their inventory footprint; items with one lie on their side, the side the icon shows facing up.
2. Look at one from within about 2.5 m: the prompt reads "F  Take <item>". Press F: it lands on you (a rifle in
   an empty primary slot, otherwise your rig, then your backpack) and disappears for both players.
3. Open the inventory and drag something out of the window: it lands about 0.75 m in front of you, on the
   ground, and the other player sees it too. Drop a few in a row: they land side by side, not inside each
   other. Facing a wall, it lands at the foot of the wall.
4. Fill your rig and backpack, then press F on a rifle while your primary slot is taken: the inventory opens
   with the item's pile on the right and says there's no room. Make room and drag it across.
5. Walk out of reach while looking at an item: the prompt goes. Crates show "F  Open Wooden crate" the same
   way, and a wall between you and a crate or item hides the prompt.
6. A player who joins after items were dropped sees them where they lie.
7. With `--loot-seed` (or Loot Seed on `RaidServer`) the same spots get the same items again.

## Bodies

Nothing deals damage yet, so open the F1 overlay in a raid and press **Die**. It only works in the editor and
Development builds.

1. The player who died sees the "Killed" card and goes back to the Hideout, where the result says their gear
   stayed in the zone.
2. Where they stood, the other player finds a body lying down (a long grey box for now). Looking at it within
   reach shows "F  Search body"; F opens it in the inventory screen with the dead player's slots, rig and
   backpack, and everything in them can be taken, dragged or Ctrl+clicked across.
3. The body stays after it's emptied, and until the raid ends.
4. A player who joins after the death sees the body too.

## A dedicated server build

CI builds the dedicated server on every change to the Unity project (`.github/workflows/server.yml`). To build
one yourself, install the **Linux Dedicated Server Build Support** module in Unity Hub, pick the project's
**Linux Server** profile in the Build Profiles window (listed under Build Profiles, not the Linux platform
entry: the profile is what sets `BUILD_SERVER` and the server's scenes), and build to `build/StandaloneLinux64`
at the repo root under the name `RaidZeroServer`. Client builds work the same way from the **Windows Client**,
**macOS Client** and **Linux Client** profiles, which set `BUILD_CLIENT`. Then, from the repo root, with the backend running:

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
