using UnityEngine;
using Unity.Netcode;
public class GameManager1:NetworkBehaviour
{
    //for testing 
    /// <summary>
    /// this script is for testing in the editor
    /// </summary>
    //[SerializeField] GameObject cameraGO;


    public static GameManager1 instance { get; private set; }

    [SerializeField] private GameObject playerPrefab;

    private void Awake()
    {
        if (instance != null && instance != this) { Destroy(gameObject); return; }
        instance = this;
        DontDestroyOnLoad(gameObject); // persists across scenes
    }

    [Rpc(SendTo.Server)]
    public void SpawnPlayerServerRpc(ulong requestingClientId)
    {
        if (!IsServer) return;

        var refs = SceneRefs.Instance;
        if (refs == null)
        {
            Debug.LogWarning("SceneRefs not found in this scene.");
            return;
        }

        // host vs client spawn
        Transform spawn = (requestingClientId == NetworkManager.ServerClientId)
            ? refs.hostSpawn
            : refs.clientSpawn;

        if (spawn == null)
        {
            Debug.LogWarning("Spawn transform missing on SceneRefs.");
            return;
        }

        var playerObj = Instantiate(playerPrefab, spawn.position, spawn.rotation);
        playerObj.GetComponent<NetworkObject>().SpawnAsPlayerObject(requestingClientId);
    }
}
