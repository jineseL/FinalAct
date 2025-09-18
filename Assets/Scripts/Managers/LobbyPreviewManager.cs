using Unity.Netcode;
using UnityEngine;

public class LobbyPreviewManager : NetworkBehaviour
{
    [SerializeField] private GameObject previewPrefab; // NOT a NetworkObject

    private GameObject p1, p2;

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            // When the network controller spawns on the server, tell everyone to show Player 1
            SpawnPreviewClientRpc(1);

            // Subscribe to join/leave
            NetworkManager.OnClientConnectedCallback += OnClientConnected;
            NetworkManager.OnClientDisconnectCallback += OnClientDisconnected;
        }
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer && NetworkManager != null)
        {
            NetworkManager.OnClientConnectedCallback -= OnClientConnected;
            NetworkManager.OnClientDisconnectCallback -= OnClientDisconnected;
        }
    }

    private void OnDestroy()
    {
        // Clean up local previews when this controller goes away (Back/Shutdown)
        if (p1) Destroy(p1);
        if (p2) Destroy(p2);
    }

    private void OnClientConnected(ulong clientId)
    {
        if (!IsServer) return;

        // A new client just connected.
        // 1) Ensure the new client gets Player 1 (they missed the earlier RPC):
        var onlyNewClient = new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } }
        };
        SpawnPreviewClientRpc(1, onlyNewClient);

        // 2) Broadcast Player 2 to everyone (host and new client)
        SpawnPreviewClientRpc(2);
    }

    private void OnClientDisconnected(ulong clientId)
    {
        if (!IsServer) return;

        // Client left, clear Player 2 on everyone
        ClearPreviewClientRpc(2);
    }

    // --------- RPCs ---------

    [ClientRpc]
    private void SpawnPreviewClientRpc(int slot, ClientRpcParams rpcParams = default)
    {
        Transform spawn = slot == 1 ? LobbySceneRefs.Instance.player1Spawn
                                    : LobbySceneRefs.Instance.player2Spawn;

        if (slot == 1)
        {
            //if (p1 == null) p1 = Instantiate(previewPrefab, spawn.position, spawn.rotation);
            if (p1 == null) p1 = Instantiate(previewPrefab, spawn);
        }
        else
        {
            //if (p2 == null) p2 = Instantiate(previewPrefab, spawn.position, spawn.rotation);
            if (p2 == null) p2 = Instantiate(previewPrefab, spawn);
        }
    }

    [ClientRpc]
    private void ClearPreviewClientRpc(int slot, ClientRpcParams rpcParams = default)
    {
        if (slot == 1)
        {
            if (p1) { Destroy(p1); p1 = null; }
        }
        else
        {
            if (p2) { Destroy(p2); p2 = null; }
        }
    }
}
