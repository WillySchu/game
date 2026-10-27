namespace RaidZero.Backend.Raids;

public sealed partial class RaidService
{
    /// <summary>
    /// Ends a live raid right now, exactly as if its lease had run out: everyone still inside is refunded.
    /// For the admin tool, when a game server is stuck but still heartbeating.
    /// </summary>
    public Task<ServerResponse> AbortNowAsync(string raidId, CancellationToken ct = default) =>
        _db.WriteAsync(async (c, tx) =>
        {
            var raid = await GetRaidAsync(c, tx, raidId);
            if (raid is null)
            {
                return ServerResponse.Error(404, "raid_not_found", "No such raid.");
            }

            if (!IsLive(raid.State))
            {
                return RaidNotActive(raid.State);
            }

            await AbortAsync(c, tx, raidId, Now());
            _log.LogWarning("Raid {RaidId} aborted by an admin.", raidId);
            return ServerResponse.Json(200, new { Aborted = true });
        }, ct);
}
