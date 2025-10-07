using Unity.Netcode;
using UnityEngine;
using System.Collections;
using System.Collections.Generic;

public enum GravityOrbState { Idle, Active, Dead, Respawning }


[RequireComponent(typeof(NetworkObject))]
public class GravityOrb : NetworkBehaviour, IDamageable

{
    private NetworkVariable<GravityOrbState> netState =
    new NetworkVariable<GravityOrbState>(
        GravityOrbState.Idle,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);
    [Header("Boss Link")]
    [SerializeField] private PlutoBoss boss;            // orbit center
    public PlutoBoss Boss => boss;

    [Header("Visuals")]
    [SerializeField] private Transform model;           // spinning visual
    [SerializeField] private Renderer rend;            // planet renderer to tint
    [SerializeField] private float idleSpinSpeed = 25f;    // deg/sec
    [SerializeField] private float activeSpinSpeed = 360f; // deg/sec
    [SerializeField] private float spinRampUp = 0.5f;
    [SerializeField] private float spinRampDown = 0.4f;

    [Header("Orbit")]
    [SerializeField] private float orbitRadius = 12f;
    [SerializeField] private float orbitHeight = 8f;
    [SerializeField] private float orbitSpeedDegPerSec = 20f;
    [SerializeField] private float orbitFollowLerp = 10f;
    [SerializeField] private float orbitAngleDeg = 0f;

    [Header("Health")]
    [SerializeField] private float maxHP = 200f;
    [SerializeField] private float respawnDelay = 5f;
    private float hp;

    [Header("Blackhole Effect")]
    [SerializeField] private float travelSpeed = 12f;       // to move to anchor/activated position
    [SerializeField] private float holdDuration = 3.5f;     // time pulling players, not in use currently
    [SerializeField] private float effectRadius = 18f;      // radius where pull applies
    /*[SerializeField] private float minPull = 6f;            // at edge of radius
    [SerializeField] private float maxPull = 28f;           // near center*/
    [SerializeField] private float slowFactor = 0.65f;      // movement slow multiplier
    [SerializeField] private float slowRefresh = 0.25f;     // reapply slow this often
    [SerializeField] private float touchDamage = 25f;       // damage on contact while active
    [SerializeField] private bool activeUntilKilled = true;

    [Header("Collision")]
    [SerializeField] private Collider bodyCollider;         // set as trigger
    private static int PlayerLayer = -1;

    [Header("Touch Knockback")]
    [SerializeField] private float touchKnockback = 30f;   // how hard to launch on contact for players
    [SerializeField] private float touchUpBias = 0.15f;  // small upward bias for nicer arcs

    [Header("State")]
    [SerializeField] private GravityOrbState state = GravityOrbState.Idle;
    public bool IsBusy { get; private set; } // for AI availability
    public event System.Action EffectCompleted;

    [SerializeField] private Renderer[] renderers;
    [SerializeField] private Collider[] colliders;

    [Header("Colors")]
    [ColorUsage(false, true)] public Color idleColor = Color.gray;
    [ColorUsage(false, true)] public Color activeColor = new Color(0.25f, 0.0f, 0.35f); // purple-black
    [ColorUsage(false, true)] public Color damageFlashColor = new Color(1.0f, 0.2f, 0.2f); // bright red
    [SerializeField] private float colorRampUp = 0.4f;
    [SerializeField] private float colorRampDown = 0.35f;

    [Header("Damage Flash")]
    [SerializeField] private float damageFlashDuration = 0.10f; // seconds
    [SerializeField] private float damageFlashEnterSpeed = 16f; // how fast we reach red

    [Header("Pull Tuning (works with your existing PlayerMotor)")]
    [SerializeField] private float pullMinVelocity = 2f;   // desired persistent speed at edge
    [SerializeField] private float pullMaxVelocity = 8f;   // desired persistent speed near center
    [SerializeField] private float pullCurvePower = 2f;    // >1 flattens near center (less spike), 1 = linear
    [SerializeField] private float distanceCapFactor = 2f; // clamp by dist: maxVel <= dist * factor

    [Header("Slow RPC Gate")]
    [SerializeField] private float slowRpcInterval = 0.25f; // send slow refresh at most 4x/sec
    private readonly Dictionary<ulong, float> _nextSlowSend = new();

    // runtime
    private float currentSpin;
    private float targetSpin;
    private Color currentColor;
    private Color targetColor;
    private Coroutine activeRoutine;
    private bool flashActive = false;
    private float flashUntil = 0f;

    public override void OnNetworkSpawn()
    {
        //if (!IsServer) { enabled = false; return; }

        if (!boss)
        {
            boss = FindFirstObjectByType<PlutoBoss>();
            if (!boss) Debug.LogWarning("[GravityOrb] PlutoBoss not assigned; set it in the Inspector.");
        }

        if (renderers == null || renderers.Length == 0)
            renderers = GetComponentsInChildren<Renderer>(true);
        if (colliders == null || colliders.Length == 0)
            colliders = GetComponentsInChildren<Collider>(true);

        if (!model) model = transform;
        if (!rend) rend = GetComponentInChildren<Renderer>();
        if (!bodyCollider) bodyCollider = GetComponentInChildren<Collider>();

        if (PlayerLayer == -1) PlayerLayer = LayerMask.NameToLayer("Player");
        //so that client can see
        netState.OnValueChanged += OnNetStateChanged;
        if (IsServer)
        {
            hp = maxHP;
            orbitAngleDeg = Random.Range(0f, 360f);
            ServerSetState(GravityOrbState.Idle, immediate: true); // sets netState too
        }
        else
        {
            // Apply current state on late-join or spawn
            ApplyPresentationForState(netState.Value, immediate: true);
        }
        hp = maxHP;
        SetAlive(true);
        ServerSetState(GravityOrbState.Idle, immediate: true);
        orbitAngleDeg = Random.Range(0f, 360f);
    }

    public override void OnNetworkDespawn()
    {
        netState.OnValueChanged -= OnNetStateChanged;
        ClearPullClientRpc();
    }
    private void Update()
    {
        float spinLerp = (targetSpin > currentSpin) ? spinRampUp : spinRampDown;
        currentSpin = Mathf.MoveTowards(currentSpin, targetSpin, spinLerp * 360f * Time.deltaTime);
        if (model) model.Rotate(0f, currentSpin * Time.deltaTime, 0f, Space.World);

        if (renderers != null && renderers.Length > 0)
        {
            if (flashActive && Time.time < flashUntil)
            {
                // Rush toward flash color
                float t = 1f - Mathf.Exp(-damageFlashEnterSpeed * Time.deltaTime);
                currentColor = Color.Lerp(currentColor, damageFlashColor, t);
            }
            else
            {
                // Normal ramp back to state color
                flashActive = false;
                float ramp = (targetColor == activeColor) ? colorRampUp : colorRampDown;
                currentColor = Color.Lerp(currentColor, targetColor, Time.deltaTime / Mathf.Max(0.0001f, ramp));
            }

            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r) r.material.color = currentColor;
            }
        }

        if (!IsServer) return; // below here is server-only logic

        switch (state)
        {
            case GravityOrbState.Idle:
                ServerOrbitMotion();
                break;
            case GravityOrbState.Active:
                break;
        }
    }

    private void ServerOrbitMotion()
    {
        if (boss == null || boss.core == null) return;

        orbitAngleDeg = Mathf.Repeat(orbitAngleDeg + orbitSpeedDegPerSec * Time.deltaTime, 360f);
        Vector3 ringDir = Quaternion.Euler(0f, orbitAngleDeg, 0f) * Vector3.forward;
        Vector3 targetPos = boss.core.position + ringDir * orbitRadius + Vector3.up * orbitHeight;
        transform.position = Vector3.Lerp(transform.position, targetPos, Time.deltaTime * orbitFollowLerp);

        targetSpin = idleSpinSpeed;
        targetColor = idleColor;
    }

    private void ServerSetState(GravityOrbState s, bool immediate = false)
    {
        if (!IsServer) return;

        var old = state;
        state = s;
        netState.Value = s;
        ApplyPresentationForState(s, immediate);

        switch (s)
        {
            case GravityOrbState.Idle: IsBusy = false; SetDamageable(false); break;
            case GravityOrbState.Active: IsBusy = true; SetDamageable(true); break;
            default: IsBusy = true; SetDamageable(false); break;
        }

        if (old == GravityOrbState.Active && s != GravityOrbState.Active)
            ClearPullClientRpc();
    }
    private void ApplyPresentationForState(GravityOrbState s, bool immediate)
    {
        switch (s)
        {
            case GravityOrbState.Idle:
                targetSpin = idleSpinSpeed;
                targetColor = idleColor;
                if (immediate) { currentSpin = idleSpinSpeed; currentColor = idleColor; SetRendererColor(idleColor); }
                SetAlive(true);
                break;

            case GravityOrbState.Active:
                targetSpin = activeSpinSpeed;
                targetColor = activeColor;
                if (immediate) { currentSpin = activeSpinSpeed; currentColor = activeColor; SetRendererColor(activeColor); }
                SetAlive(true);
                break;

            case GravityOrbState.Dead:
                // hide visuals on all clients
                SetAlive(false);
                break;

            case GravityOrbState.Respawning:
                // could be hidden during respawn too
                SetAlive(false);
                break;
        }
    }
    // Called for everyone when netState changes
    private void OnNetStateChanged(GravityOrbState oldS, GravityOrbState newS)
    {
        ApplyPresentationForState(newS, immediate: false);
    }
    /* private void SetState(GravityOrbState s, bool immediate = false)
     {
         state = s;

         switch (s)
         {
             case GravityOrbState.Idle:
                 IsBusy = false;
                 SetDamageable(false);
                 targetSpin = idleSpinSpeed;
                 targetColor = idleColor;
                 if (immediate) { currentSpin = idleSpinSpeed; currentColor = idleColor; SetRendererColor(idleColor); }
                 break;

             case GravityOrbState.Active:
                 IsBusy = true;
                 SetDamageable(true);
                 targetSpin = activeSpinSpeed;
                 targetColor = activeColor;
                 if (immediate) { currentSpin = activeSpinSpeed; currentColor = activeColor; SetRendererColor(activeColor); }
                 break;

             case GravityOrbState.Dead:
                 IsBusy = true;
                 SetDamageable(false);
                 SetAlive(false);
                 break;

             case GravityOrbState.Respawning:
                 IsBusy = true;
                 SetDamageable(false);
                 break;
         }
     }*/

    private void SetRendererColor(Color c)
    {
        currentColor = c;
        if (renderers != null)
            foreach (var r in renderers) if (r) r.material.color = c;
    }

    private void SetDamageable(bool canTakeDamage)
    {
        // If you have a hitbox component, toggle it here.
        // For prototype, we just rely on state in TakeDamageServerRpc.
    }

    private void SetAlive(bool alive)
    {
        // keep the root GameObject active so coroutines and networking keep running
        if (renderers != null)
            foreach (var r in renderers) if (r) r.enabled = alive;

        if (colliders != null)
            foreach (var c in colliders) if (c) c.enabled = alive;
    }

    // Server-only damage entry
    [ServerRpc(RequireOwnership = false)]
    public void TakeDamageServerRpc(float dmg)
    {
        if (!IsServer) return;
        if (state != GravityOrbState.Active) return; // invulnerable unless active

        hp = Mathf.Max(0f, hp - Mathf.Abs(dmg));
        if (hp <= 0f)
        {
            Kill();
        }
    }

    private void Kill()
    {
        if (activeRoutine != null)
        {
            StopCoroutine(activeRoutine);
            activeRoutine = null;
        }

        if (activeUntilKilled)
            EffectCompleted?.Invoke();

        ServerSetState(GravityOrbState.Dead);     // this now only disables renderers/colliders
        ClearPullClientRpc();
        StartCoroutine(RespawnAfter(respawnDelay)); // safe: root stays active
    }

    private IEnumerator RespawnAfter(float seconds)
    {
        yield return new WaitForSeconds(seconds);

        hp = maxHP;

        // Reappear at a random orbit angle
        orbitAngleDeg = Random.Range(0f, 360f);
        if (boss && boss.core)
        {
            Vector3 ringDir = Quaternion.Euler(0f, orbitAngleDeg, 0f) * Vector3.forward;
            transform.position = boss.core.position + ringDir * orbitRadius + Vector3.up * orbitHeight;
        }

        SetAlive(true);
        ServerSetState(GravityOrbState.Idle, immediate: true);
    }

    // Call from the action to start the attack
    public void ActivateBlackhole(Vector3 anchorPosition)
    {
        if (!IsServer) return;
        if (state == GravityOrbState.Dead || state == GravityOrbState.Respawning) return;
        if (IsBusy) return;// prevents reentry
        if (activeRoutine != null) StopCoroutine(activeRoutine);
        activeRoutine = StartCoroutine(DoBlackhole(anchorPosition));
    }

    private IEnumerator DoBlackhole(Vector3 anchorPos)
    {
        ServerSetState(GravityOrbState.Active); // sets IsBusy = true

        // fly to anchor
        while ((transform.position - anchorPos).sqrMagnitude > 0.16f && state == GravityOrbState.Active)
        {
            Vector3 dir = (anchorPos - transform.position).normalized;
            transform.position += dir * travelSpeed * Time.deltaTime;
            yield return null;
        }

        if (state != GravityOrbState.Active)
        {
            activeRoutine = null;
            yield break; // aborted mid-flight by death or respawn
        }

        if (activeUntilKilled)
        {
            // keep sucking until killed
            while (state == GravityOrbState.Active)
            {
                PullAllPlayers();
                yield return null;
            }
            // state changed away from Active, likely by Kill()
            activeRoutine = null;
            yield break;
        }
        else
        {
            // timed mode: pull for duration, then return to Idle
            float tEnd = Time.time + holdDuration;
            while (Time.time < tEnd && state == GravityOrbState.Active)
            {
                PullAllPlayers();
                yield return null;
            }

            // back to idle only in timed mode
            if (state == GravityOrbState.Active)
                ServerSetState(GravityOrbState.Idle);

            activeRoutine = null;
            ClearPullClientRpc();
            // notify the action to unlock in timed mode
            EffectCompleted?.Invoke();
        }
    }

    private void PullAllPlayers()
    {
        if (boss == null) return;

        var nm = NetworkManager.Singleton;
        if (nm == null) return;

        Vector3 center = transform.position;
        float now = Time.time;

        foreach (var kv in nm.ConnectedClients)
        {
            var po = kv.Value.PlayerObject;
            if (po == null || !po.IsSpawned) continue;

            var motor = po.GetComponent<PlayerMotor>();
            var ctrl = po.GetComponent<CharacterController>();
            if (motor == null) continue;

            // Measure distance from player center to orb
            Vector3 playerPos = ctrl ? ctrl.transform.TransformPoint(ctrl.center) : po.transform.position;
            Vector3 toCenter = center - playerPos;
            float dist = toCenter.magnitude;
            if (dist > effectRadius) continue;

            // 0 at edge, 1 near center
            float t = 1f - Mathf.Clamp01(dist / Mathf.Max(0.001f, effectRadius));

            // Shape it so it doesn't spike near the center
            float shaped = Mathf.Pow(t, Mathf.Max(1f, pullCurvePower)); // power>1 = softer near center

            // Desired persistent velocity magnitude
            float velMag = Mathf.Lerp(pullMinVelocity, pullMaxVelocity, shaped);

            // Optional extra safety: cap by distance so very close players don't overshoot
            velMag = Mathf.Min(velMag, dist * distanceCapFactor);

            Vector3 desiredVel = (dist > 0.001f) ? toCenter.normalized * velMag : Vector3.zero;

            // Ask that client to SET its persistent velocity for this frame (no stacking)
            SetPullVelocityForClient(kv.Key, desiredVel);

            // Throttle slow application to avoid spamming RPCs
            if (!_nextSlowSend.TryGetValue(kv.Key, out var next) || now >= next)
            {
                ApplySlowToVictimClient(kv.Key, slowFactor, slowRefresh * 1.1f);
                _nextSlowSend[kv.Key] = now + Mathf.Max(0.05f, slowRpcInterval);
            }
        }
    }


    // touch damage while active
    private void OnTriggerEnter(Collider other)
    {
        if (!IsServer) return;
        if (state != GravityOrbState.Active) return;
        if (other.gameObject.layer != PlayerLayer) return;

        var hp = other.GetComponentInParent<PlayerHealth>();
        if (hp == null) return;

        // Use the actual contact point for direction
        Vector3 hitPoint = other.ClosestPoint(transform.position);
        hp.ApplyDamageFromHitPoint(Mathf.RoundToInt(touchDamage), hitPoint, touchKnockback,touchUpBias);
    }
    public void TakeDamage(float amount)
    {
        if (!IsServer) return;
        if (state != GravityOrbState.Active) return;          // invulnerable unless active
        hp = Mathf.Max(0f, hp - Mathf.Abs(amount));
        if (hp > 0f)
        {
            // show bright red flash on all clients
            DamageFlashClientRpc();
        }
        if (hp <= 0f)
        {
            Kill();
        }
    }
    [ClientRpc]
    private void DamageFlashClientRpc()
    {
        // start flash window; Update() handles blending
        flashActive = true;
        flashUntil = Time.time + damageFlashDuration;
    }
    private void ApplyForceToVictimClient(ulong clientId, Vector3 force)
    {
        var p = new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } }
        };
        ApplyForceClientRpc(force, p);
    }

    [ClientRpc]
    private void ApplyForceClientRpc(Vector3 force, ClientRpcParams p = default)
    {
        var po = NetworkManager.Singleton.LocalClient?.PlayerObject;
        if (!po) return;
        var motor = po.GetComponent<PlayerMotor>();
        if (motor != null) motor.ApplyExternalForce(force);
    }

    private void ApplySlowToVictimClient(ulong clientId, float factor, float duration)
    {
        var p = new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } }
        };
        ApplySlowClientRpc(factor, duration, p);
    }

    [ClientRpc]
    private void ApplySlowClientRpc(float factor, float duration, ClientRpcParams p = default)
    {
        var po = NetworkManager.Singleton.LocalClient?.PlayerObject;
        if (!po) return;
        var motor = po.GetComponent<PlayerMotor>();
        if (motor != null) motor.ApplySlow(factor, duration);
    }
    private void SetPullVelocityForClient(ulong clientId, Vector3 desiredVel)
    {
        var p = new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } }
        };
        SetPullVelocityClientRpc(desiredVel, p);
    }

    [ClientRpc]
    private void SetPullVelocityClientRpc(Vector3 desiredVel, ClientRpcParams p = default)
    {
        var po = NetworkManager.Singleton.LocalClient?.PlayerObject;
        if (!po) return;
        var motor = po.GetComponent<PlayerMotor>();
        if (motor != null) motor.SetPersistentExternalVelocity(desiredVel);
    }
    [ClientRpc]
    private void ClearPullClientRpc(ClientRpcParams p = default)
    {
        var po = NetworkManager.Singleton.LocalClient?.PlayerObject;
        if (!po) return;
        var motor = po.GetComponent<PlayerMotor>();
        if (motor != null) motor.ClearPersistentExternalVelocity();
    }

    // Public knobs for the action to read if desired
    public bool IsIdle => state == GravityOrbState.Idle;
    public bool IsActive => state == GravityOrbState.Active;
    public bool IsAlive => state != GravityOrbState.Dead && state != GravityOrbState.Respawning;
    public float MaxHP => maxHP;
    public float CurrentHP => hp;

    // Convenience to compute a point directly below Pluto at given depth
    public Vector3 BelowPluto(float depth)
    {
        if (boss && boss.core)
            return boss.core.position + Vector3.down * Mathf.Abs(depth);
        return transform.position + Vector3.down * Mathf.Abs(depth);
    }
}

