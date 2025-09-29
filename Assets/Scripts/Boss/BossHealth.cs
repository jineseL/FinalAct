using Unity.Netcode;
using UnityEngine;

[DisallowMultipleComponent]
public class BossHealth : NetworkBehaviour
{
    [SerializeField] private float maxHP = 1000f;
    private NetworkVariable<float> hp = new(1000f,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    public float MaxHP => maxHP;
    public float CurrentHP => hp.Value;
    public bool IsAlive => hp.Value > 0f;

    public override void OnNetworkSpawn()
    {
        if (IsServer) hp.Value = maxHP;
        gameObject.tag = "Boss"; // optional for finding
    }

    [ServerRpc(RequireOwnership = false)]
    public void TakeDamageServerRpc(float dmg)
    {
        if (!IsServer) return;
        hp.Value = Mathf.Max(0, hp.Value - dmg);
    }

    // Server direct call
    public void ApplyDamage(float dmg)
    {
        if (!IsServer) return;
        hp.Value = Mathf.Max(0, hp.Value - dmg);
    }
}
