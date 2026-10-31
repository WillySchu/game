using UnityEngine;

public class KeepPersistentTags : MonoBehaviour
{
    private void Awake()
    {
        // Find all GameObjects with the "Persistent" tag and make them persistent across scene loads.
        GameObject[] objects = GameObject.FindGameObjectsWithTag("Persistent");
        foreach (GameObject obj in objects)
        {
            DontDestroyOnLoad(obj);
        }
    }
}
