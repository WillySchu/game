namespace RaidZero.Backend;

/// <summary>The "Backend" section of appsettings.json. Relative paths resolve against the content root.</summary>
public sealed class BackendOptions
{
    public const string SectionName = "Backend";

    /// <summary>SQLite database file. Created, along with its folder, on first start.</summary>
    public string DatabasePath { get; set; } = "data/raidzero.db";

    /// <summary>The catalog exported from Unity. Shared with the game server builds.</summary>
    public string CatalogPath { get; set; } = "../../shared/catalog/item_catalog.json";
}
