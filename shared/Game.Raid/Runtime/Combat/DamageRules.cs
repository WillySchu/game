using System;
using RaidZero.Shared.Raid;

namespace RaidZero.Raid
{
    /// <summary>The armor where a shot landed: how well it stops rounds, and how worn it is.</summary>
    public readonly struct ArmorHit
    {
        public ArmorHit(float protection, float durability, float maxDurability)
        {
            Protection = protection;
            Durability = durability;
            MaxDurability = maxDurability;
        }

        /// <summary>The armor's protection when new (ArmorStats.protection). A round whose penetration is at least this goes through.</summary>
        public float Protection { get; }

        /// <summary>Durability points left.</summary>
        public float Durability { get; }

        /// <summary>Durability points when new (ArmorStats.durability).</summary>
        public float MaxDurability { get; }

        /// <summary>
        /// The protection it has now: worn armor protects less, in proportion to the durability it has left.
        /// None once it's worn out, or with no durability to speak of.
        /// </summary>
        public float EffectiveProtection =>
            MaxDurability > 0 && Durability > 0 ? Protection * Math.Min(1f, Durability / MaxDurability) : 0f;
    }

    /// <summary>What one hit does: the health it takes, and the durability it takes off the armor (0 with none).</summary>
    public readonly struct HitDamage
    {
        public HitDamage(float health, float armorWear, bool penetrated)
        {
            Health = health;
            ArmorWear = armorWear;
            Penetrated = penetrated;
        }

        public float Health { get; }

        public float ArmorWear { get; }

        /// <summary>Whether the round went through armor, or there was none in the way.</summary>
        public bool Penetrated { get; }
    }

    /// <summary>
    /// How much a hit hurts. Plain arithmetic, the same for every player, with all the numbers here to tune:
    /// <list type="bullet">
    /// <item>Everyone has <see cref="MaxHealth"/>; the round's damage is scaled by where it landed (<see cref="Multiplier"/>).</item>
    /// <item>Armor covers only what <see cref="ArmorCovers"/> says (the torso, for now).</item>
    /// <item>A round whose penetration is at least the armor's protection (less as it wears) goes through: most
    /// of its damage gets through, and the armor wears by the full damage. Otherwise most is stopped, and the
    /// armor wears by half.</item>
    /// </list>
    /// </summary>
    public static class DamageRules
    {
        public const float MaxHealth = 100f;

        public const float HeadMultiplier = 3f;
        public const float TorsoMultiplier = 1f;
        public const float LimbMultiplier = 0.6f;

        /// <summary>Of a round that goes through armor, the share of its damage that reaches the player.</summary>
        public const float PenetratingShare = 0.85f;

        /// <summary>Of a round that armor stops, the share that still hurts (blunt damage).</summary>
        public const float BluntShare = 0.15f;

        /// <summary>Armor durability lost per point of damage, for a round that goes through, and for one that doesn't.</summary>
        public const float PenetratingWear = 1f;
        public const float StoppedWear = 0.5f;

        public static float Multiplier(BodyPart part)
        {
            switch (part)
            {
                case BodyPart.Head:
                    return HeadMultiplier;
                case BodyPart.Arms:
                case BodyPart.Legs:
                    return LimbMultiplier;
                default:
                    return TorsoMultiplier;
            }
        }

        /// <summary>Whether body armor (the character's armor slot) covers that part.</summary>
        public static bool ArmorCovers(BodyPart part) => part == BodyPart.Torso;

        /// <summary>
        /// What a round with this damage and penetration does where it landed. <paramref name="armor"/> is the
        /// armor covering that part, or null for none (or a part the armor doesn't cover). Worn-out armor stops
        /// nothing and wears no further.
        /// </summary>
        public static HitDamage Resolve(float damage, float penetration, BodyPart part, ArmorHit? armor)
        {
            var scaled = Math.Max(0f, damage) * Multiplier(part);
            if (armor == null || armor.Value.EffectiveProtection <= 0f)
            {
                return new HitDamage(scaled, 0f, penetrated: true);
            }

            bool through = penetration >= armor.Value.EffectiveProtection;
            var wear = Math.Min(armor.Value.Durability, scaled * (through ? PenetratingWear : StoppedWear));
            return new HitDamage(scaled * (through ? PenetratingShare : BluntShare), wear, through);
        }
    }
}
