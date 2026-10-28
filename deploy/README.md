# Deploying to one VPS

This runs the backend container behind [Caddy](https://caddyserver.com), which gets and renews an HTTPS
certificate on its own, plus one dedicated game server next to it. The SQLite database lives in a Docker volume.
It's enough for playtests and early access; moving to Postgres and several servers comes later.

```
players ──HTTPS──▶ Caddy :443 ──▶ backend :8080 ──▶ /data/raidzero.db (volume)
players ──UDP 7770──▶ game server ──HTTP──▶ backend :8080 (Compose network, with its server token)
```

The game server runs one raid at a time. When a raid closes, it exits and Docker starts a fresh one.

For playtests on a machine you already have, over Tailscale and with no VPS, see [LOCAL.md](LOCAL.md) instead.

## What you need

- A small Linux VPS (1 vCPU, 1 GB RAM is plenty), x86 or ARM, with Docker and the Compose plugin installed.
- A DNS name, such as `api.yourgame.com`, with an A record pointing at the server.
- Ports 80 and 443 (TCP) open (Caddy needs 80 for the certificate challenge), and the game server's port,
  7770 by default, open for UDP. Nothing else. With ufw: `ufw allow 80,443/tcp && ufw allow 7770/udp`.
- An x86 server. The backend image also runs on ARM, but Unity's Linux server build is x86-64 only.
- The two images, which CI pushes to GitHub Container Registry from `main`: `ghcr.io/<owner>/<repo>/backend` on
  every push, and `ghcr.io/<owner>/<repo>/gameserver` whenever the Unity project or shared code changes (it needs
  the Unity secrets below). New packages start out private on GHCR even in a public repository: either make each
  one public in its package settings on GitHub, or log the server in once with a GitHub token that has
  `read:packages`: `echo <token> | docker login ghcr.io -u <github-user> --password-stdin`

### Unity secrets for CI

Building the game server needs a Unity license in GitHub Actions, as three repository secrets (**Settings →
Secrets and variables → Actions**):

| Secret | What |
| --- | --- |
| `UNITY_EMAIL` | The Unity account's email. The account must not use two-factor sign-in |
| `UNITY_PASSWORD` | Its password |
| `UNITY_LICENSE` | The whole contents of the license file Unity Hub saved when that account activated its Personal license: `/Library/Application Support/Unity/Unity_lic.ulf` on a Mac, `C:\ProgramData\Unity\Unity_lic.ulf` on Windows |

The build uses [GameCI](https://game.ci/docs/github/activation)'s builder, which activates that license for the
build and returns it afterwards. If you change Unity versions and activation starts failing, copy the license
file again. Without these secrets the game server workflow skips the build with a warning, and nothing else
breaks.

## First deploy

```sh
# On the server
mkdir raidzero && cd raidzero
# copy compose.yaml, Caddyfile and .env.example from this folder into it
cp .env.example .env
nano .env                      # fill in every value; generate secrets with: openssl rand -base64 32
docker compose up -d
docker compose logs -f backend # wait for "Ready: ..." and "Admin tool is on at /admin."
curl https://api.yourgame.com/healthz
```

Compose refuses to start if a required value is missing from `.env`, and names the one it wants.

In production, dev login doesn't exist: `/v1/auth/dev-login` returns 404. Playtesters need real accounts first
(Steam login is on the roadmap). Until then, for a private playtest, you can set
`Backend__DevLoginEnabled: "true"` in `compose.yaml`. Anyone who can reach the server can then log in as anyone,
so turn it off again afterwards.

## The game server

Compose runs it with everything it needs: the backend's address on the Compose network, and `GAME_SERVER_TOKEN`
from `.env`, which the backend also has. `GAME_SERVER_HOST` and `GAME_SERVER_PORT` are what the backend tells
players' clients to connect to, so they must be this machine's public address and the published UDP port.

```sh
docker compose logs -f gameserver   # wait for "[raid] Server ready: ..., map greybox_01, catalog ..."
```

- **The catalog must match.** The server sends the catalog it was built with on every call, and the backend
  refuses a different one (`catalog_mismatch` in the server's log), so run images built from the same catalog.
  Using `latest` for both, after both CI workflows for a commit have finished, does that.
- **Raid results survive restarts.** The server writes each result to the `gameserver-data` volume before sending
  it, and a fresh server sends anything the last one didn't get answered.
- **Updating mid-raid refunds the raid.** Stopping the server mid-raid stops its heartbeats, and the backend aborts
  the raid after a minute and gives everyone inside their gear back. Update between raids if you can:
  `docker compose logs gameserver` shows when one ends.
- **Raid length.** 25 minutes. For a playtest, add `command: ["--raid-minutes", "10"]` to the `gameserver`
  service.

### Playing against it

Until Steam login exists, playtesters use dev login, which is off in production. For a private playtest, set
`Backend__DevLoginEnabled: "true"` on the `backend` service, and turn it off again afterwards: anyone who can
reach the server can log in as anyone while it's on.

Then, in the Unity editor, point `UserSettings/RaidZeroDev.json`'s `backend_url` at `https://api.yourgame.com`,
press Play, open the debug overlay with F1, and press **Deploy**. That logs in, gets a ticket, and connects to
the server the ticket names, which is the one on the VPS.

## Updating

```sh
docker compose pull && docker compose up -d
```

See the game server section about updating between raids.

Migrations run automatically at startup, and the backend refuses to start if something's wrong. To control exactly
what runs, set `BACKEND_IMAGE` to a `sha-xxxxxxx` tag instead of `latest`; rolling back is then a matter of
setting the previous tag. Take a backup before any update that adds a migration, since migrations can't be undone.

## Backups

The admin tool takes consistent backups while the backend keeps serving: **Backups → Back up now** at
`https://api.yourgame.com/admin`, or from cron. A nightly backup, at 04:10 server time:

```sh
# crontab -e
10 4 * * * curl -fsS -X POST https://api.yourgame.com/v1/admin/backups -H "Authorization: Bearer <ADMIN_TOKEN>" > /dev/null
```

Backups land in `/data/backups` inside the volume, and the newest 14 are kept (`Backend__BackupsToKeep`). A backup on
the same disk doesn't survive losing the server, so copy them off it too:

```sh
docker compose cp backend:/data/backups ./backups   # then rsync or rclone ./backups somewhere else
```

### Restoring

```sh
docker compose stop backend
docker compose cp ./backups/raidzero-YYYYMMDD-HHMMSS-fff.db backend:/data/raidzero.db
docker compose run --rm --user root --entrypoint sh backend -c \
  'rm -f /data/raidzero.db-wal /data/raidzero.db-shm && chown "$APP_UID" /data/raidzero.db'
docker compose start backend
```

Both parts of the middle step matter. The `-wal` and `-shm` files belong to the old database, and SQLite would try
to apply them to the restored one. And `docker compose cp` creates files owned by root, while the backend runs as
the image's non-root user, so the restored file needs handing back to it.

## Day to day

| Task | How |
| --- | --- |
| Logs | `docker compose logs -f backend`, `docker compose logs -f gameserver` |
| Is it up? | `curl https://api.yourgame.com/healthz` |
| Look up a player, an item's history, a stuck raid | `https://api.yourgame.com/admin`, with `ADMIN_TOKEN` |
| Rotate a secret | Change it in `.env`, then `docker compose up -d`. A new `TOKEN_SIGNING_KEY` logs every player out; a new `GAME_SERVER_TOKEN` needs the game server updated too |
