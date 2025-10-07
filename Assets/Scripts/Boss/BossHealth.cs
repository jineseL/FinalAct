using Unity.Netcode;
using UnityEngine;

[DisallowMultipleComponent]
public class BossHealth : NetworkBehaviour
{
    [SerializeField] private float maxHPStart = 300f;

    private NetworkVariable<float> maxHp =
        new NetworkVariable<float>(300f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private NetworkVariable<float> hp = new(300f,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    public float MaxHP => maxHp.Value;
    public float CurrentHP => hp.Value;
    public bool IsAlive => hp.Value > 0f;

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            maxHp.Value = Mathf.Max(1f, maxHPStart);
            hp.Value = maxHp.Value;                // start full
        }
        gameObject.tag = "Boss";
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

    // --- Max HP controls (server-only) ---

    /// Set a new max HP. If refill=true, fill to new max; otherwise preserve current %.
    public void SetMaxHp(float newMax, bool refill)
    {
        if (!IsServer) return;

        newMax = Mathf.Max(1f, newMax);
        float pct = (maxHp.Value > 0f) ? (hp.Value / maxHp.Value) : 1f;

        maxHp.Value = newMax;
        hp.Value = refill ? newMax : Mathf.Clamp(newMax * pct, 0f, newMax);
    }

    /// Scale current max HP by a factor. If refill=true, fill to new max; else keep same %.
    public void ScaleMaxHp(float factor, bool refill)
    {
        if (!IsServer) return;
        SetMaxHp(maxHp.Value * Mathf.Max(0.01f, factor), refill);
    }
}
