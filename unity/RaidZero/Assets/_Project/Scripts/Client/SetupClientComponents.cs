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

            if(_networkManager != null && _networkManager.gameObject.GetComponent<RaidSessionClient>() == null)
            {
                _networkManager.gameObject.AddComponent<RaidSessionClient>();
            }

            _networkManager.gameObject.AddComponent<RaidInventoryClient>();

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
