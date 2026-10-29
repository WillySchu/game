using FishNet.Managing;
using UnityEngine;

namespace RaidZero.Net
{
    /// <summary>
    /// Whether Fish-Net's latency simulator runs, decided at startup rather than in the NetworkManager prefab,
    /// so it's on while developing and off in builds people play: the editor follows simulate_lag in
    /// UserSettings/RaidZeroDev.json (on unless set to false), and a build simulates only with --simulate-lag.
    /// How much latency and loss comes from the prefab's Transport Manager (100 ms each way, 2% loss).
    /// </summary>
    public static class NetworkConditions
    {
        public static bool ShouldSimulate => Application.isEditor ? DevSettings.SimulateLag : HasArgument("--simulate-lag");

        public static void Apply(NetworkManager networkManager)
        {
            var simulator = networkManager.TransportManager.LatencySimulator;
            var simulate = ShouldSimulate;
            if (simulator.GetEnabled() != simulate)
            {
                simulator.SetEnabled(simulate);
            }

            if (simulate)
            {
                Debug.Log("[net] Simulating " + simulator.GetLatency() + " ms latency and " + (simulator.GetPacketLost() * 100).ToString("0.#")
                    + "% packet loss (simulate_lag in UserSettings/RaidZeroDev.json turns it off).");
            }
        }

        private static bool HasArgument(string name)
        {
            foreach (var arg in System.Environment.GetCommandLineArgs())
            {
                if (arg == name)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
