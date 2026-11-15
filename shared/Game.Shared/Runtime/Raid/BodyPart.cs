namespace RaidZero.Shared.Raid
{
    /// <summary>
    /// Where a shot landed. The player prefab's hitboxes say which part they are; the damage rules
    /// (RaidZero.Raid.DamageRules) scale the damage by it, and armor covers some parts.
    /// </summary>
    public enum BodyPart : byte
    {
        Torso = 0,
        Head = 1,
        Arms = 2,
        Legs = 3,
    }
}
