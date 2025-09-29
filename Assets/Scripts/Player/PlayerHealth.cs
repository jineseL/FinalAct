using Unity.Netcode;
using UnityEngine;
using System.Collections;

public class PlayerHealth : NetworkBehaviour
{
    [SerializeField] private int maxHealth = 100;

    // Server writes, everyone reads
    private NetworkVariable<int> currentHealth =
        new NetworkVariable<int>(
            0,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

    [SerializeField] private float invulnerableTime = 1f;

    // server-only runtime state
    private bool isInvulnerable = false;
    private Coroutine invulnRoutine;

    public int MaxHealth => maxHealth;
    public int CurrentHealth => currentHealth.Value;
    public bool IsAlive => currentHealth.Value > 0;

    // Call from your PlayerManager.OnNetworkSpawn()
    public void InitializePlayerHealth()
    {
        WireCallbacks();
        if (IsServer) currentHealth.Value = maxHealth;
    }

    public override void OnNetworkSpawn()
    {
        WireCallbacks();
        if (IsServer && currentHealth.Value <= 0) // fresh spawn
            currentHealth.Value = maxHealth;
    }

    public override void OnNetworkDespawn()
    {
        UnwireCallbacks();
    }

    private void WireCallbacks()
    {
        currentHealth.OnValueChanged -= OnHealthChanged;
        currentHealth.OnValueChanged += OnHealthChanged;
    }

    private void UnwireCallbacks()
    {
        currentHealth.OnValueChanged -= OnHealthChanged;
    }

    private void OnHealthChanged(int oldValue, int newValue)
    {
        if (newValue <= 0)
            Die();
    }

    // ======= DAMAGE API =======

    /// <summary>
    /// Server-side direct damage (preferred for boss/AoE/weapon hits).
    /// no knockback version of taking damage
    /// </summary>
    public void ApplyDamage(int amount, ulong attackerClientId = ulong.MaxValue)
    {
        if (!IsServer || !IsAlive) return;
        if (isInvulnerable) return;

        currentHealth.Value = Mathf.Clamp(currentHealth.Value - Mathf.Abs(amount), 0, maxHealth);

        // Local hit feedback for the victim client only
        var no = GetComponent<NetworkObject>();
        if (no != null)
        {
            var targetClientId = no.OwnerClientId;
            HitFeedbackClientRpc(new ClientRpcParams
            {
                Send = new ClientRpcSendParams { TargetClientIds = new[] { targetClientId } }
            });
        }

        if (currentHealth.Value > 0)
        {
            SetInvulnerable(invulnerableTime);
        }
    }

    // ======= NEW: Damage + knockback variants =======

    /// <summary>
    /// Server-side damage with a world-space knockback force vector.
    /// knockback version, when you want to make player get knockback when getting hit
    /// </summary>
    public void ApplyDamage(int amount, Vector3 knockbackForce, ulong attackerClientId = ulong.MaxValue)
    {
        if (!IsServer || !IsAlive) return;
        if (isInvulnerable) return;

        currentHealth.Value = Mathf.Clamp(currentHealth.Value - Mathf.Abs(amount), 0, maxHealth);

        var no = GetComponent<NetworkObject>();
        if (no != null)
        {
            var targetClientId = no.OwnerClientId;

            // Send knockback to the owning client so their motor applies it locally
            KnockbackClientRpc(knockbackForce, new ClientRpcParams
            {
                Send = new ClientRpcSendParams { TargetClientIds = new[] { targetClientId } }
            });

            HitFeedbackClientRpc(new ClientRpcParams
            {
                Send = new ClientRpcSendParams { TargetClientIds = new[] { targetClientId } }
            });
        }

        if (currentHealth.Value > 0)
        {
            SetInvulnerable(invulnerableTime);
        }
    }

    /// <summary>
    /// Server-side damage from an origin point in space, with magnitude and optional upward bias.
    /// Good for explosions and gravity pulls.
    /// </summary>
    public void ApplyDamageFromPoint(int amount, Vector3 origin, float forceMagnitude, float upBias = 0f, ulong attackerClientId = ulong.MaxValue)
    {
        var force = ComputeKnockbackFromOrigin(origin, forceMagnitude, upBias);
        ApplyDamage(amount, force, attackerClientId);
    }

    /// <summary>
    /// Server-side damage from a hit point (contact point), with magnitude and optional upward bias.
    /// </summary>
    public void ApplyDamageFromHitPoint(int amount, Vector3 hitPoint, float forceMagnitude, float upBias = 0f, ulong attackerClientId = ulong.MaxValue)
    {
        var force = ComputeKnockbackFromOrigin(hitPoint, forceMagnitude, upBias);
        ApplyDamage(amount, force, attackerClientId);
    }

    /// <summary>
    /// Server-side knockback without damage.
    /// </summary>
    public void ApplyKnockbackOnly(Vector3 knockbackForce)
    {
        if (!IsServer) return;

        var no = GetComponent<NetworkObject>();
        if (no == null) return;

        var targetClientId = no.OwnerClientId;
        KnockbackClientRpc(knockbackForce, new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = new[] { targetClientId } }
        });
    }

    // Helper to compute a good force based on an origin and optional upward bias
    private Vector3 ComputeKnockbackFromOrigin(Vector3 origin, float forceMagnitude, float upBias)
    {
        Vector3 center;
        var cc = GetComponent<CharacterController>();
        if (cc != null)
            center = cc.transform.TransformPoint(cc.center);
        else
            center = transform.position;

        Vector3 dir = center - origin;
        if (dir.sqrMagnitude < 0.0001f) dir = transform.forward;
        dir.Normalize();

        if (upBias != 0f)
        {
            dir.y += Mathf.Abs(upBias);
            dir.Normalize();
        }

        return dir * Mathf.Max(0f, forceMagnitude);
    }

    /// <summary>
    /// Client-side application of the force on the victim only.
    /// </summary>
    [ClientRpc]
    private void KnockbackClientRpc(Vector3 force, ClientRpcParams p = default)
    {
        var po = NetworkManager.Singleton.LocalClient?.PlayerObject;
        if (!po) return;

        var motor = po.GetComponent<PlayerMotor>();
        if (motor != null)
        {
            motor.ApplyExternalForce(force);
            motor.LockExternalForces(invulnerableTime); // lock new external forces for the invuln duration
        }
    }

    // ======= RPCs and utilities =======

    /// <summary>
    /// Client requests damage (e.g., self-damage, environmental on owner). Server validates and applies.
    /// </summary>
    [ServerRpc(RequireOwnership = false)]
    public void RequestDamageServerRpc(int amount)
    {
        ApplyDamage(amount);
    }

    public void Heal(int amount)
    {
        if (!IsServer || !IsAlive) return;
        currentHealth.Value = Mathf.Clamp(currentHealth.Value + Mathf.Abs(amount), 0, maxHealth);
    }

    public void SetInvulnerable(float duration)
    {
        if (!IsServer) return;
        if (invulnRoutine != null) StopCoroutine(invulnRoutine);
        invulnRoutine = StartCoroutine(InvulnerabilityCoroutine(duration));
    }

    private IEnumerator InvulnerabilityCoroutine(float duration)
    {
        isInvulnerable = true;
        yield return new WaitForSeconds(duration);
        isInvulnerable = false;
        invulnRoutine = null;
    }

    [ServerRpc(RequireOwnership = false)]
    public void MakeInvulnerableServerRpc()
    {
        SetInvulnerable(invulnerableTime);
    }

    [ClientRpc]
    private void HitFeedbackClientRpc(ClientRpcParams p = default)
    {
        // camera shake / flash / sound on victim client only
    }

    private void Die()
    {
        if (IsServer)
        {
            // server-driven death handling
        }
    }
}
