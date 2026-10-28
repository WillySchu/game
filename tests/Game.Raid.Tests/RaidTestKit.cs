using RaidZero.Shared.Catalog;
using RaidZero.Shared.Ids;
using RaidZero.Shared.Inventory;

namespace RaidZero.Raid.Tests;

/// <summary>A small catalog built in code, and helpers for building items from it.</summary>
public static class RaidTestKit
{
    public static readonly ItemCatalog Catalog = new(
    [
        new() { DefId = "character", Slots = [Slot("primary", "weapon_long"), Slot("rig", "rig"), Slot("backpack", "backpack")] },
        new() { DefId = "ground", Grid = new GridSpec { Width = 10, Height = 10 } },
        new() { DefId = "crate", Grid = new GridSpec { Width = 6, Height = 4 } },
        new() { DefId = "rifle", Width = 4, Height = 2, Tags = ["weapon_long"], Slots = [Slot("magazine", "mag")] },
        new() { DefId = "mag", Width = 1, Height = 2, Tags = ["mag"], Slots = [new() { Name = "rounds", Accepts = ["ammo"], MaxQuantity = 30 }] },
        new() { DefId = "ammo", MaxStack = 60, Tags = ["ammo"] },
        new() { DefId = "rig", Width = 2, Height = 2, Tags = ["rig"], Grid = new GridSpec { Width = 2, Height = 1 } },
        new() { DefId = "backpack", Width = 3, Height = 3, Tags = ["backpack"], Grid = new GridSpec { Width = 4, Height = 4 } },
        new() { DefId = "gpu", Width = 2, Height = 1 },
        new() { DefId = "fuse" },
    ]);

    public static SlotSpec Slot(string name, string accepts) => new() { Name = name, Accepts = [accepts] };

    public static ItemNode Node(string defId, string? slot = null, int? x = null, int? y = null, int quantity = 1, params ItemNode[] children) =>
        new() { Id = Uuid7.NewId(), DefId = defId, Slot = slot, X = x, Y = y, Quantity = quantity, Children = children.ToList() };

    /// <summary>A loaded rifle, an empty rig and an empty backpack.</summary>
    public static ItemNode Loadout() =>
        Node("character", children:
        [
            Node("rifle", "primary", children: [Node("mag", "magazine", children: [Node("ammo", "rounds", quantity: 30)])]),
            Node("rig", "rig"),
            Node("backpack", "backpack"),
        ]);

    public static string Child(ItemNode parent, string slot) => parent.Children.Single(c => c.Slot == slot).Id;
}
