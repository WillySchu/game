using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RaidZero.Backend;
using RaidZero.Backend.Auth;
using RaidZero.Backend.Catalog;
using RaidZero.Backend.Data;
using RaidZero.Backend.Players;
using RaidZero.Backend.Raids;
using RaidZero.Shared.Catalog;
using RaidZero.Shared.Inventory;

namespace Game.Backend.Tests;

/// <summary>
/// The real raid service on a temp database, with one game server ("dev-local") and a clock tests move.
/// Every player starts with the committed starter kit:
///   character: rifle (primary) holding a magazine of 30; armor; rig with a magazine of 30 at (0,0) and a
///   medkit at (1,0); an empty 4x5 backpack.
/// </summary>
public sealed class RaidFixture : IDisposable
{
    public const string ServerId = "dev-local";
    public const string Map = "greybox_01";

    public static readonly ItemCatalog Catalog = CatalogLoader.LoadFile(TestPaths.CatalogPath);

    private readonly TempDatabase _temp = new();
    private int _keys;

    public RaidFixture(int maxPlayersPerRaid = 4, bool withServer = true)
    {
        Players = new PlayerStore(_temp.Database, Catalog, StarterKit.LoadFile(TestPaths.StarterKitPath, Catalog), Time);
        Stash = new StashInventory(_temp.Database, Catalog, Time);
        var servers = new GameServerRegistry(withServer ? [new GameServerOptions { Id = ServerId, Token = new string('s', 40) }] : []);
        var options = Options.Create(new BackendOptions
        {
            Raids = new RaidOptions { TicketSeconds = 60, LeaseSeconds = 60, MaxPlayersPerRaid = maxPlayersPerRaid },
        });
        Raids = new RaidService(_temp.Database, Catalog, servers, options, Time, NullLogger<RaidService>.Instance);
    }

    public FixedTime Time { get; } = new(new DateTimeOffset(2026, 10, 26, 20, 0, 0, TimeSpan.Zero));

    public Database Db => _temp.Database;

    public PlayerStore Players { get; }

    public StashInventory Stash { get; }

    public RaidService Raids { get; }

    public void Dispose() => _temp.Dispose();

    public async Task StartAsync()
    {
        await Db.InitializeAsync();
        await Migrator.MigrateAsync(Db, Migrator.LoadEmbedded());
    }

    public async Task<string> PlayerAsync(string name) => (await Players.LoginOrCreateAsync(name)).PlayerId;

    public async Task<Profile> ProfileAsync(string playerId) => (await Players.GetProfileAsync(playerId))!;

    public string NextKey() => $"key-{++_keys}";

    /// <summary>A ticket, asserting it was granted.</summary>
    public async Task<(string RaidId, string Token)> TicketAsync(string playerId, string map = Map)
    {
        var response = await Raids.CreateTicketAsync(playerId, map);
        Assert.Equal(200, response.StatusCode);
        var body = Body(response);
        return (body.GetProperty("raid_id").GetString()!, body.GetProperty("join_token").GetString()!);
    }

    public Task<ServerResponse> JoinAsync(string raidId, string token, string? key = null, string server = ServerId) =>
        Raids.JoinAsync(server, raidId, key ?? NextKey(), Hash(raidId, "join", token), new RaidService.JoinRequest(token));

    /// <summary>Ticket and join, returning the loadout the game server would spawn the player with.</summary>
    public async Task<(string RaidId, ItemNode Loadout)> EnterAsync(string playerId)
    {
        var (raidId, token) = await TicketAsync(playerId);
        var joined = await JoinAsync(raidId, token);
        Assert.Equal(200, joined.StatusCode);
        return (raidId, Loadout(joined));
    }

    public Task<ServerResponse> ExtractAsync(string raidId, string playerId, ItemNode inventory, string? key = null) =>
        Outcome(raidId, playerId, "extracted", inventory, key);

    public Task<ServerResponse> DieAsync(string raidId, string playerId, string? key = null) =>
        Outcome(raidId, playerId, "died", null, key);

    public Task<ServerResponse> CloseAsync(string raidId, string? key = null) =>
        Raids.CloseAsync(ServerId, raidId, key ?? NextKey(), Hash(raidId, "close", ""));

    public static JsonElement Body(ServerResponse response) => JsonDocument.Parse(response.Body).RootElement;

    public static string Error(ServerResponse response) => Body(response).GetProperty("error").GetString()!;

    public static ItemNode Loadout(ServerResponse joined) =>
        JsonSerializer.Deserialize<ItemNode>(Body(joined).GetProperty("loadout").GetRawText(), JsonDefaults.Options)!;

    public static ItemNode InSlot(ItemNode parent, string slot) => parent.Children.Single(c => c.Slot == slot);

    public static ItemNode AtCell(ItemNode parent, int x, int y) => parent.Children.Single(c => c.X == x && c.Y == y);

    public async Task<string> ItemStateAsync(string itemId) =>
        (await Db.ReadAsync(c => Dapper.SqlMapper.ExecuteScalarAsync<string>(c, "SELECT state FROM item_instances WHERE id = @itemId", new { itemId })))!;

    public async Task<string> ParticipantStateAsync(string raidId, string playerId) =>
        (await Db.ReadAsync(c => Dapper.SqlMapper.ExecuteScalarAsync<string>(c,
            "SELECT state FROM raid_participants WHERE raid_id = @raidId AND player_id = @playerId", new { raidId, playerId })))!;

    public async Task<string> RaidStateAsync(string raidId) =>
        (await Db.ReadAsync(c => Dapper.SqlMapper.ExecuteScalarAsync<string>(c, "SELECT state FROM raids WHERE id = @raidId", new { raidId })))!;

    private Task<ServerResponse> Outcome(string raidId, string playerId, string outcome, ItemNode? inventory, string? key)
    {
        var request = new RaidService.OutcomeRequest(playerId, outcome, "north_gate", inventory, null);
        return Raids.ReportOutcomeAsync(ServerId, raidId, key ?? NextKey(), Hash(raidId, "outcome", JsonSerializer.Serialize(request, JsonDefaults.Options)), request);
    }

    // Same request, same hash; that's all the idempotency layer needs from callers.
    private static string Hash(string raidId, string call, string body) => Idempotency.HashRequest("POST", $"/{raidId}/{call}", body);
}
