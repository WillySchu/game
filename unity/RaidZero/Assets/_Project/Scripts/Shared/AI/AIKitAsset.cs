using System.Collections.Generic;
using RaidZero.Items;
using RaidZero.Raid;
using UnityEngine;

namespace RaidZero.Shared
{
    /// <summary>
    /// One way an AI can be kitted out (AIKitSpec): a gun with a loaded magazine, armor, a rig and a backpack, spare
    /// magazines, and items it always carries. An <see cref="AIProfile"/> lists one or more, picked by weight. Every
    /// magazine is filled with the ammo, as full as it goes; spares and items go in the rig, then the backpack.
    /// </summary>
    [CreateAssetMenu(menuName = "Raid Zero/AI/Kit", fileName = "AIKit")]
    public sealed class AIKitAsset : ScriptableObject
    {
        [Tooltip("The gun: in the primary slot, or the secondary for a handgun.")]
        [SerializeField] private ItemDefinitionAsset _weapon;

        [Tooltip("The magazine loaded in the gun, and the type the spares are.")]
        [SerializeField] private ItemDefinitionAsset _magazine;

        [Tooltip("What fills every magazine.")]
        [SerializeField] private ItemDefinitionAsset _ammo;

        [Min(0)]
        [SerializeField] private int _spareMagazines = 2;

        [SerializeField] private ItemDefinitionAsset _armor;
        [SerializeField] private ItemDefinitionAsset _rig;
        [SerializeField] private ItemDefinitionAsset _backpack;

        [Tooltip("Anything else it always carries (a medkit, say).")]
        [SerializeField] private List<ItemDefinitionAsset> _items = new List<ItemDefinitionAsset>();

        [Tooltip("How likely this kit is against the profile's others.")]
        [Min(0)]
        [SerializeField] private int _weight = 1;

        public int Weight => _weight;

        /// <summary>The kit by def IDs, for AIKits.Build.</summary>
        public AIKitSpec ToSpec()
        {
            var spec = new AIKitSpec
            {
                Name = name,
                Weapon = IdOf(_weapon),
                Magazine = IdOf(_magazine),
                Ammo = IdOf(_ammo),
                SpareMagazines = _spareMagazines,
                Armor = IdOf(_armor),
                Rig = IdOf(_rig),
                Backpack = IdOf(_backpack),
                Weight = _weight,
            };

            foreach (var item in _items)
            {
                if (item != null)
                {
                    spec.Items.Add(item.DefId);
                }
            }

            return spec;
        }

        private static string IdOf(ItemDefinitionAsset item) => item != null ? item.DefId : null;

#if UNITY_EDITOR
        /// <summary>For the example assets menu.</summary>
        public void Set(ItemDefinitionAsset weapon, ItemDefinitionAsset magazine, ItemDefinitionAsset ammo, int spareMagazines,
            ItemDefinitionAsset armor, ItemDefinitionAsset rig, ItemDefinitionAsset backpack, IEnumerable<ItemDefinitionAsset> items)
        {
            _weapon = weapon;
            _magazine = magazine;
            _ammo = ammo;
            _spareMagazines = spareMagazines;
            _armor = armor;
            _rig = rig;
            _backpack = backpack;
            _items = new List<ItemDefinitionAsset>(items);
        }
#endif
    }
}
