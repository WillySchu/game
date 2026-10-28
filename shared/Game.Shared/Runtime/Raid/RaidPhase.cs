namespace RaidZero.Shared.Raid
{
    /// <summary>Where a raid is in its life, as the game server runs it and its clients see it.</summary>
    public enum RaidPhase : byte
    {
        /// <summary>The level is ready and nobody has joined yet. The clock hasn't started.</summary>
        Waiting = 0,

        /// <summary>Someone has joined and the clock is running.</summary>
        InProgress = 1,

        /// <summary>The raid is over. Players still inside are missing in action; the results are being sent.</summary>
        Ending = 2,

        /// <summary>The backend has every result. A dedicated server exits now.</summary>
        Closed = 3,
    }

    /// <summary>How a player's raid ended, for the screen they see before going back to the Hideout.</summary>
    public enum RaidExit : byte
    {
        Extracted = 0,
        Died = 1,

        /// <summary>Still inside when the raid ended. Counts as a death: their gear is lost.</summary>
        MissingInAction = 2,

        /// <summary>The raid was called off (the server lost the backend, or an admin aborted it). Everyone gets their gear back.</summary>
        Aborted = 3,
    }

    /// <summary>Why a raid moved to <see cref="RaidPhase.Ending"/>.</summary>
    public enum RaidEndReason : byte
    {
        None = 0,

        /// <summary>The clock ran out.</summary>
        TimeUp = 1,

        /// <summary>Everyone who joined has extracted or died.</summary>
        EveryoneLeft = 2,

        /// <summary>Nobody inside has been connected for the grace period.</summary>
        Abandoned = 3,

        /// <summary>The backend aborted the raid.</summary>
        Aborted = 4,
    }
}
