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

        _networkManager.gameObject.AddComponent<RaidServer>();
        _networkManager.gameObject.AddComponent<RaidAuthenticator>();

        }

        void Start()
        {
            SceneManager.LoadScene("SampleScene");
        }

    }
}