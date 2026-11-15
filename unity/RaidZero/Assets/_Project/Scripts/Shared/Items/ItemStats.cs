using System;
using System.Collections.Generic;
using UnityEngine;

namespace RaidZero.Items
{
    
    public static class ItemTags
    {
        public const string Weapon = "weapon";
        public const string Magazine = "magazine";
        public const string Ammo = "ammo";
        public const string Armor = "armor";
        public const string Medical = "medical";
    }
    public interface IItemStats
    {
        public string itemType { get; }
    }

    public enum FireModes
    {
        Single,
        Burst,
        Auto
    }

    [Serializable]
    public class WeaponStats : IItemStats
    {
        public string itemType => ItemTags.Weapon;

        
        
        [Tooltip("List of fire modes available for this weapon")]
        public List<FireModes> fireModes = new List<FireModes> { FireModes.Single };

        [Tooltip("Delay in seconds between shots")]
        public float fireRate;
        public float recoilVertical;
        public float recoilHorizontal;
        public float accuracy;
        [Tooltip("Reload speed in seconds")]
        public float reloadSpeed;
        
        
    }

    [Serializable]
    public class MagazineStats : IItemStats
    {
        public string itemType => ItemTags.Magazine;

        [Tooltip("Multiplier on weapon's base reload speed")]
        public float reloadMult = 1f;
    }

    [Serializable]
    public class AmmoStats : IItemStats
    {
        public string itemType => ItemTags.Ammo;
        public float damage;
        public float penetration;
        public float velocity;
        
    }

    // Armor and meds not implemented yet.
    [Serializable]
    public class ArmorStats : IItemStats
    {
        public string itemType => ItemTags.Armor;
        public float durability;
        public float protection;
    }

    [Serializable]
    public class MedicalStats : IItemStats
    {
        public string itemType => ItemTags.Medical;
        public float healing;
    }
}