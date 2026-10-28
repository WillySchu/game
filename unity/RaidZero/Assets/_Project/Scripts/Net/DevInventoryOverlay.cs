using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using FishNet.Managing;
using RaidZero.Items;
using RaidZero.Shared.Inventory;
using RaidZero.Shared.Raid;
using UnityEngine;

namespace RaidZero.Net
{
    /// <summary>
    /// A throwaway debug screen for the raid inventory, until the real one exists. Press F1 in play mode:
    /// it lists your gear and anything you have open, opens the nearest crate, and takes or drops items.
    /// A line in the top right always shows the raid's phase, its clock, any extraction countdown, and how
    /// you left. When not connected, the screen has a Deploy button that goes through the Hideout's path:
    /// a dev login, a ticket, and a connection to the server the ticket names (a dedicated server on a VPS,
    /// say), instead of the Multiplayer Play Mode buttons' local address.
    /// Everything goes through RaidInventoryClient exactly as the real inventory screen will, so it
    /// exercises the whole path: client, Fish-Net, server, the raid's rules, and back.
    /// Put it on any GameObject in the raid scene. It does nothing in release builds.
    /// </summary>
    public sealed class DevInventoryOverlay : MonoBehaviour
    {
        [SerializeField]
        private float _openRange = 2.5f;

        private bool _visible;
        private bool _deploying;
        private string _lastResult = "";
        private Vector2 _scroll;

        private void Start()
        {
            if (RaidInventoryClient.Instance != null)
            {
                RaidInventoryClient.Instance.View.RequestCompleted += result =>
                    _lastResult = result.Success ? "ok" : "refused: " + result.Error + (result.FailedOpIndex.HasValue ? " (op " + result.FailedOpIndex + ")" : "");
            }
        }

        private void OnGUI()
        {
            if (!Application.isEditor && !Debug.isDebugBuild)
            {
                return;
            }

            var e = Event.current;
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.F1)
            {
                _visible = !_visible;
                Cursor.lockState = _visible ? CursorLockMode.None : CursorLockMode.Locked;
                Cursor.visible = _visible;
                e.Use();
            }

            DrawRaidStatus();

            var client = RaidInventoryClient.Instance;
            if (!_visible || client == null)
            {
                return;
            }

            GUILayout.BeginArea(new Rect(10, 10, 460, Screen.height - 20), GUI.skin.box);
            _scroll = GUILayout.BeginScrollView(_scroll);
            GUILayout.Label("Raid inventory (F1 to close)   last request: " + _lastResult);
            DrawDeploy();

            var character = client.View.Character;
            if (character == null)
            {
                GUILayout.Label("No gear yet: not joined to a raid.");
            }
            else
            {
                GUILayout.Label("Your gear");
                DrawTree(client, character.Root, 0, isMine: true);
            }

            // A copy, because the Close button below removes a container from the view straight away.
            foreach (var view in new List<ContainerView>(client.View.Containers.Values))
            {
                if (view.Kind == RootKind.Character)
                {
                    continue;
                }

                GUILayout.Space(8);
                GUILayout.BeginHorizontal();
                GUILayout.Label(view.Kind + " " + view.Root.DefId);
                if (GUILayout.Button("Close", GUILayout.Width(60)))
                {
                    client.Close(view.Root.Id);
                }

                GUILayout.EndHorizontal();
                DrawTree(client, view.Root, 0, isMine: false);
            }

            GUILayout.Space(8);
            if (GUILayout.Button("Open the nearest crate"))
            {
                OpenNearest(client);
            }

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private void DrawDeploy()
        {
            var networkManager = ClientNetworkManager();
            if (networkManager == null || networkManager.ClientManager.Started || networkManager.ServerManager.Started)
            {
                return;
            }

            GUILayout.Label("Backend: " + Hideout.BackendUrl + "   map: " + DevSettings.MapId);
            GUI.enabled = !_deploying;
            if (GUILayout.Button(_deploying ? "Deploying..." : "Deploy (dev login, ticket, connect to the ticket's server)"))
            {
                DeployAsync(networkManager);
            }

            GUI.enabled = true;
        }

        private async void DeployAsync(NetworkManager networkManager)
        {
            _deploying = true;
            try
            {
                if (!Hideout.Session.IsLoggedIn && !await NewDevPlayerAsync())
                {
                    return;
                }

                var deployed = await Hideout.DeployAsync(networkManager, DevSettings.MapId);

                // This dev player is still in a raid this client has no ticket for (the editor forgot it, or it
                // was another machine's). Dev players are throwaway, so deploy as a fresh one instead of waiting.
                if (deployed.Error == "already_in_raid")
                {
                    Debug.Log("[raid] This dev player is still in raid " + deployed.RaidId + "; deploying as a new dev player.");
                    if (!await NewDevPlayerAsync())
                    {
                        return;
                    }

                    deployed = await Hideout.DeployAsync(networkManager, DevSettings.MapId);
                }

                _lastResult = !deployed.Succeeded
                    ? "no ticket: " + deployed
                    : (deployed.Rejoined ? "rejoining your raid at " : "connecting to ") + deployed.Ticket.ServerHost + ":" + deployed.Ticket.ServerPort;
            }
            catch (System.Exception exception)
            {
                _lastResult = "deploy failed: " + exception.Message;
            }
            finally
            {
                _deploying = false;
            }
        }

        private async Task<bool> NewDevPlayerAsync()
        {
            Hideout.ForgetLastTicket();
            var login = await Hideout.Session.DevLoginAsync("dev-" + UnityEngine.Random.Range(10000, 99999));
            if (!login.Succeeded)
            {
                _lastResult = "dev login failed: " + login;
            }

            return login.Succeeded;
        }

        private static NetworkManager ClientNetworkManager() =>
            RaidInventoryClient.Instance != null ? RaidInventoryClient.Instance.GetComponent<NetworkManager>() : null;

        private static void DrawRaidStatus()
        {
            var session = RaidSessionClient.Instance;
            if (session == null)
            {
                return;
            }

            var text = new StringBuilder("Raid: ").Append(session.Phase);
            if (session.Phase == RaidPhase.InProgress)
            {
                var seconds = Mathf.CeilToInt(session.SecondsRemaining);
                text.Append("  ").Append(seconds / 60).Append(':').Append((seconds % 60).ToString("00"));
            }

            if (session.IsExtracting)
            {
                text.Append("  |  extracting at ").Append(session.ExtractionPoint).Append(": ")
                    .Append(Mathf.CeilToInt(session.ExtractionSecondsLeft)).Append(" s");
            }

            if (session.Exit.HasValue)
            {
                text.Append("  |  you left: ").Append(session.Exit.Value);
            }

            GUI.Label(new Rect(Screen.width - 430, 10, 420, 24), text.ToString(), GUI.skin.box);
        }

        private void DrawTree(RaidInventoryClient client, ItemNode node, int depth, bool isMine)
        {
            foreach (var child in node.Children)
            {
                GUI.enabled = !client.View.IsPending(child.Id);
                GUILayout.BeginHorizontal();
                GUILayout.Label(new string(' ', (depth + 1) * 3) + Describe(child));
                if (GUILayout.Button(isMine ? "Drop" : "Take", GUILayout.Width(60)))
                {
                    if (isMine)
                    {
                        client.Drop(child.Id);
                    }
                    else
                    {
                        client.PickUp(child.Id);
                    }
                }

                GUILayout.EndHorizontal();
                GUI.enabled = true;
                DrawTree(client, child, depth + 1, isMine);
            }
        }

        private static string Describe(ItemNode item)
        {
            var text = new StringBuilder(ItemLibrary.DisplayNameOf(item.DefId));
            if (item.Quantity > 1)
            {
                text.Append(" x").Append(item.Quantity);
            }

            text.Append("  [").Append(item.Slot);
            if (item.X.HasValue)
            {
                text.Append(' ').Append(item.X).Append(',').Append(item.Y).Append(item.Rotated ? " r" : "");
            }

            return text.Append(']').ToString();
        }

        private void OpenNearest(RaidInventoryClient client)
        {
            var from = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
            LootContainer nearest = null;
            float best = _openRange;
            foreach (var crate in FindObjectsByType<LootContainer>(FindObjectsSortMode.None))
            {
                var distance = Vector3.Distance(from, crate.transform.position);
                if (distance <= best)
                {
                    best = distance;
                    nearest = crate;
                }
            }

            if (nearest == null)
            {
                _lastResult = "no crate within " + _openRange + " m";
                return;
            }

            client.Open(nearest.ContainerId);
        }
    }
}
