using UnityEngine;
using FishNet;
using FishNet.Managing;
using RaidZero.Net;
using UnityEngine.SceneManagement;

namespace RaidZero.Server
{
    public class SetupServerComponents : MonoBehaviour
    {

        private NetworkManager _networkManager;
        void Awake()
        {
        _networkManager = InstanceFinder.NetworkManager;

        if (_networkManager.gameObject.GetComponent<RaidServer>() == null)
        {
            _networkManager.gameObject.AddComponent<RaidServer>();
        }

        // Registers it with the ServerManager too; adding the component alone leaves Fish-Net without one.
        RaidAuthenticator.Install(_networkManager);

        }

        void Start()
        {
            SceneManager.LoadScene("SampleScene");
        }

    }
}