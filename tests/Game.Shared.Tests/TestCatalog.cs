using RaidZero.Shared.Catalog;
using RaidZero.Shared.Ids;
using RaidZero.Shared.Inventory;

namespace Game.Shared.Tests;

/// <summary>
/// A small catalog built in code, independent of the real item_catalog.json, so these tests don't
/// break when designers change the real items.
/// </summary>
internal static class TestCatalog
{
    public static List<ItemDefinition> Definitions() =>
    [
        new() { DefId = "stash", Grid = new GridSpec { Width = 10, Height = 10 } },
        new()
        {
            DefId = "character",
            Slots =
            [
                new() { Name = "primary", Accepts = ["weapon_long"] },
                new() { Name = "backpack", Accepts = ["backpack"] },
            ],
        },
        new()
        {
            DefId = "rifle",
            Width = 4,
            Height = 2,
            HasDurability = true,
            Tags = ["weapon_long"],
            Slots = [new() { Name = "magazine", Accepts = ["mag_rifle"] }],
        },
        new()
        {
            DefId = "mag_30",
            Width = 1,
            Height = 2,
            Tags = ["mag_rifle"],
            Slots = [new() { Name = "rounds", Accepts = ["ammo_rifle"], MaxQuantity = 30 }],
        },
        new() { DefId = "ammo", MaxStack = 60, Tags = ["ammo_rifle"] },
        new() { DefId = "backpack", Width = 3, Height = 3, Tags = ["backpack"], Grid = new GridSpec { Width = 4, Height = 3 } },
        new() { DefId = "gpu", Width = 2, Height = 1 },
        new() { DefId = "fuse" },
    ];

    public static ItemCatalog Build() => new(Definitions());

    public static ItemNode Node(string defId, string? slot = null, int? x = null, int? y = null, int quantity = 1, double? durability = null, bool rotated = false, params ItemNode[] children) =>
        new()
        {
            Id = Uuid7.NewId(),
            DefId = defId,
            Slot = slot,
            X = x,
            Y = y,
            Rotated = rotated,
            Quantity = quantity,
            Durability = durability,
            Children = children.ToList(),
        };

    /// <summary>A character with a loaded rifle and a backpack holding two items.</summary>
    public static ItemNode ValidLoadout() =>
        Node("character", children:
        [
            Node("rifle", "primary", durability: 0.9, children:
            [
                Node("mag_30", "magazine", children: [Node("ammo", "rounds", quantity: 30)]),
            ]),
            Node("backpack", "backpack", children:
            [
                Node("gpu", "grid", 0, 0),
                Node("ammo", "grid", 2, 0, quantity: 45),
            ]),
        ]);
}
