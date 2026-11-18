namespace RaidZero.Net
{
    /// <summary>
    /// A component on an AI's prefab that needs the AI's ID: the raid server calls <see cref="SetUp"/> on every
    /// one of them when it spawns the AI (RaidServer.SpawnAI), before it's networked. The AI fires, reloads and
    /// is hit through the raid server by that ID. Server only: clients never get one.
    /// </summary>
    public interface IRaidAI
    {
        void SetUp(string aiId);
    }

    /// <summary>
    /// An AI's brain, which the F1 overlay's Spawn thinking dummy turns on (before it's networked): AI from spawn
    /// zones think anyway, and F1 dummies otherwise just stand and look. Editor and Development builds only.
    /// </summary>
    public interface IAIDevThinking
    {
        void ThinkForDev();
    }
}
