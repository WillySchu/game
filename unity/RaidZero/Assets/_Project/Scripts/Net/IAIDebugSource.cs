namespace RaidZero.Net
{
    /// <summary>
    /// A component on an AI that can say what it's thinking, for the F1 overlay's AI debug view: the raid server
    /// asks each AI's a few times a second while a dev client is watching (DevWatchAIBroadcast). Server only.
    /// </summary>
    public interface IAIDebugSource
    {
        AIDebugEntry Describe();
    }
}
