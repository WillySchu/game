# Deploying the backend to one VPS

This runs the backend container behind [Caddy](https://caddyserver.com), which gets and renews an HTTPS
certificate on its own. The SQLite database lives in a Docker volume. It's enough for playtests and early
access; moving to Postgres and several servers comes later.

```
players ──HTTPS──▶ Caddy :443 ──▶ backend :8080 ──▶ /data/raidzero.db (volume)
game server ──HTTPS──▶ same, with its server token
```

## What you need

- A small Linux VPS (1 vCPU, 1 GB RAM is plenty) with Docker and the Compose plugin installed.
- A DNS name, such as `api.yourgame.com`, with an A record pointing at the server.
- Ports 80 and 443 open (Caddy needs 80 for the certificate challenge), plus the game server's UDP port if it
  runs on the same machine. Nothing else.
- The backend image. CI pushes one to `ghcr.io/<owner>/<repo>/backend` on every push to `main`. If the repository
  is private, log the server in once with a GitHub token that has `read:packages`:
  `echo <token> | docker login ghcr.io -u <github-user> --password-stdin`

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

Configure the game server with:

- the backend URL, `https://api.yourgame.com`;
- `GAME_SERVER_TOKEN` from `.env`, sent as `Authorization: Bearer <token>`;
- the catalog it was built with, sent as `X-Catalog-Version`. It must match the backend's, so deploy both from the
  same commit.

`GAME_SERVER_HOST` and `GAME_SERVER_PORT` are what the backend tells players' clients to connect to, so they must be
the game server's public address.

## Updating

```sh
docker compose pull && docker compose up -d
```

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
| Logs | `docker compose logs -f backend` |
| Is it up? | `curl https://api.yourgame.com/healthz` |
| Look up a player, an item's history, a stuck raid | `https://api.yourgame.com/admin`, with `ADMIN_TOKEN` |
| Rotate a secret | Change it in `.env`, then `docker compose up -d`. A new `TOKEN_SIGNING_KEY` logs every player out; a new `GAME_SERVER_TOKEN` needs the game server updated too |
