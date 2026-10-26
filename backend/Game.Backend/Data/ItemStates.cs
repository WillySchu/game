using RaidZero.Shared.Inventory;

namespace RaidZero.Backend.Data;

/// <summary>
/// Values of item_instances.state. Keep in step with the CHECK constraint in the latest migration that
/// rebuilds item_instances (currently 0003_merged_state.sql); ItemStateMigrationTests checks they match.
/// </summary>
public static class ItemStates
{
    /// <summary>In a player's stash or equipped on their character, outside a raid.</summary>
    public const string Stash = "stash";

    /// <summary>In a raid's pool: checked out, picked up, or raid loot not yet extracted.</summary>
    public const string InRaid = "in_raid";

    /// <summary>Left in a raid nobody extracted it from.</summary>
    public const string Lost = "lost";

    /// <summary>Used up, like a medkit.</summary>
    public const string Consumed = "consumed";

    /// <summary>Sold to a trader.</summary>
    public const string Sold = "sold";

    /// <summary>Thrown away by the player.</summary>
    public const string Discarded = "discarded";

    /// <summary>A stack whose whole quantity moved into another stack.</summary>
    public const string Merged = "merged";

    public static IReadOnlyList<string> All { get; } = [Stash, InRaid, Lost, Consumed, Sold, Discarded, Merged];

    /// <summary>The state an item takes when an <see cref="InventoryTree"/> operation removes it.</summary>
    public static string ForRemoval(RemovalReason reason) => reason switch
    {
        RemovalReason.Discarded => Discarded,
        RemovalReason.Merged => Merged,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "No item state for this removal reason."),
    };
}
