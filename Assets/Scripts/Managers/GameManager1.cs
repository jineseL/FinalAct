using UnityEngine;
using Unity.Netcode;
using System.Collections.Generic;
using System.Linq;

public class GameManager1 : NetworkBehaviour
{
    public static GameManager1 instance { get; private set; }

    [SerializeField] private GameObject playerPrefab;

    // --- cached client ids (includes host) ---
    private readonly List<ulong> _cachedClientIds = new List<ulong>(8);

    void Awake()
    {
        if (instance != null && instance != this) { Destroy(gameObject); return; }
        instance = this;
        DontDestroyOnLoad(gameObject);
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        RebuildPlayerCache();

        // keep cache in sync with joins/leaves
        var nm = NetworkManager.Singleton;
        if (nm != null)
        {
            nm.OnClientConnectedCallback -= OnClientConnected;
            nm.OnClientConnectedCallback += OnClientConnected;

            nm.OnClientDisconnectCallback -= OnClientDisconnected;
            nm.OnClientDisconnectCallback += OnClientDisconnected;
        }
    }

    void OnClientConnected(ulong clientId)
    {
        if (!_cachedClientIds.Contains(clientId))
            _cachedClientIds.Add(clientId);
    }

    void OnClientDisconnected(ulong clientId)
    {
        _cachedClientIds.Remove(clientId);
    }

    void RebuildPlayerCache()
    {
        _cachedClientIds.Clear();
        var nm = NetworkManager.Singleton;
        if (nm == null) return;

        foreach (var cc in nm.ConnectedClientsList)
            _cachedClientIds.Add(cc.ClientId);
    }

    // ----------------- SPAWNING (your existing code) -----------------
    [Rpc(SendTo.Server)]
    public void SpawnPlayerServerRpc(ulong requestingClientId)
    {
        if (!IsServer) return;

        var refs = SceneRefs.Instance;
        if (refs == null) { Debug.LogWarning("SceneRefs not found in this scene."); return; }

        Transform spawn = (requestingClientId == NetworkManager.ServerClientId)
            ? refs.hostSpawn : refs.clientSpawn;

        if (spawn == null) { Debug.LogWarning("Spawn transform missing on SceneRefs."); return; }

        var playerObj = Instantiate(playerPrefab, spawn.position, spawn.rotation);
        playerObj.GetComponent<NetworkObject>().SpawnAsPlayerObject(requestingClientId);
    }

    // ----------------- CAMERA SHAKE (cache-driven) -----------------

    /// <summary>
    /// Shake every player's camera using the cached client list.
    /// Safe to call from server or client. Rebuilds cache if empty.
    /// </summary>
    public void ShakeAllCameras(PlayerCameraShake.Strength strength)
    {
        if (!IsServer)
        {
            // If called from a client, ask the server to fan out.
            RequestShakeAllServerRpc(strength);
            return;
        }

        if (_cachedClientIds.Count == 0)
            RebuildPlayerCache();

        if (_cachedClientIds.Count == 0)
        {
            Debug.LogWarning("ShakeAllCameras: no clients in cache.");
            return;
        }

        var rpc = new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = _cachedClientIds.ToArray() }
        };
        ShakeCamerasClientRpc(strength, rpc);
    }

    [ServerRpc(RequireOwnership = false)]
    void RequestShakeAllServerRpc(PlayerCameraShake.Strength strength)
    {
        // server validates/gates if needed, then reuse the same method
        ShakeAllCameras(strength);
    }

    [ClientRpc]
    void ShakeCamerasClientRpc(PlayerCameraShake.Strength strength, ClientRpcParams rpcParams = default)
    {
        // This runs on every client and shakes *their* local player’s camera
        PlayerCameraShake.ShakeLocal(strength);
    }
}
