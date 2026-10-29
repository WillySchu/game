using System;
using System.Threading.Tasks;
using FishNet.Managing;
using FishNet.Transporting;
using RaidZero.BackendApi;
using RaidZero.Shared.Raid;
using UnityEngine;

namespace RaidZero.Net
{
    /// <summary>
    /// Turns any build, the dedicated server's included, into a scripted player for CI's raid test:
    ///
    ///   RaidZeroServer.x86_64 -batchmode -nographics --ci-client --ci-expect extracted
    ///
    /// It dev-logs in as a new player against the backend (--backend-url or RAIDZERO_BACKEND_URL), deploys
    /// like the Hideout does, joins the raid, and stands still. Then it checks that it left the way
    /// --ci-expect says (extracted, or missing_in_action) and that the backend's result and the player's gear
    /// agree (<see cref="RaidResultCheck"/>). It exits 0 if everything checks out and 1 if not, logging
    /// "[ci] PASS" or "[ci] FAIL" and why.
    ///
    /// Run the server with --test-extract-at-spawn to test an extraction, or with a short --raid-minutes to
    /// test being missing in action. --ci-map picks the map (greybox_01), --ci-timeout the seconds it waits
    /// for the whole run (300). Nothing here runs without --ci-client.
    /// </summary>
    public sealed class CiClient : MonoBehaviour
    {
        private const string Log = "[ci] ";

        private readonly TaskCompletionSource<JoinRaidResultBroadcast> _joined = new TaskCompletionSource<JoinRaidResultBroadcast>();
        private readonly TaskCompletionSource<RaidExit> _left = new TaskCompletionSource<RaidExit>();
        private NetworkManager _networkManager;

        // After every Awake in the first scene, before any Start: early enough to stop Fish-Net starting a server
        // in a dedicated server build (it does that in NetworkManager.Start).
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (!ServerConfig.HasFlag("--ci-client"))
            {
                return;
            }

            var networkManager = FindAnyObjectByType<NetworkManager>();
            if (networkManager == null)
            {
                Debug.LogError(Log + "FAIL: no NetworkManager in the first scene.");
                Application.Quit(1);
                return;
            }

            networkManager.ServerManager.SetStartOnHeadless(false);
            var client = new GameObject("CI client").AddComponent<CiClient>();
            client._networkManager = networkManager;
            DontDestroyOnLoad(client.gameObject);
        }

        private async void Start()
        {
            var timeout = ServerConfig.FloatArgument("--ci-timeout") ?? 300f;
            var run = RunAsync();
            var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(timeout)));
            string problem;
            if (finished != run)
            {
                problem = "timed out after " + timeout + " seconds.";
            }
            else if (run.IsFaulted)
            {
                problem = run.Exception?.GetBaseException().ToString();
            }
            else
            {
                problem = run.Result;
            }

            if (problem == null)
            {
                Debug.Log(Log + "PASS");
                Application.Quit(0);
            }
            else
            {
                Debug.LogError(Log + "FAIL: " + problem);
                Application.Quit(1);
            }
        }

        private void OnDestroy()
        {
            RaidAuthenticator.JoinAnswered -= OnJoinAnswered;
            if (_networkManager != null)
            {
                _networkManager.ClientManager.UnregisterBroadcast<LeftRaidBroadcast>(OnLeft);
                _networkManager.ClientManager.UnregisterBroadcast<ExtractionBroadcast>(OnExtraction);
                _networkManager.ClientManager.OnClientConnectionState -= OnClientConnectionState;
            }
        }

        /// <summary>The whole test. Null if it passed, otherwise what went wrong.</summary>
        private async Task<string> RunAsync()
        {
            var expect = ServerConfig.StringArgument("--ci-expect") ?? "extracted";
            RaidExit expectedExit;
            switch (expect)
            {
                case "extracted":
                    expectedExit = RaidExit.Extracted;
                    break;
                case "missing_in_action":
                    expectedExit = RaidExit.MissingInAction;
                    break;
                default:
                    return "--ci-expect must be extracted or missing_in_action, not " + expect + ".";
            }

            var mapId = ServerConfig.StringArgument("--ci-map") ?? "greybox_01";
            var session = Hideout.Session;
            var name = "ci-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            Debug.Log(Log + "Logging in as " + name + " at " + Hideout.BackendUrl + "; expecting to leave " + expect + ".");
            var login = await session.DevLoginAsync(name);
            if (!login.Succeeded)
            {
                return "dev login failed: " + login.Error + " " + login.Message;
            }

            var carriedIn = RaidResultCheck.CarriedItemIds(login.Value);
            Debug.Log(Log + "Going in with " + carriedIn.Count + " item(s).");

            RaidAuthenticator.JoinAnswered += OnJoinAnswered;
            _networkManager.ClientManager.RegisterBroadcast<LeftRaidBroadcast>(OnLeft);
            _networkManager.ClientManager.RegisterBroadcast<ExtractionBroadcast>(OnExtraction);
            _networkManager.ClientManager.OnClientConnectionState += OnClientConnectionState;

            var deploy = await Hideout.DeployAsync(_networkManager, mapId);
            if (!deploy.Succeeded)
            {
                return "deploy failed: " + deploy;
            }

            var raidId = deploy.Ticket.RaidId;
            Debug.Log(Log + "Ticket for raid " + raidId + "; connecting to " + deploy.Ticket.ServerHost + ":" + deploy.Ticket.ServerPort + ".");

            var joined = await _joined.Task;
            if (!joined.Success)
            {
                return "the server refused the ticket: " + joined.Error + " " + joined.Message;
            }

            Debug.Log(Log + "In the raid. Standing still.");
            var exit = await _left.Task;
            Debug.Log(Log + "Left the raid: " + exit + ".");
            if (exit != expectedExit)
            {
                return "left the raid as " + exit + ", not " + expectedExit + ".";
            }

            var result = await session.WaitForRaidResultAsync(raidId, TimeSpan.FromSeconds(90));
            if (!result.Succeeded)
            {
                return "no final raid result from the backend: " + result.Error + " " + result.Message;
            }

            var outcome = result.Value;
            Debug.Log(Log + "Backend result: " + outcome.Outcome + "; " + outcome.ItemsReturned.Count + " returned, " + outcome.ItemsLost.Count + " lost, "
                + outcome.ItemsGained.Count + " gained.");
            return RaidResultCheck.Problem(expectedExit == RaidExit.Extracted, carriedIn, outcome, session.Profile);
        }

        private void OnJoinAnswered(JoinRaidResultBroadcast result) => _joined.TrySetResult(result);

        private void OnLeft(LeftRaidBroadcast message, Channel channel) => _left.TrySetResult(message.Exit);

        // The server tells us how we left before it disconnects us, so a connection that stops before then failed.
        private void OnClientConnectionState(ClientConnectionStateArgs args)
        {
            if (args.ConnectionState == LocalConnectionState.Stopped)
            {
                var lost = new InvalidOperationException("the connection to the game server stopped before the raid was over.");
                _joined.TrySetException(lost);
                _left.TrySetException(lost);
            }
        }

        private void OnExtraction(ExtractionBroadcast message, Channel channel)
        {
            Debug.Log(Log + (message.Point != null
                ? "Extracting at " + message.Point + ": " + message.SecondsLeft.ToString("0.0") + " s."
                : "Extraction countdown stopped."));
        }
    }
}
