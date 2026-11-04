using UnityEngine;
using UnityEngine.SceneManagement;
namespace RaidZero.World
{
    public class CoreLoader : MonoBehaviour
    {
        void Start()
        {
            #if BUILD_CLIENT
            SceneManager.LoadScene("core.client");
            return;
            #elif BUILD_SERVER
            SceneManager.LoadScene("core.server");
            return;
            #endif

        }

    }
}
