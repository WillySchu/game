using UnityEngine;
using FishNet;
using FishNet.Managing;
using RaidZero.Net;
using UnityEngine.SceneManagement;

namespace RaidZero.Client
{
    public class SetupClientComponents : MonoBehaviour
    {
        private NetworkManager _networkManager;
        
        void Awake()
        {
            _networkManager = InstanceFinder.NetworkManager;
            if (_networkManager == null)
            {
                Debug.LogError("[client] No NetworkManager; start from the core scene.");
                return;
            }

            if (_networkManager.gameObject.GetComponent<RaidSessionClient>() == null)
            {
                _networkManager.gameObject.AddComponent<RaidSessionClient>();
            }

            if (_networkManager.gameObject.GetComponent<RaidInventoryClient>() == null)
            {
                _networkManager.gameObject.AddComponent<RaidInventoryClient>();
            }

            // The client half of joining a raid (sending the ticket) lives in the authenticator.
            RaidAuthenticator.Install(_networkManager);

        }

        void Start()
        {
            
            #if BUILD_SERVER
            // This will usually only be the case in editor.
            SceneManager.LoadScene("core.server");
            return;
            #endif

            SceneManager.LoadScene("SampleScene");

        }
    }
}
