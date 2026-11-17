namespace RaidZero.Shared.Raid
{
    /// <summary>Who someone in a raid is with: every player, or one of the AI factions.</summary>
    public enum Faction : byte
    {
        Players = 0,

        /// <summary>Holdout looters: armed locals picking over the zone.</summary>
        Looters = 1,

        /// <summary>Contractor patrols: the cordon's private security.</summary>
        Contractors = 2,
    }

    /// <summary>
    /// Who's hostile to whom. Every player is hostile to everyone, other players included; each AI faction is
    /// friendly to itself and hostile to everyone else, so looters and contractors fight each other too.
    /// </summary>
    public static class Factions
    {
        //                         Players  Looters  Contractors   (row: who's looking; column: at whom)
        private static readonly bool[,] Hostile =
        {
            /* Players */          { true,    true,    true },
            /* Looters */          { true,    false,   true },
            /* Contractors */      { true,    true,    false },
        };

        public static bool AreHostile(Faction looking, Faction at)
        {
            int a = (int)looking, b = (int)at;
            return a < Hostile.GetLength(0) && b < Hostile.GetLength(1) && Hostile[a, b];
        }
    }
}
