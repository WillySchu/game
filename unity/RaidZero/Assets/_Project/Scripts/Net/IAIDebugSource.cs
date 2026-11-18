namespace RaidZero.Net
{
    /// <summary>
    /// A component on an AI that can say what it's thinking, for the F1 overlay's AI debug view: the raid server
    /// asks each of an AI's a few times a second while a dev client is watching (DevWatchAIBroadcast), and each
    /// fills in its part of the AI's entry (its perception, its brain). Server only.
    /// </summary>
    public interface IAIDebugSource
    {
        void Describe(ref AIDebugEntry entry);
    }
}
