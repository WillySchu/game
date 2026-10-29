# Running it on your own machine

`compose.local.yaml` runs the backend and one dedicated game server on a machine you already have, for
playtests. With [Tailscale](https://tailscale.com) on each player's machine, everyone reaches it over a private,
encrypted network: no router ports to open, no domain, no HTTPS to set up. The machine running it can play too.

```
your Mac, John's PC ──Tailscale──▶ the server PC: backend :5080, game server :7770/udp (Docker)
the server PC itself ─────────────▶ same, through its own Tailscale address
```

It needs an x86 machine (Unity's Linux server build is x86-64 only) with Docker Desktop. The steps below assume
Windows; on Linux, use the shell equivalents. It's only up while that machine is on, and dev login is on, so
don't expose it to the internet.

## Set up (once)

1. **Install Docker Desktop** on the server PC, with its default WSL 2 backend and Linux containers.
2. **Install Tailscale** on the server PC and on every machine that will play, and sign them all in to the same
   tailnet. Invite the others from the Tailscale admin console (or share just the server PC with them).
3. **Get the two files** from this folder onto the server PC, `compose.local.yaml` and `.env.local.example`,
   into a folder of their own (cloning the repo works too). Then, in PowerShell in that folder:

   ```powershell
   copy .env.local.example .env.local
   notepad .env.local
   ```

   - For each of the three secrets, run this once and paste the result:
     `$b = New-Object byte[] 32; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); [Convert]::ToBase64String($b)`
   - `GAME_SERVER_HOST`: this PC's Tailscale address, from `tailscale ip -4` (it starts with `100.`).
   - The image names are already right. They need the two GHCR packages to be public (each package's settings
     on GitHub). If they aren't, run `docker login ghcr.io` with a GitHub token that has `read:packages` first.

4. **Let the other machines in through Windows Firewall.** In PowerShell as administrator; this allows the two
   ports only from Tailscale addresses:

   ```powershell
   New-NetFirewallRule -DisplayName "Raid Zero backend" -Direction Inbound -Protocol TCP -LocalPort 5080 -RemoteAddress 100.64.0.0/10 -Action Allow
   New-NetFirewallRule -DisplayName "Raid Zero game server" -Direction Inbound -Protocol UDP -LocalPort 7770 -RemoteAddress 100.64.0.0/10 -Action Allow
   ```

   If Docker Desktop also asks whether to allow access, allow it on private networks.

## Start it

```powershell
docker compose -f compose.local.yaml --env-file .env.local up -d
docker compose -f compose.local.yaml --env-file .env.local logs -f gameserver
```

Wait for `[raid] Server ready: ..., map greybox_01, ...`, then press Ctrl+C (the servers keep running). From
any machine on the tailnet, `curl http://<tailscale address>:5080/healthz` should answer with the catalog version.

## Play

On every machine that plays, including the server PC if you play there:

1. In the Unity project, set `backend_url` in `unity/RaidZero/UserSettings/RaidZeroDev.json` to
   `http://<the server PC's tailscale address>:5080`. With a client build from CI instead of the editor, open
   the F1 overlay, type that address in the field above Deploy and press **Use this backend** (it's remembered).
2. Press Play (or start the client build), open the debug overlay with F1, and press **Deploy**. Don't use the
   Server or Client buttons: the dedicated server is the server.

Everyone who deploys within two minutes of the first player lands in the same raid (up to four players). When
the raid ends, the server restarts for the next one, which takes a few seconds.

## Day to day

Each of these runs in the folder with `compose.local.yaml`; they all start with
`docker compose -f compose.local.yaml --env-file .env.local`.

| Task | Command (after the prefix above) |
| --- | --- |
| Update to the newest images | `pull`, then `up -d` |
| Watch the game server or backend | `logs -f gameserver`, `logs -f backend` |
| Stop it | `down` (players, stashes and undelivered raid results stay in Docker volumes) |
| Start over from scratch | `down -v` (deletes every player and item) |
| Shorter raids | Set `RAID_MINUTES` in `.env.local`, then `up -d` |

The admin tool is at `http://<tailscale address>:5080/admin`, with `ADMIN_TOKEN` from `.env.local`.

## When something's wrong

| What you see | Why, and what to do |
| --- | --- |
| Deploy says `no_server_available` | The game server is busy with a raid that started more than two minutes ago, or that's full (four players, counting ones who disconnected and haven't come back). Wait for it to end; shorter `RAID_MINUTES` helps while testing |
| Deploy says `rejoining your raid` | You'd disconnected from a raid that's still going, so Deploy puts you back in it with the same ticket |
| Deploy says `the server refused: version_mismatch` | Your editor's networked code (RPCs, prediction methods, synced fields) differs from the server's, such as unpushed work on the player controller. Everyone needs the same code as the server: `git pull` (or stash local changes while playtesting), and if the change is new, push it, wait for CI's game server build, then `pull` and `up -d` on the server PC. The server's log shows its build and network code at startup; the editor's overlay shows its network code next to Deploy |
| Deploy hangs at "connecting", then times out | The client can't reach UDP 7770: check `GAME_SERVER_HOST` is the Tailscale address and the firewall rule exists |
| Deploy fails reaching the backend | Check `backend_url`, the TCP 5080 rule, and that Tailscale is connected on both machines |
| `catalog_mismatch` in the game server's log | The two images were built from different item catalogs. `pull` both, then `up -d` |
| The game server's log complains about binding a socket | Possibly IPv6 being unavailable inside Docker. The fix is turning off the transport's IPv6 setting in the Unity project |

## On one machine, without Tailscale

To try the dedicated server alone on the server PC: set `GAME_SERVER_HOST=127.0.0.1`, skip Tailscale and the
firewall rules, and use `backend_url` `http://localhost:5080`.
