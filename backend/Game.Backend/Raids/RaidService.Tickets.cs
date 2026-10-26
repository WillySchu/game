using System.Buffers.Text;
using System.Security.Cryptography;
using Dapper;
using Microsoft.Data.Sqlite;
using RaidZero.Backend.Data;
using RaidZero.Shared.Ids;

namespace RaidZero.Backend.Raids;

public sealed partial class RaidService
{
    public sealed record ServerAddress(string Host, int Port);

    public sealed record TicketResponse(string RaidId, ServerAddress Server, string JoinToken, DateTimeOffset ExpiresAt);

    /// <summary>
    /// POST /v1/me/raid-tickets. Reserves a place in a raid; no gear moves until the game server confirms the
    /// player connected (<see cref="JoinAsync"/>). A ticket nobody redeems simply expires.
    ///
    /// M1 matchmaking: each configured server runs one raid at a time. A ticket joins that server's open raid
    /// when the map matches and there's room (which is how co-op and looting another player's body get tested
    /// on one dev server), and otherwise opens a new raid on the first idle server.
    /// </summary>
    public Task<ServerResponse> CreateTicketAsync(string playerId, string? mapId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(mapId) || mapId.Length > 64 || !mapId.All(ch => char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch) || ch == '_'))
        {
            return Task.FromResult(ServerResponse.Error(422, "invalid_request", "map_id must be lowercase letters, digits and underscores."));
        }

        return _db.WriteAsync(async (c, tx) =>
        {
            var nowValue = _time.GetUtcNow();
            var now = Timestamps.Format(nowValue);

            if (await GetPlayerAsync(c, tx, playerId) is null)
            {
                return ServerResponse.Error(404, "player_not_found", "The player in this token no longer exists.");
            }

            var live = await LiveRaidIdAsync(c, tx, playerId, now);
            if (live is not null)
            {
                return ServerResponse.Error(409, "already_in_raid", "Finish or wait out your current raid first.", new { RaidId = live });
            }

            // Tickets that ran out still hold a row under the one-live-raid index until they're marked expired.
            await c.ExecuteAsync(
                "UPDATE raid_participants SET state = 'expired', resolved_at = @now WHERE player_id = @playerId AND state = 'ticketed'",
                new { now, playerId }, transaction: tx);

            var expires = nowValue.AddSeconds(_options.TicketSeconds);
            var target = await FindOrOpenRaidAsync(c, tx, playerId, mapId, now, Timestamps.Format(expires.AddSeconds(_options.LeaseSeconds)));
            if (target is null)
            {
                return ServerResponse.Error(503, "no_server_available", "Every game server is busy with another raid. Try again shortly.");
            }

            var (raidId, server, rejoining) = target.Value;
            var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
            var ticket = new { raidId, playerId, hash = HashJoinToken(token), expires = Timestamps.Format(expires) };

            if (rejoining)
            {
                await c.ExecuteAsync(
                    """
                    UPDATE raid_participants
                    SET state = 'ticketed', join_token_hash = @hash, ticket_expires_at = @expires,
                        loadout_snapshot = NULL, joined_at = NULL, resolved_at = NULL
                    WHERE raid_id = @raidId AND player_id = @playerId
                    """,
                    ticket, transaction: tx);
            }
            else
            {
                await c.ExecuteAsync(
                    "INSERT INTO raid_participants (raid_id, player_id, state, join_token_hash, ticket_expires_at) VALUES (@raidId, @playerId, 'ticketed', @hash, @expires)",
                    ticket, transaction: tx);
            }

            // A raid nobody has joined yet lives only as long as its tickets, plus the usual lease.
            await c.ExecuteAsync(
                "UPDATE raids SET lease_expires_at = @lease WHERE id = @raidId AND state = 'pending' AND lease_expires_at < @lease",
                new { raidId, lease = Timestamps.Format(expires.AddSeconds(_options.LeaseSeconds)) }, transaction: tx);

            return ServerResponse.Json(200, new TicketResponse(raidId, new ServerAddress(server.Host, server.Port), token, expires));
        }, ct);
    }

    private async Task<(string RaidId, GameServerOptions Server, bool Rejoining)?> FindOrOpenRaidAsync(
        SqliteConnection c, SqliteTransaction tx, string playerId, string mapId, string now, string newRaidLease)
    {
        foreach (var server in _servers.All)
        {
            var open = (await c.QueryAsync<RaidRow>(
                """
                SELECT id AS Id, map_id AS MapId, server_id AS ServerId, state AS State, lease_expires_at AS LeaseExpiresAt
                FROM raids WHERE server_id = @serverId AND state IN ('pending', 'active') ORDER BY created_at DESC
                """,
                new { serverId = server.Id }, transaction: tx)).FirstOrDefault();

            if (open is null)
            {
                var raidId = Uuid7.NewId();
                await c.ExecuteAsync(
                    "INSERT INTO raids (id, map_id, server_id, state, lease_expires_at, created_at) VALUES (@raidId, @mapId, @serverId, 'pending', @newRaidLease, @now)",
                    new { raidId, mapId, serverId = server.Id, newRaidLease, now }, transaction: tx);
                return (raidId, server, false);
            }

            if (open.MapId != mapId)
            {
                continue;
            }

            // Someone who already played this raid (died or extracted) can't drop back into it.
            var previous = await GetParticipantAsync(c, tx, open.Id, playerId);
            if (previous is not null && previous.State != ParticipantStates.Expired)
            {
                continue;
            }

            var players = await c.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM raid_participants WHERE raid_id = @raidId AND (state = 'in_raid' OR (state = 'ticketed' AND ticket_expires_at > @now))",
                new { raidId = open.Id, now }, transaction: tx);
            if (players >= _options.MaxPlayersPerRaid)
            {
                continue;
            }

            return (open.Id, server, previous is not null);
        }

        return null;
    }
}
