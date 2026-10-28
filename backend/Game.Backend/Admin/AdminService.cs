using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using RaidZero.Backend.Data;
using RaidZero.Backend.Players;
using RaidZero.Backend.Raids;
using RaidZero.Shared.Catalog;
using RaidZero.Shared.Ids;
using RaidZero.Shared.Inventory;

namespace RaidZero.Backend.Admin;

/// <summary>
/// What the admin tool can see and do. Built for playtests: answering "where did my rifle go?" from the
/// ledger, handing out gear, unsticking a raid, and taking a backup. Everything it changes is ledgered.
/// </summary>
public sealed class AdminService
{
    public const int MaxGrantItems = 50;

    private readonly Database _db;
    private readonly ItemCatalog _catalog;
    private readonly PlayerStore _players;
    private readonly RaidService _raids;
    private readonly TimeProvider _time;
    private readonly int _backupsToKeep;

    public AdminService(Database db, ItemCatalog catalog, PlayerStore players, RaidService raids, TimeProvider time, IOptions<BackendOptions> options)
    {
        _db = db;
        _catalog = catalog;
        _players = players;
        _raids = raids;
        _time = time;
        _backupsToKeep = Math.Max(1, options.Value.BackupsToKeep);
    }

    public sealed record PlayerSummary(string Id, string DisplayName, long ProfileVersion, string CreatedAt, string? LiveRaidId);

    public sealed record LedgerEntry(long Id, string? PlayerId, string? RaidId, string Kind, JsonElement Detail, string CreatedAt);

    public sealed record PlayerDetail(PlayerSummary Player, ItemNode Stash, ItemNode Character, IReadOnlyList<LedgerEntry> Ledger);

    public sealed record GrantItem(string? DefId, int Quantity = 1);

    public sealed record GrantRequest(List<GrantItem>? Items, string? Note);

    public sealed record GrantedItem(string ItemId, string DefId, int Quantity, int X, int Y);

    public sealed record CatalogEntry(string DefId, string? DisplayName, int MaxStack, int Width, int Height);

    public IReadOnlyList<CatalogEntry> Catalog() =>
        _catalog.All
            .Where(d => d.DefId is not (WellKnownDefs.Stash or WellKnownDefs.Character))
            .OrderBy(d => d.DefId, StringComparer.Ordinal)
            .Select(d => new CatalogEntry(d.DefId, d.DisplayName, d.MaxStack, d.Width, d.Height))
            .ToList();

    /// <summary>Players whose name contains the text, or whose ID is exactly it; newest first, at most 50.</summary>
    public Task<IReadOnlyList<PlayerSummary>> SearchPlayersAsync(string? query, CancellationToken ct = default) =>
        _db.ReadAsync<IReadOnlyList<PlayerSummary>>(async c =>
            (await c.QueryAsync<PlayerRow>(
                $"""
                SELECT {PlayerRow.Columns} FROM players
                WHERE @query = '' OR id = @query OR display_name LIKE '%' || @query || '%' ESCAPE '\'
                ORDER BY created_at DESC LIMIT 50
                """,
                new { query = EscapeLike(query ?? "") })).Select(r => r.ToSummary()).ToList(), ct);

    public Task<PlayerDetail?> GetPlayerAsync(string playerId, CancellationToken ct = default) =>
        _db.ReadAsync<PlayerDetail?>(async c =>
        {
            var row = (await c.QueryAsync<PlayerRow>($"SELECT {PlayerRow.Columns} FROM players WHERE id = @playerId", new { playerId })).FirstOrDefault();
            if (row is null)
            {
                return null;
            }

            var profile = (await _players.GetProfileAsync(playerId, ct))!;
            var ledger = await QueryLedgerAsync(c, "player_id = @playerId", new { playerId });
            return new PlayerDetail(row.ToSummary(), profile.Stash, profile.Character, ledger);
        }, ct);

    /// <summary>
    /// Puts new items in the player's stash, each in the first free spot, with origin 'admin'. All or nothing:
    /// if any item doesn't fit, nothing is granted.
    /// </summary>
    public Task<ServerResponse> GrantAsync(string playerId, GrantRequest request, CancellationToken ct = default)
    {
        var items = request.Items ?? [];
        if (items.Count is 0 or > MaxGrantItems)
        {
            return Task.FromResult(ServerResponse.Error(422, "invalid_request", $"Grant between 1 and {MaxGrantItems} items."));
        }

        foreach (var item in items)
        {
            if (item.DefId is null || item.DefId is WellKnownDefs.Stash or WellKnownDefs.Character || !_catalog.TryGet(item.DefId, out var def))
            {
                return Task.FromResult(ServerResponse.Error(422, "unknown_def", $"There's no grantable item '{item.DefId}'.", new { item.DefId }));
            }

            if (item.Quantity < 1 || item.Quantity > def.MaxStack)
            {
                return Task.FromResult(ServerResponse.Error(422, "invalid_quantity", $"{def.DefId} stacks from 1 to {def.MaxStack}.", new { item.DefId }));
            }
        }

        return _db.WriteAsync(async (c, tx) =>
        {
            var now = Timestamps.Format(_time.GetUtcNow());
            var player = (await c.QueryAsync<PlayerRow>($"SELECT {PlayerRow.Columns} FROM players WHERE id = @playerId", new { playerId }, transaction: tx)).FirstOrDefault();
            if (player is null)
            {
                return ServerResponse.Error(404, "player_not_found", "No such player.");
            }

            var liveRaid = await RaidService.LiveRaidIdAsync(c, tx, playerId, now);
            if (liveRaid is not null)
            {
                return ServerResponse.Error(409, "in_raid", "The player is in a raid; grant once it's over.", new { RaidId = liveRaid });
            }

            var stashRootId = await c.ExecuteScalarAsync<string>("SELECT stash_root_id FROM players WHERE id = @playerId", new { playerId }, transaction: tx);
            var stash = (await ItemRows.LoadStashItemsAsync(c, tx, playerId))[stashRootId!];

            var granted = new List<(ItemNode Node, string DefId)>();
            foreach (var item in items)
            {
                var def = _catalog.Get(item.DefId!);
                var node = new ItemNode { Id = Uuid7.NewId(), DefId = def.DefId, Quantity = item.Quantity, Durability = def.HasDurability ? 1.0 : null };
                if (!GridPlacement.TryFindFreeSpot(_catalog, stash, def, out var grid, out var x, out var y, out var rotated))
                {
                    return ServerResponse.Error(422, "no_space", $"The stash has no room for {def.DefId}; nothing was granted.", new { def.DefId });
                }

                (node.Slot, node.X, node.Y, node.Rotated) = (grid, x, y, rotated);
                stash.Children.Add(node);
                granted.Add((node, def.DefId));
            }

            foreach (var (node, _) in granted)
            {
                await ItemRows.InsertItemAsync(c, tx, node, stash.Id, playerId, ItemStates.Stash, "admin", now);
            }

            await c.ExecuteAsync("UPDATE players SET profile_version = profile_version + 1 WHERE id = @playerId", new { playerId }, transaction: tx);
            await c.ExecuteAsync(
                "INSERT INTO ledger_entries (player_id, kind, detail, created_at) VALUES (@playerId, 'grant', @detail, @now)",
                new { playerId, detail = JsonSerializer.Serialize(new { Reason = "admin", request.Note, Items = granted.Select(g => g.Node.Id) }, JsonDefaults.Options), now },
                transaction: tx);

            return ServerResponse.Json(200, new
            {
                ProfileVersion = player.ProfileVersion + 1,
                Granted = granted.Select(g => new GrantedItem(g.Node.Id, g.DefId, g.Node.Quantity, g.Node.X!.Value, g.Node.Y!.Value)),
            });
        }, ct);
    }

    /// <summary>One item's row and every ledger entry that mentions it: its whole life story.</summary>
    public Task<ServerResponse> GetItemAsync(string itemId, CancellationToken ct = default) =>
        _db.ReadAsync(async c =>
        {
            var item = (await c.QueryAsync<ItemRow>(
                $"SELECT {ItemRow.Columns} FROM item_instances i LEFT JOIN players p ON p.id = i.owner_id WHERE i.id = @itemId",
                new { itemId })).FirstOrDefault();
            if (item is null)
            {
                return ServerResponse.Error(404, "item_not_found", "No such item.");
            }

            // A full scan of the ledger. Fine at playtest scale; index item IDs separately if it ever isn't.
            var history = await QueryLedgerAsync(c, "detail LIKE '%' || @itemId || '%'", new { itemId }, newestFirst: false);
            return ServerResponse.Json(200, new { Item = item, History = history });
        }, ct);

    /// <summary>Raids newest first, with who's in them. Live ones only, unless asked otherwise.</summary>
    public Task<ServerResponse> ListRaidsAsync(bool liveOnly, CancellationToken ct = default) =>
        _db.ReadAsync(async c =>
        {
            var raids = (await c.QueryAsync<RaidRow>(
                $"""
                SELECT id AS Id, map_id AS MapId, server_id AS ServerId, state AS State, created_at AS CreatedAt,
                       started_at AS StartedAt, closed_at AS ClosedAt, lease_expires_at AS LeaseExpiresAt
                FROM raids {(liveOnly ? "WHERE state IN ('pending', 'active')" : "")}
                ORDER BY created_at DESC LIMIT 50
                """)).ToList();

            var ids = raids.Select(r => r.Id).ToList();
            var participants = (await c.QueryAsync<ParticipantRow>(
                """
                SELECT rp.raid_id AS RaidId, rp.player_id AS PlayerId, p.display_name AS DisplayName, rp.state AS State,
                       rp.joined_at AS JoinedAt, rp.resolved_at AS ResolvedAt
                FROM raid_participants rp JOIN players p ON p.id = rp.player_id
                WHERE rp.raid_id IN @ids
                """, new { ids })).ToLookup(p => p.RaidId);

            return ServerResponse.Json(200, new
            {
                Raids = raids.Select(r => new
                {
                    r.Id, r.MapId, r.ServerId, r.State, r.CreatedAt, r.StartedAt, r.ClosedAt, r.LeaseExpiresAt,
                    Participants = participants[r.Id].Select(p => new { p.PlayerId, p.DisplayName, p.State, p.JoinedAt, p.ResolvedAt }),
                }),
            });
        }, ct);

    public Task<ServerResponse> AbortRaidAsync(string raidId, CancellationToken ct = default) => _raids.AbortNowAsync(raidId, ct);

    /// <summary>
    /// A consistent copy of the whole database, taken while the backend keeps running (VACUUM INTO), saved in a
    /// backups folder next to the database. Only the newest few are kept; copy them somewhere else to be safe.
    /// </summary>
    public async Task<ServerResponse> BackupAsync(CancellationToken ct = default)
    {
        var folder = BackupFolder();
        Directory.CreateDirectory(folder);
        var stamp = _time.GetUtcNow().UtcDateTime.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var path = Path.Combine(folder, $"raidzero-{stamp}.db");
        for (var n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(folder, $"raidzero-{stamp}-{n}.db");
        }

        await using (var connection = await _db.OpenAsync(ct))
        {
            await connection.ExecuteAsync("VACUUM INTO @path", new { path });
        }

        var pruned = ListBackupFiles().Skip(_backupsToKeep).ToList();
        foreach (var old in pruned)
        {
            old.Delete();
        }

        return ServerResponse.Json(200, new { File = Path.GetFileName(path), Bytes = new FileInfo(path).Length, Pruned = pruned.Select(f => f.Name) });
    }

    public object ListBackups() => new { Folder = BackupFolder(), Backups = ListBackupFiles().Select(f => new { File = f.Name, Bytes = f.Length, Created = f.LastWriteTimeUtc }) };

    private string BackupFolder() => Path.Combine(Path.GetDirectoryName(_db.FilePath)!, "backups");

    private List<FileInfo> ListBackupFiles()
    {
        var folder = new DirectoryInfo(BackupFolder());
        return folder.Exists
            ? folder.GetFiles("raidzero-*.db").OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name, StringComparer.Ordinal).ToList()
            : [];
    }

    private static async Task<IReadOnlyList<LedgerEntry>> QueryLedgerAsync(Microsoft.Data.Sqlite.SqliteConnection c, string where, object args, bool newestFirst = true) =>
        (await c.QueryAsync<LedgerRow>(
            $"""
            SELECT id AS Id, player_id AS PlayerId, raid_id AS RaidId, kind AS Kind, detail AS Detail, created_at AS CreatedAt
            FROM ledger_entries WHERE {where} ORDER BY id {(newestFirst ? "DESC" : "ASC")} LIMIT 100
            """, args))
        .Select(r => new LedgerEntry(r.Id, r.PlayerId, r.RaidId, r.Kind, JsonDocument.Parse(r.Detail).RootElement.Clone(), r.CreatedAt))
        .ToList();

    private static string EscapeLike(string text) => text.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    private sealed class PlayerRow
    {
        public const string Columns =
            "id AS Id, display_name AS DisplayName, profile_version AS ProfileVersion, created_at AS CreatedAt, " +
            "(SELECT raid_id FROM raid_participants rp WHERE rp.player_id = players.id AND (rp.state = 'in_raid' OR rp.state = 'ticketed') LIMIT 1) AS LiveRaidId";

        public string Id { get; set; } = "";

        public string DisplayName { get; set; } = "";

        public long ProfileVersion { get; set; }

        public string CreatedAt { get; set; } = "";

        public string? LiveRaidId { get; set; }

        public PlayerSummary ToSummary() => new(Id, DisplayName, ProfileVersion, CreatedAt, LiveRaidId);
    }

    private sealed class ItemRow
    {
        public const string Columns =
            "i.id AS Id, i.def_id AS DefId, i.state AS State, i.owner_id AS OwnerId, p.display_name AS OwnerName, i.raid_id AS RaidId, " +
            "i.parent_id AS ParentId, i.slot AS Slot, i.grid_x AS GridX, i.grid_y AS GridY, i.quantity AS Quantity, i.durability AS Durability, " +
            "i.origin AS Origin, i.origin_raid_id AS OriginRaidId, i.created_at AS CreatedAt, i.updated_at AS UpdatedAt";

        public string Id { get; set; } = "";

        public string DefId { get; set; } = "";

        public string State { get; set; } = "";

        public string? OwnerId { get; set; }

        public string? OwnerName { get; set; }

        public string? RaidId { get; set; }

        public string? ParentId { get; set; }

        public string? Slot { get; set; }

        public long? GridX { get; set; }

        public long? GridY { get; set; }

        public long Quantity { get; set; }

        public double? Durability { get; set; }

        public string Origin { get; set; } = "";

        public string? OriginRaidId { get; set; }

        public string CreatedAt { get; set; } = "";

        public string UpdatedAt { get; set; } = "";
    }

    private sealed class RaidRow
    {
        public string Id { get; set; } = "";

        public string MapId { get; set; } = "";

        public string ServerId { get; set; } = "";

        public string State { get; set; } = "";

        public string CreatedAt { get; set; } = "";

        public string? StartedAt { get; set; }

        public string? ClosedAt { get; set; }

        public string LeaseExpiresAt { get; set; } = "";
    }

    private sealed class ParticipantRow
    {
        public string RaidId { get; set; } = "";

        public string PlayerId { get; set; } = "";

        public string DisplayName { get; set; } = "";

        public string State { get; set; } = "";

        public string? JoinedAt { get; set; }

        public string? ResolvedAt { get; set; }
    }

    private sealed class LedgerRow
    {
        public long Id { get; set; }

        public string? PlayerId { get; set; }

        public string? RaidId { get; set; }

        public string Kind { get; set; } = "";

        public string Detail { get; set; } = "";

        public string CreatedAt { get; set; } = "";
    }
}
