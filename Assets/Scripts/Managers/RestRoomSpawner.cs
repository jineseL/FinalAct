using Unity.Netcode;
using UnityEngine;

public class RestRoomSpawner : NetworkBehaviour
{
    [SerializeField] private GameObject playerPrefab;
    [SerializeField] private Transform hostPositionSpawn;
    [SerializeField] private Transform clientPositionSpawn;

    public override void OnNetworkSpawn()
    {
        if (!IsServer) return;

        // Spawn for anyone already connected (host & any client)
        foreach (var clientId in NetworkManager.ConnectedClientsIds)
            TrySpawnForClient(clientId);
    }

    // Client calls this after scene loads to request their spawn
    [Rpc(SendTo.Server)]
    public void RequestSpawnRpc(RpcParams rpcParams = default)
    {
        var senderId = rpcParams.Receive.SenderClientId;
        TrySpawnForClient(senderId);
    }

    private void TrySpawnForClient(ulong clientId)
    {
        var cc = NetworkManager.ConnectedClients[clientId];

        // Prevent double-spawn
        if (cc.PlayerObject != null && cc.PlayerObject.IsSpawned)
            return;

        // Pick spawn: host vs client
        Transform spawn = (clientId == NetworkManager.ServerClientId)
            ? hostPositionSpawn
            : clientPositionSpawn;

        if (spawn == null)
        {
            Debug.LogWarning("Spawn transform missing on RestRoomSpawner.");
            return;
        }

        var go = Instantiate(playerPrefab, spawn.position, spawn.rotation);
        go.GetComponent<NetworkObject>().SpawnAsPlayerObject(clientId);
    }
}

