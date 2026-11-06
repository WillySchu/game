using UnityEngine;
using FishNet;
using FishNet.Managing;
using RaidZero.Net;
using RaidZero.World;
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
            // A host: the server half comes next, and it loads the map.
            if (ProcessRole.IsServer)
            {
                SceneManager.LoadScene("core.server");
                return;
            }

            SceneManager.LoadScene("SampleScene");
        }
    }
}
