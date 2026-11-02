-- 0006: what each game server last said about itself while waiting for a raid (POST /v1/server/ready), so
-- tickets only go to servers that are up, have the ticket's map loaded and run the client's network code.
-- A configured server with no row here has never reported, and tickets treat it as before: idle when it has no
-- raid, whatever the map.
--
-- reported_at is cleared when the server's raid closes or is aborted: its process exits then, and it's ready
-- again only once the fresh process reports.

CREATE TABLE game_servers (
    id              TEXT PRIMARY KEY,   -- the server's id in Backend:Servers
    map_id          TEXT NOT NULL,
    build           TEXT,
    network_code    TEXT,
    catalog_version TEXT NOT NULL,
    reported_at     TIMESTAMP
);
