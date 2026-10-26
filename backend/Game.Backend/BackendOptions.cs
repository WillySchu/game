namespace RaidZero.Backend;

/// <summary>The "Backend" section of appsettings.json. Relative paths resolve against the content root.</summary>
public sealed class BackendOptions
{
    public const string SectionName = "Backend";

    /// <summary>SQLite database file. Created, along with its folder, on first start.</summary>
    public string DatabasePath { get; set; } = "data/raidzero.db";

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
}
