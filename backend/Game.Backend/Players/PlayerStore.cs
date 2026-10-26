using System.Text.Json;
using Dapper;
using RaidZero.Backend.Data;
using RaidZero.Shared.Catalog;
using RaidZero.Shared.Ids;
using RaidZero.Shared.Inventory;

namespace RaidZero.Backend.Players;

/// <summary>A player's stash and equipped gear, as GET /v1/me/profile returns it.</summary>
public sealed record Profile(string PlayerId, string DisplayName, long ProfileVersion, ItemNode Stash, ItemNode Character);

/// <summary>The result of a login: who it was, and whether this call created them.</summary>
public sealed record LoginResult(string PlayerId, string DisplayName, bool Created);

public sealed class PlayerStore
{
    private readonly Database _db;
    private readonly ItemCatalog _catalog;
    private readonly StarterKit _starterKit;
    private readonly TimeProvider _time;

    public PlayerStore(Database db, ItemCatalog catalog, StarterKit starterKit, TimeProvider time)
    {
        _db = db;
        _catalog = catalog;
        _starterKit = starterKit;
        _time = time;
    }

    /// <summary>
    /// Finds the player with this display name, or creates them with an empty stash and character plus the
    /// starter kit. Creating the player, their items and the ledger entry is one transaction.
    /// </summary>
    public Task<LoginResult> LoginOrCreateAsync(string displayName, CancellationToken ct = default) =>
        _db.WriteAsync(async (c, tx) =>
        {
            var existing = (await c.QueryAsync<string>(
                "SELECT id FROM players WHERE display_name = @displayName", new { displayName }, transaction: tx)).FirstOrDefault();
            if (existing is not null)
            {
                return new LoginResult(existing, displayName, Created: false);
            }

            var playerId = Uuid7.NewId();
            var now = _time.GetUtcNow().UtcDateTime.ToString("O");
            var (stash, character) = _starterKit.Instantiate();

            // Belt and braces: the kit was validated at startup, so this only fails on a bug.
            foreach (var root in new[] { stash, character })
            {
                var errors = InventoryValidator.Validate(_catalog, root);
                if (errors.Count > 0)
                {
                    throw new InvalidOperationException($"Starter kit produced an invalid tree: {errors[0]}");
                }
            }

            await c.ExecuteAsync(
                """
                INSERT INTO players (id, display_name, stash_root_id, character_root_id, profile_version, created_at)
                VALUES (@playerId, @displayName, @stashId, @characterId, 0, @now)
                """,
                new { playerId, displayName, stashId = stash.Id, characterId = character.Id, now },
                transaction: tx);

            var granted = await ItemRows.InsertTreeAsync(c, tx, stash, null, playerId, ItemStates.Stash, "starter", now);
            granted.AddRange(await ItemRows.InsertTreeAsync(c, tx, character, null, playerId, ItemStates.Stash, "starter", now));

            await c.ExecuteAsync(
                "INSERT INTO ledger_entries (player_id, kind, detail, created_at) VALUES (@playerId, 'grant', @detail, @now)",
                new { playerId, detail = JsonSerializer.Serialize(new { Reason = "starter_kit", Items = granted }, JsonDefaults.Options), now },
                transaction: tx);

            return new LoginResult(playerId, displayName, Created: true);
        }, ct);

    /// <summary>The player's stash and character trees, or null if there is no such player.</summary>
    public Task<Profile?> GetProfileAsync(string playerId, CancellationToken ct = default) =>
        _db.ReadAsync<Profile?>(async c =>
        {
            var player = (await c.QueryAsync<PlayerRow>(
                """
                SELECT id AS Id, display_name AS DisplayName, stash_root_id AS StashRootId,
                       character_root_id AS CharacterRootId, profile_version AS ProfileVersion
                FROM players WHERE id = @playerId
                """,
                new { playerId })).FirstOrDefault();
            if (player is null)
            {
                return null;
            }

            var items = await ItemRows.LoadStashItemsAsync(c, null, playerId);
            if (!items.TryGetValue(player.StashRootId, out var stash) || !items.TryGetValue(player.CharacterRootId, out var character))
            {
                throw new InvalidOperationException($"Player {playerId} is missing a root container.");
            }

            return new Profile(player.Id, player.DisplayName, player.ProfileVersion, stash, character);
        }, ct);

    private sealed class PlayerRow
    {
        public string Id { get; set; } = "";

        public string DisplayName { get; set; } = "";

        public string StashRootId { get; set; } = "";

        public string CharacterRootId { get; set; } = "";

        public long ProfileVersion { get; set; }
    }
}

public static class DisplayNames
{
    public const int MinLength = 3;
    public const int MaxLength = 24;

    /// <summary>3 to 24 characters: ASCII letters, digits, underscores and hyphens.</summary>
    public static bool IsValid(string? name) =>
        name is { Length: >= MinLength and <= MaxLength } && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
}
