namespace RaidZero.Backend;

/// <summary>The "Backend" section of appsettings.json. Relative paths resolve against the content root.</summary>
public sealed class BackendOptions
{
    public const string SectionName = "Backend";

    /// <summary>
    /// SQLite database file. Created, along with its folder, on first start. Not "data/": on macOS and Windows that
    /// is the same folder as the source folder Data/, and the database would land among the code.
    /// </summary>
    public string DatabasePath { get; set; } = "App_Data/raidzero.db";

    /// <summary>The catalog exported from Unity. Shared with the game server builds.</summary>
    public string CatalogPath { get; set; } = "../../shared/catalog/item_catalog.json";

    /// <summary>The gear new players start with.</summary>
    public string StarterKitPath { get; set; } = "starter_kit.json";

    /// <summary>
    /// Maps POST /v1/auth/dev-login, which logs anyone in by name with no password. On in Development only;
    /// the endpoint doesn't exist at all when this is off.
    /// </summary>
    public bool DevLoginEnabled { get; set; }

    /// <summary>Base64 HMAC key, at least 32 bytes. Required; the backend won't start without it.</summary>
    public string TokenSigningKey { get; set; } = "";

    public double TokenLifetimeHours { get; set; } = 72;

    /// <summary>
    /// Unlocks /v1/admin and the /admin page: player and item lookups, grants, raid aborts and backups. At least
    /// 32 characters. When empty, none of the admin endpoints exist.
    /// </summary>
    public string AdminToken { get; set; } = "";

    /// <summary>How many database backups to keep in the backups folder next to the database.</summary>
    public int BackupsToKeep { get; set; } = 14;

    /// <summary>Game servers allowed to run raids. Each authenticates with its own token.</summary>
    public List<GameServerOptions> Servers { get; set; } = [];

    public RaidOptions Raids { get; set; } = new();
}

public sealed class GameServerOptions
{
    /// <summary>Stored on each raid it runs, e.g. "dev-local".</summary>
    public string Id { get; set; } = "";

    /// <summary>The server's secret, at least 32 characters. It sends this as its bearer token.</summary>
    public string Token { get; set; } = "";

    /// <summary>Where clients connect, as handed out in raid tickets.</summary>
    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 7770;
}

public sealed class RaidOptions
{
    /// <summary>How long a player has to connect after getting a ticket.</summary>
    public int TicketSeconds { get; set; } = 60;

    /// <summary>A raid is aborted, and its players refunded, if the server goes this long without a heartbeat.</summary>
    public int LeaseSeconds { get; set; } = 60;

    public int MaxPlayersPerRaid { get; set; } = 4;

    /// <summary>
    /// How long after its first player joins a raid still takes new tickets. After that, players queue for the
    /// next raid instead of dropping into one that's nearly over.
    /// </summary>
    public int JoinWindowSeconds { get; set; } = 120;

    /// <summary>How often the background job looks for raids whose lease has run out.</summary>
    public int ReaperIntervalSeconds { get; set; } = 10;

    public bool ReaperEnabled { get; set; } = true;
}
