using System.Text.Json;
using Dapper;
using RaidZero.Backend.Data;
using RaidZero.Shared.Inventory;

namespace RaidZero.Backend.Raids;

public sealed partial class RaidService
{
    public sealed record JoinRequest(string? JoinToken);

    public sealed record JoinResponse(string PlayerId, string DisplayName, ItemNode Loadout);

    public sealed record HeartbeatResponse(DateTimeOffset LeaseExpiresAt);

    /// <summary>
    /// POST /v1/server/raids/{raid_id}/join. The game server redeems a player's join token when they connect,
    /// and this is when their gear actually leaves home. In one transaction: everything under the character
    /// moves into the raid's pool, the loadout is snapshotted for refunds, and the raid becomes active.
    /// </summary>
    public Task<ServerResponse> JoinAsync(string serverId, string raidId, string idempotencyKey, string requestHash, JoinRequest request, CancellationToken ct = default) =>
        Idempotency.RunAsync(_db, serverId, idempotencyKey, requestHash, _time, async (c, tx) =>
        {
            var nowValue = _time.GetUtcNow();
            var now = Timestamps.Format(nowValue);

            var raid = await GetRaidAsync(c, tx, raidId);
            if (raid is null || raid.ServerId != serverId)
            {
                return RaidNotFound();
            }

            if (!IsLive(raid.State))
            {
                return RaidNotActive(raid.State);
            }

            if (string.IsNullOrEmpty(request.JoinToken))
            {
                return ServerResponse.Error(422, "invalid_request", "join_token is required.");
            }

            var participant = (await c.QueryAsync<ParticipantRow>(
                $"SELECT {ParticipantRow.Columns} FROM raid_participants WHERE raid_id = @raidId AND join_token_hash = @hash",
                new { raidId, hash = HashJoinToken(request.JoinToken) }, transaction: tx)).FirstOrDefault();
            if (participant is null)
            {
                return ServerResponse.Error(404, "invalid_join_token", "That join token isn't valid for this raid.");
            }

            if (participant.State == ParticipantStates.InRaid)
            {
                return ServerResponse.Error(409, "already_joined", "This player already joined the raid.");
            }

            if (participant.State != ParticipantStates.Ticketed || string.CompareOrdinal(participant.TicketExpiresAt, now) <= 0)
            {
                return ServerResponse.Error(410, "ticket_expired", "The ticket expired before the player connected. They need a new one.");
            }

            var player = (await GetPlayerAsync(c, tx, participant.PlayerId))!;
            var items = await ItemRows.LoadStashItemsAsync(c, tx, player.Id);
            var character = items[player.CharacterRootId];
            var gear = character.Walk().Skip(1).Select(n => n.Id).ToList();
            var topLevel = character.Children.Select(n => n.Id).ToList();

            if (gear.Count > 0)
            {
                var moved = await c.ExecuteAsync(
                    "UPDATE item_instances SET state = 'in_raid', owner_id = NULL, raid_id = @raidId, updated_at = @now WHERE id IN @gear AND owner_id = @playerId AND state = 'stash'",
                    new { raidId, now, gear, playerId = player.Id }, transaction: tx);
                if (moved != gear.Count)
                {
                    throw new InvalidOperationException($"Checkout for {player.Id} moved {moved} of {gear.Count} items.");
                }

                // Unhook the top-level items from the character, which stays home. The snapshot keeps their positions.
                await c.ExecuteAsync(
                    "UPDATE item_instances SET parent_id = NULL, slot = NULL, grid_x = NULL, grid_y = NULL WHERE id IN @topLevel",
                    new { topLevel }, transaction: tx);
            }

            await c.ExecuteAsync(
                "UPDATE raid_participants SET state = 'in_raid', joined_at = @now, loadout_snapshot = @snapshot WHERE raid_id = @raidId AND player_id = @playerId",
                new { now, snapshot = JsonSerializer.Serialize(character, JsonDefaults.Options), raidId, playerId = player.Id }, transaction: tx);

            // From the first join on, the server owns the raid and its lease runs on heartbeats. The longer lease
            // a pending raid gets from its ticket window no longer applies: a crash now must be noticed promptly.
            await c.ExecuteAsync(
                "UPDATE raids SET state = 'active', started_at = COALESCE(started_at, @now), lease_expires_at = @lease WHERE id = @raidId",
                new { now, lease = Timestamps.Format(nowValue.AddSeconds(_options.LeaseSeconds)), raidId }, transaction: tx);

            await BumpProfileAsync(c, tx, player.Id);
            await LedgerAsync(c, tx, player.Id, raidId, "checkout", new { Items = gear }, now);

            return ServerResponse.Json(200, new JoinResponse(player.Id, player.DisplayName, character));
        }, ct);

    /// <summary>
    /// POST /v1/server/raids/{raid_id}/heartbeat. Pushes the lease out; a raid whose server stops sending
    /// these is aborted and its players refunded. Needs no idempotency key, since repeating it is harmless.
    /// </summary>
    public Task<ServerResponse> HeartbeatAsync(string serverId, string raidId, CancellationToken ct = default) =>
        _db.WriteAsync(async (c, tx) =>
        {
            var raid = await GetRaidAsync(c, tx, raidId);
            if (raid is null || raid.ServerId != serverId)
            {
                return RaidNotFound();
            }

            if (!IsLive(raid.State))
            {
                return RaidNotActive(raid.State);
            }

            var lease = _time.GetUtcNow().AddSeconds(_options.LeaseSeconds);
            await c.ExecuteAsync(
                "UPDATE raids SET lease_expires_at = @lease WHERE id = @raidId AND lease_expires_at < @lease",
                new { lease = Timestamps.Format(lease), raidId }, transaction: tx);
            return ServerResponse.Json(200, new HeartbeatResponse(lease));
        }, ct);
}
