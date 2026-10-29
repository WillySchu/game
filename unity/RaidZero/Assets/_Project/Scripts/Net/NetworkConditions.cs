using FishNet.Managing;
using UnityEngine;

namespace RaidZero.Net
{
    /// <summary>
    /// Keeps Fish-Net's latency simulator out of builds people play. In the editor, the Latency Simulator's
    /// Enabled box on the NetworkManager's Transport Manager decides, as usual. A build (a client, or the
    /// dedicated server) turns it off, whatever the box says, unless it's started with --simulate-lag.
    /// How much latency and loss comes from the Transport Manager either way (100 ms each way, 2% loss).
    /// </summary>
    public static class NetworkConditions
    {
        public static void Apply(NetworkManager networkManager)
        {
            var simulator = networkManager.TransportManager.LatencySimulator;
            if (!Application.isEditor)
            {
                var simulate = HasArgument("--simulate-lag");
                if (simulator.GetEnabled() != simulate)
                {
                    simulator.SetEnabled(simulate);
                }
            }

            if (simulator.GetEnabled())
            {
                Debug.Log("[net] Simulating " + simulator.GetLatency() + " ms latency and " + (simulator.GetPacketLost() * 100).ToString("0.#")
                    + "% packet loss (the Latency Simulator on the NetworkManager's Transport Manager).");
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
