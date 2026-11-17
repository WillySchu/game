using FishNet.Managing;
using FishNet.Transporting;
using UnityEngine;

namespace RaidZero.Net
{
    /// <summary>
    /// The F1 overlay's AI debug view: with it on, a label over every AI says what it's doing (calm, heard,
    /// squad, noticed, alert) and who it's aware of, with a bar for how aware, and a dotted line to where it's
    /// looking. It comes from the server (DevAIDebugBroadcast) five times a second, and only from servers in the
    /// editor and Development builds; a release server ignores the request, so nobody can see its AI this way.
    /// The brain's state, its path and its cover join the label in step 4.
    /// </summary>
    public sealed class AIDebugView
    {
        private const float AskEverySeconds = 2f;   // so a server we reconnect to (or a new raid's) knows too
        private const float StaleAfterSeconds = 1f;

        private NetworkManager _registeredWith;
        private AIDebugEntry[] _entries = System.Array.Empty<AIDebugEntry>();
        private float _receivedAt;
        private float _askedAt = float.NegativeInfinity;
        private GUIStyle _style;

        public bool On { get; private set; }

        public void Toggle(NetworkManager networkManager, bool on)
        {
            if (on == On || networkManager == null)
            {
                return;
            }

            On = on;
            if (on && _registeredWith != networkManager)
            {
                Unregister();
                networkManager.ClientManager.RegisterBroadcast<DevAIDebugBroadcast>(OnEntries);
                _registeredWith = networkManager;
            }

            _askedAt = float.NegativeInfinity;
            if (!on)
            {
                _entries = System.Array.Empty<AIDebugEntry>();
                Ask(networkManager, false);
            }
        }

        /// <summary>Call every frame: keeps the server sending while it's on.</summary>
        public void Tick(NetworkManager networkManager)
        {
            if (On && networkManager != null && Time.unscaledTime - _askedAt >= AskEverySeconds)
            {
                Ask(networkManager, true);
            }
        }

        public void Unregister()
        {
            if (_registeredWith != null)
            {
                _registeredWith.ClientManager.UnregisterBroadcast<DevAIDebugBroadcast>(OnEntries);
                _registeredWith = null;
            }
        }

        private void Ask(NetworkManager networkManager, bool watch)
        {
            _askedAt = Time.unscaledTime;
            if (networkManager.ClientManager.Started)
            {
                networkManager.ClientManager.Broadcast(new DevWatchAIBroadcast { Watch = watch });
            }
        }

        private void OnEntries(DevAIDebugBroadcast message, Channel channel)
        {
            if (On)
            {
                _entries = message.Entries ?? System.Array.Empty<AIDebugEntry>();
                _receivedAt = Time.unscaledTime;
            }
        }

        /// <summary>Call from OnGUI: the labels, bars and lines.</summary>
        public void Draw()
        {
            var camera = Camera.main;
            if (!On || camera == null || Time.unscaledTime - _receivedAt > StaleAfterSeconds)
            {
                return;
            }

            _style ??= new GUIStyle(GUI.skin.label) { fontSize = 12, richText = false };
            var previous = GUI.color;
            foreach (var entry in _entries)
            {
                if (!ToScreen(camera, entry.LabelAt, out var at))
                {
                    continue;
                }

                var colour = ColourOf(entry.State);
                if (entry.HasLookPoint && ToScreen(camera, entry.LookPoint, out var look))
                {
                    GUI.color = colour;
                    for (int i = 1; i < 12; i++)
                    {
                        var dot = Vector2.Lerp(at, look, i / 12f);
                        GUI.DrawTexture(new Rect(dot.x - 1.5f, dot.y - 1.5f, 3f, 3f), Texture2D.whiteTexture);
                    }

                    GUI.Label(new Rect(look.x - 6f, look.y - 9f, 160f, 20f), "x " + entry.State, _style);
                }

                var text = entry.State.ToUpperInvariant() + "  " + entry.Detail;
                var size = _style.CalcSize(new GUIContent(text));
                var box = new Rect(at.x - size.x * 0.5f, at.y - size.y - 8f, size.x, size.y);

                GUI.color = new Color(0f, 0f, 0f, 0.6f);
                GUI.DrawTexture(new Rect(box.x - 4f, box.y - 2f, box.width + 8f, box.height + 10f), Texture2D.whiteTexture);
                GUI.color = colour;
                GUI.Label(box, text, _style);
                GUI.DrawTexture(new Rect(box.x, box.yMax + 2f, box.width * Mathf.Clamp01(entry.Awareness), 4f), Texture2D.whiteTexture);
            }

            GUI.color = previous;
        }

        private static bool ToScreen(Camera camera, Vector3 world, out Vector2 screen)
        {
            var point = camera.WorldToScreenPoint(world);
            screen = new Vector2(point.x, Screen.height - point.y);
            return point.z > 0f;
        }

        private static Color ColourOf(string state) => state switch
        {
            "alert" => new Color(1f, 0.3f, 0.25f),
            "noticed" => new Color(1f, 0.8f, 0.2f),
            "heard" or "squad" => new Color(0.45f, 0.75f, 1f),
            _ => new Color(0.8f, 0.8f, 0.8f),
        };
    }
}
