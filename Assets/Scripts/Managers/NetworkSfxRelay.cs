using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(NetworkObject))]
public class NetworkSfxRelay : NetworkBehaviour
{
    public static NetworkSfxRelay Instance { get; private set; }

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject); // optional if you persist this across scenes
    }

    public override void OnNetworkDespawn()
    {
        if (Instance == this) Instance = null;
    }

    // ================= Server APIs (call these from server/host code) =================

    public static void All_Play2D(string key, float volume = 1f, float pitch = 1f)
    {
        if (Instance != null && Instance.IsServer)
            Instance.Play2DClientRpc(key, volume, pitch);
    }

    public static void All_PlayAt(string key, Vector3 pos, float volume = 1f, float pitch = 1f, float spatial = 1f, float minDistance = 1f, float maxDistance = 25f)
    {
        if (Instance != null && Instance.IsServer)
            Instance.PlayAtClientRpc(key, pos, volume, pitch, spatial, minDistance, maxDistance);
    }

    public static void ToClient_Play2D(ulong clientId, string key, float volume = 1f, float pitch = 1f)
    {
        if (!(Instance != null && Instance.IsServer)) return;
        var p = new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } } };
        Instance.Play2DClientRpc(key, volume, pitch, p);
    }

    public static void ToClient_PlayAt(ulong clientId, string key, Vector3 pos, float volume = 1f, float pitch = 1f, float spatial = 1f, float minDistance = 1f, float maxDistance = 25f)
    {
        if (!(Instance != null && Instance.IsServer)) return;
        var p = new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } } };
        Instance.PlayAtClientRpc(key, pos, volume, pitch, spatial, minDistance, maxDistance, p);
    }

    // ================= RPCs (run on clients) =================

    [ClientRpc]
    private void Play2DClientRpc(string key, float volume, float pitch, ClientRpcParams p = default)
    {
        SoundManager.PlaySfx(key, volume, pitch);
    }

    [ClientRpc]
    private void PlayAtClientRpc(string key, Vector3 pos, float volume, float pitch, float spatial, float minDistance, float maxDistance, ClientRpcParams p = default)
    {
        SoundManager.PlaySfxAt(key, pos, volume, pitch, spatial, minDistance, maxDistance);
    }
}
