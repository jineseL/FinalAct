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
    [Header("Downed/Revive")]
    [SerializeField] private float downedBleedoutTime = 10f; // seconds allowed to revive
    [SerializeField] private float reviveTime = 3f;          // hold E duration
    [SerializeField] private float reviveHpPct = 0.5f;       // 50% HP when revived
    [SerializeField] private float fakeDeathHpPct = 0.25f;   // 25% HP when no revive

    // everyone reads, server writes
    private NetworkVariable<bool> isDownedNV = new NetworkVariable<bool>(
        false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private NetworkVariable<float> reviveProgressNV = new NetworkVariable<float>(
        0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public bool IsDowned => isDownedNV.Value;
    public float ReviveProgress01 => reviveProgressNV.Value;

    // server-only runtime
    private Coroutine bleedoutRoutine;
    private Coroutine reviveRoutine;

    [SerializeField] GameObject DieText;
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

        isDownedNV.OnValueChanged += OnDownedChanged;
        reviveProgressNV.OnValueChanged += OnReviveProgressChanged;
    }

    public override void OnNetworkDespawn()
    {
        UnwireCallbacks();
        isDownedNV.OnValueChanged -= OnDownedChanged;
        reviveProgressNV.OnValueChanged -= OnReviveProgressChanged;
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
    private void OnDownedChanged(bool oldV, bool newV)
    {
        // Toggle local presentation for the owner
        if (IsOwner)
            SetDownedPresentation(newV);
    }
    private void OnReviveProgressChanged(float oldV, float newV)
    {
        // Optional: owner or reviver HUD can observe this NetworkVariable to show a bar
    }

    private void SetDownedPresentation(bool down)
    {
        // Disable owner input + show red overlay
        var input = GetComponentInChildren<InputManager>(true);
        if (input) input.enabled = !down;

        // Freeze movement a bit more if you want:
        var motor = GetComponent<PlayerMotor>();
        if (motor) motor.ClearSlow(); // optional
        // Red overlay (if you have a HUD)
        var hud = GetComponentInChildren<PlayerHUD>(true);
        if (hud) hud.SetDownedOverlay(down); // implement SetDownedOverlay(bool) in your HUD
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
        if (!IsServer) return;

        // If already downed (double kill), just fake-death immediately
        if (IsDowned)
        {
            ServerFakeRespawn(fakeDeathHpPct);
            DieText.SetActive(true);
            return;
        }

        // Coop? If 2 players are in game, enter downed. Else solo: fake-death.
        bool hasTeammate = NetworkManager.Singleton != null && NetworkManager.Singleton.ConnectedClients.Count >= 2;
        if (hasTeammate)
        {
            EnterDowned();
            DieText.SetActive(true);
        }
        else
        {
            ServerFakeRespawn(fakeDeathHpPct);
        }
    }
    // ======= DOWNED / REVIVE =======

    private void EnterDowned()
    {
        if (!IsServer) return;
        if (IsDowned) return;

        // Clamp HP to 0 and mark downed
        currentHealth.Value = 0;
        isDownedNV.Value = true;
        reviveProgressNV.Value = 0f;

        // Lock knockbacks for a tiny bit so body doesn't get tossed
        SetInvulnerable(invulnerableTime);

        // start bleed-out timer
        if (bleedoutRoutine != null) StopCoroutine(bleedoutRoutine);
        bleedoutRoutine = StartCoroutine(BleedoutTimer());
    }

    private IEnumerator BleedoutTimer()
    {
        float tEnd = Time.time + downedBleedoutTime;
        while (Time.time < tEnd && IsDowned)
            yield return null;

        bleedoutRoutine = null;

        if (IsDowned) // not revived in time
            ServerFakeRespawn(fakeDeathHpPct);
    }

    // Called by the revive interaction when hold begins
    public void ServerBeginRevive(GameObject helper)
    {
        if (!IsServer || !IsDowned || helper == null) return;
        if (reviveRoutine != null) return;

        reviveRoutine = StartCoroutine(ReviveHold(helper));
    }

    // Called by the revive interaction if helper stops/cancels
    public void ServerCancelRevive(GameObject helper)
    {
        if (!IsServer) return;
        if (reviveRoutine != null)
        {
            StopCoroutine(reviveRoutine);
            reviveRoutine = null;
        }
        reviveProgressNV.Value = 0f;
    }

    private IEnumerator ReviveHold(GameObject helper)
    {
        float started = Time.time;
        reviveProgressNV.Value = 0f;

        const float reqDistance = 3.0f;

        while (IsDowned && (Time.time - started) < reviveTime)
        {
            if (helper == null) { reviveProgressNV.Value = 0f; break; }

            float dist = Vector3.Distance(helper.transform.position, transform.position);
            if (dist > reqDistance) { reviveProgressNV.Value = 0f; break; }

            reviveProgressNV.Value = Mathf.Clamp01((Time.time - started) / reviveTime);
            yield return null;
        }

        reviveRoutine = null;

        // Downed flag flipped off while we were reviving (revived by someone else or fake-death)
        if (!IsDowned) yield break;

        if (reviveProgressNV.Value >= 0.999f)
        {
            ServerRevive(reviveHpPct);
            yield break;
        }

        // failed -> reset progress
        reviveProgressNV.Value = 0f;
    }


    private void ServerRevive(float hpPct)
    {
        if (!IsServer) return;

        // stop bleedout
        if (bleedoutRoutine != null) { StopCoroutine(bleedoutRoutine); bleedoutRoutine = null; }

        isDownedNV.Value = false;
        reviveProgressNV.Value = 0f;

        // bring back with hpPct of max and brief invuln
        int hp = Mathf.Max(1, Mathf.RoundToInt(maxHealth * Mathf.Clamp01(hpPct)));
        currentHealth.Value = Mathf.Clamp(hp, 1, maxHealth);
        SetInvulnerable(invulnerableTime);
    }

    private void ServerFakeRespawn(float hpPct)
    {
        if (!IsServer) return;

        // cancel timers
        if (bleedoutRoutine != null) { StopCoroutine(bleedoutRoutine); bleedoutRoutine = null; }
        if (reviveRoutine != null) { StopCoroutine(reviveRoutine); reviveRoutine = null; }

        isDownedNV.Value = false;
        reviveProgressNV.Value = 0f;

        int hp = Mathf.Max(1, Mathf.RoundToInt(maxHealth * Mathf.Clamp01(hpPct)));
        currentHealth.Value = Mathf.Clamp(hp, 1, maxHealth);
        SetInvulnerable(invulnerableTime);
    }
}
