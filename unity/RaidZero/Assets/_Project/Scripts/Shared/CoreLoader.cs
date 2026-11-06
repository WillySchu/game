using UnityEngine;
using UnityEngine.SceneManagement;
namespace RaidZero.World
{
    public class CoreLoader : MonoBehaviour
    {
        // A client (or a host, which is both) sets up its client half first; core.client goes on to core.server
        // when this process is a server too. ProcessRole says which this is: a build's profile, or in the editor
        // the multiplayer role chosen in the toolbar.
        void Start()
        {
            Debug.Log("[core] Starting as " + ProcessRole.Describe() + ".");
            if (ProcessRole.IsClient)
            {
                SceneManager.LoadScene("core.client");
            }
            else if (ProcessRole.IsServer)
            {
                SceneManager.LoadScene("core.server");
            }
            else
            {
                Debug.LogError("[core] This process is neither a client nor a server, so nothing loads. Builds need the BUILD_CLIENT or BUILD_SERVER define (from their build profile); in the editor, pick a multiplayer role in the toolbar.");
            }
        }
    }
}
