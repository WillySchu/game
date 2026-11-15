using RaidZero.Shared.Raid;

namespace RaidZero.Raid.Tests;

/// <summary>The damage rules, with today's items: a 5.45 (20 damage, penetration 2), a 9x18 (10, 0), and the light vest (protection 2, 100 durability).</summary>
public class DamageRulesTests
{
    private static readonly ArmorHit NewVest = new(2f, 100f, 100f);

    [Fact]
    public void WithoutArmor_TheDamageIsScaledByWhereItLanded()
    {
        Assert.Equal(20f, DamageRules.Resolve(20, 2, BodyPart.Torso, null).Health);
        Assert.Equal(60f, DamageRules.Resolve(20, 2, BodyPart.Head, null).Health);
        Assert.Equal(12f, DamageRules.Resolve(20, 2, BodyPart.Arms, null).Health, 3);
        Assert.Equal(12f, DamageRules.Resolve(20, 2, BodyPart.Legs, null).Health, 3);
        Assert.Equal(0f, DamageRules.Resolve(20, 2, BodyPart.Torso, null).ArmorWear);
        Assert.True(DamageRules.Resolve(20, 2, BodyPart.Torso, null).Penetrated);
    }

    [Fact]
    public void ARoundThatGoesThrough_MostlyHurts_AndWearsTheArmorByItsDamage()
    {
        var hit = DamageRules.Resolve(20, 2, BodyPart.Torso, NewVest);

        Assert.True(hit.Penetrated);
        Assert.Equal(17f, hit.Health, 3);
        Assert.Equal(20f, hit.ArmorWear, 3);
    }

    [Fact]
    public void ARoundThatsStopped_BarelyHurts_AndWearsTheArmorByHalf()
    {
        var hit = DamageRules.Resolve(10, 0, BodyPart.Torso, NewVest);

        Assert.False(hit.Penetrated);
        Assert.Equal(1.5f, hit.Health, 3);
        Assert.Equal(5f, hit.ArmorWear, 3);
    }

    [Fact]
    public void WornArmor_ProtectsLess_AndWornOutArmorNotAtAll()
    {
        // Half worn: protection 1, so a penetration-1 round now goes through.
        Assert.False(DamageRules.Resolve(10, 1, BodyPart.Torso, NewVest).Penetrated);
        Assert.True(DamageRules.Resolve(10, 1, BodyPart.Torso, new ArmorHit(2, 50, 100)).Penetrated);

        var throughNothing = DamageRules.Resolve(10, 0, BodyPart.Torso, new ArmorHit(2, 0, 100));
        Assert.Equal(10f, throughNothing.Health);
        Assert.Equal(0f, throughNothing.ArmorWear);
    }

    [Fact]
    public void Wear_NeverExceedsWhatsLeft()
    {
        Assert.Equal(3f, DamageRules.Resolve(20, 2, BodyPart.Torso, new ArmorHit(2, 3, 100)).ArmorWear);
    }

    [Fact]
    public void OnlyTheTorsoIsCovered()
    {
        Assert.True(DamageRules.ArmorCovers(BodyPart.Torso));
        Assert.False(DamageRules.ArmorCovers(BodyPart.Head));
        Assert.False(DamageRules.ArmorCovers(BodyPart.Arms));
        Assert.False(DamageRules.ArmorCovers(BodyPart.Legs));
    }

    [Fact]
    public void TodaysNumbers_ReadAsPlanned()
    {
        // An unarmored player dies to five torso hits or two headshots from the rifle; the vest lasts five.
        var health = new RaidHealth();
        for (int i = 0; i < 4; i++)
        {
            health.Damage("sam", DamageRules.Resolve(20, 2, BodyPart.Torso, null).Health);
        }

        Assert.False(health.IsDead("sam"));
        health.Damage("sam", DamageRules.Resolve(20, 2, BodyPart.Torso, null).Health);
        Assert.True(health.IsDead("sam"));

        health.Damage("alex", DamageRules.Resolve(20, 2, BodyPart.Head, null).Health);
        Assert.False(health.IsDead("alex"));
        health.Damage("alex", DamageRules.Resolve(20, 2, BodyPart.Head, null).Health);
        Assert.True(health.IsDead("alex"));
    }
}

public class RaidHealthTests
{
    [Fact]
    public void EveryoneStartsFull_AndHealthNeverLeavesItsRange()
    {
        var health = new RaidHealth();

        Assert.Equal(100f, health.Of("sam"));
        Assert.Equal(60f, health.Damage("sam", 40));
        Assert.Equal(60f, health.Damage("sam", -10));      // nothing for a negative amount
        Assert.Equal(90f, health.Heal("sam", 30));
        Assert.Equal(100f, health.Heal("sam", 500));
        Assert.Equal(0f, health.Damage("sam", 1000));
        Assert.True(health.IsDead("sam"));
        Assert.Equal(0f, health.Heal("sam", 50));          // no healing the dead
        Assert.Equal(100f, health.Of("alex"));             // others untouched
        Assert.Equal(100f, health.Of(null!));
    }
}
