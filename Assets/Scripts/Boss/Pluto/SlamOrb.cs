using Unity.Netcode;
using UnityEngine;
using System.Collections;
using System.Collections.Generic;

public enum SlamOrbState { Idle, Active, Dead, Respawning }

[RequireComponent(typeof(NetworkObject))]
[DisallowMultipleComponent]
public class SlamOrb : NetworkBehaviour, IDamageable
{
    [Header("Boss Link")]
    [SerializeField] private PlutoBoss boss;               // orbit center (assign in inspector)
    public PlutoBoss Boss => boss;

    [Header("Model / Colliders")]
    [SerializeField] private Transform model;              // visual root (spinning)
    [SerializeField] private Renderer[] renderers;         // visuals to tint (auto-filled)
    [SerializeField] private Collider bodyCollider;        // solid body (non-trigger)
    [SerializeField] private Collider bottomTrigger;       // child trigger enabled only while slamming
    [SerializeField] private Rigidbody rb;                 // kinematic rigidbody (required for reliable triggers)

    [Header("Colors")]
    [ColorUsage(false, true)] public Color idleColor = Color.gray;
    [ColorUsage(false, true)] public Color chargingColor = new Color(1.0f, 0.85f, 0.2f); // yellow
    [ColorUsage(false, true)] public Color slamColor = new Color(1.0f, 1.0f, 0.0f);      // bright yellow
    [ColorUsage(false, true)] public Color damageFlashColor = new Color(1.0f, 0.2f, 0.2f);

    [SerializeField] private float colorRampUp = 0.35f;    // towards target color
    [SerializeField] private float colorRampDown = 0.35f;  // back towards base

    [Header("Spin")]
    [SerializeField] private float idleSpinSpeed = 20f;    // deg/s
    [SerializeField] private float activeSpinSpeed = 160f; // deg/s
    [SerializeField] private float spinRampUp = 0.5f;
    [SerializeField] private float spinRampDown = 0.4f;

    [Header("Orbit (Idle)")]
    [SerializeField] private float orbitRadius = 16f;      // different from gravity orb
    [SerializeField] private float orbitHeight = 12f;
    [SerializeField] private float orbitSpeedDegPerSec = 28f;
    [SerializeField] private float orbitFollowLerp = 10f;
    [SerializeField] private float orbitAngleDeg = 0f;

    [Header("Health")]
    [SerializeField] private float maxHP = 220f;
    [SerializeField] private float respawnDelay = 5f;
    private float hp;

    [Header("Attack Timing/Speeds")]
    [SerializeField] private float hoverAbovePlayerHeight = 10f; // how high to hover over target
    [SerializeField] private float moveSpeed = 22f;
    [SerializeField] private float hoverChargeTime = 2.0f;       // wait before slam
    [SerializeField] private float preHopUp = 3.0f;              // small up before slam
    [SerializeField] private float preHopSpeed = 18f;
    [SerializeField] private float slamSpeed = 48f;              // fast down
    [SerializeField] private float riseSpeed = 14f;              // after slam, rise up

    [Header("Grounding")]
    [SerializeField] private LayerMask groundMask = ~0;          // what counts as ground
    [SerializeField] private float groundRayLen = 100f;
    [SerializeField] private float groundClearance = 0.2f;       // stop this far above ground

    [Header("Damage / Knockback")]
    [SerializeField] private float contactDamage = 25f;          // touching bottom trigger during descent
    [SerializeField] private float contactKnockback = 30f;
    [SerializeField] private float contactUpBias = 0.15f;

    [Header("Wave")]
    [SerializeField] private float waveMaxRadius = 10f;
    [SerializeField] private float waveDuration = 0.25f;         // expands quickly
    [SerializeField] private float waveDamage = 20f;
    [SerializeField] private float waveKnockback = 22f;
    [SerializeField] private float waveUpBias = 0.1f;
    [SerializeField] private GameObject vfxGameobject;

    [Header("Networking / State")]
    [SerializeField] private bool activeUntilKilled = true;      // keep looping slams until dead
    private NetworkVariable<SlamOrbState> netState =
        new NetworkVariable<SlamOrbState>(
            SlamOrbState.Idle,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

    [SerializeField] private int playerLayerIndex = -1;
    private int playerLayer => playerLayerIndex >= 0 ? playerLayerIndex : (playerLayerIndex = LayerMask.NameToLayer("Player"));

    public event System.Action EffectCompleted; //invoke on death

    // AI gate
    public bool IsBusy { get; private set; } // used by action

    // runtime visuals
    private float currentSpin;
    private float targetSpin;
    private Color currentColor;
    private Color targetColor;
    private bool flashActive = false;
    private float flashUntil = 0f;

    // state/control
    [SerializeField] private SlamOrbState state = SlamOrbState.Idle;
    private Coroutine activeRoutine;
    private ulong lastTargetClient = ulong.MaxValue; // alternate targets

    // ===== Unity

    public override void OnNetworkSpawn()
    {
        // Do NOT disable on clients; they need to run visuals
        if (!boss)
        {
            boss = FindFirstObjectByType<PlutoBoss>();
            if (!boss) Debug.LogWarning("[SlamOrb] PlutoBoss not assigned; set it in the Inspector.");
        }

        if (renderers == null || renderers.Length == 0)
            renderers = GetComponentsInChildren<Renderer>(true);
        if (!model) model = transform;

        // Ensure body collider solid, bottom trigger is trigger-only
        if (bodyCollider)
        {
            bodyCollider.isTrigger = false;
        }
        if (bottomTrigger)
        {
            bottomTrigger.isTrigger = true;
            bottomTrigger.enabled = false; // only during descent
            // Make sure the child has a callback component:
            var t = bottomTrigger.GetComponent<SlamBottomTrigger>();
            if (!t) { t = bottomTrigger.gameObject.AddComponent<SlamBottomTrigger>(); t.Bind(this); }
            else t.Bind(this);
        }

        // Ensure kinematic rigidbody present for reliable triggers on moving object
        rb = GetComponent<Rigidbody>() ?? gameObject.AddComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.useGravity = false;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.Continuous;

        // Subscribe to net state
        netState.OnValueChanged += OnNetStateChanged;

        if (IsServer)
        {
            hp = maxHP;
            orbitAngleDeg = Random.Range(0f, 360f);
            ServerSetState(SlamOrbState.Idle, immediate: true);
        }
        else
        {
            ApplyPresentationForState(netState.Value, immediate: true);
        }
    }

    public override void OnNetworkDespawn()
    {
        netState.OnValueChanged -= OnNetStateChanged;
    }

    private void Update()
    {
        // spin + color on everyone
        float spinLerp = (targetSpin > currentSpin) ? spinRampUp : spinRampDown;
        currentSpin = Mathf.MoveTowards(currentSpin, targetSpin, spinLerp * 360f * Time.deltaTime);
        if (model) model.Rotate(0f, currentSpin * Time.deltaTime, 0f, Space.World);

        if (renderers != null && renderers.Length > 0)
        {
            if (flashActive && Time.time < flashUntil)
            {
                float t = 1f - Mathf.Exp(-16f * Time.deltaTime);
                currentColor = Color.Lerp(currentColor, damageFlashColor, t);
            }
            else
            {
                flashActive = false;
                float ramp = (targetColor == chargingColor || targetColor == slamColor) ? colorRampUp : colorRampDown;
                currentColor = Color.Lerp(currentColor, targetColor, Time.deltaTime / Mathf.Max(0.0001f, ramp));
            }

            for (int i = 0; i < renderers.Length; i++)
                if (renderers[i]) renderers[i].material.color = currentColor;
        }

        if (!IsServer) return;

        switch (state)
        {
            case SlamOrbState.Idle:
                ServerOrbitMotion();
                break;
            case SlamOrbState.Active:
                break;
        }
    }

    private void ServerOrbitMotion()
    {
        if (!boss || !boss.core) return;

        orbitAngleDeg = Mathf.Repeat(orbitAngleDeg + orbitSpeedDegPerSec * Time.deltaTime, 360f);
        Vector3 ringDir = Quaternion.Euler(0f, orbitAngleDeg, 0f) * Vector3.forward;
        Vector3 targetPos = boss.core.position + ringDir * orbitRadius + Vector3.up * orbitHeight;
        transform.position = Vector3.Lerp(transform.position, targetPos, Time.deltaTime * orbitFollowLerp);

        targetSpin = idleSpinSpeed;
        targetColor = idleColor;
        SetRenderersEnabled(true);
    }

    // ===== State sync

    private void ServerSetState(SlamOrbState s, bool immediate = false)
    {
        if (!IsServer) return;

        state = s;
        netState.Value = s;

        // visuals
        ApplyPresentationForState(s, immediate);

        // server-only flags
        switch (s)
        {
            case SlamOrbState.Idle:
                IsBusy = false;
                break;
            case SlamOrbState.Active:
                IsBusy = true;
                break;
            case SlamOrbState.Dead:
            case SlamOrbState.Respawning:
                IsBusy = true;
                break;
        }
    }

    private void OnNetStateChanged(SlamOrbState oldS, SlamOrbState newS)
    {
        ApplyPresentationForState(newS, immediate: false);
    }

    private void ApplyPresentationForState(SlamOrbState s, bool immediate)
    {
        switch (s)
        {
            case SlamOrbState.Idle:
                targetSpin = idleSpinSpeed;
                targetColor = idleColor;
                if (immediate) { currentSpin = idleSpinSpeed; currentColor = idleColor; SetRendererColor(idleColor); }
                SetRenderersEnabled(true);
                SetColliderEnabled(true);
                if (bottomTrigger) bottomTrigger.enabled = false;
                break;

            case SlamOrbState.Active:
                targetSpin = activeSpinSpeed;
                targetColor = chargingColor; // base color during active; we’ll pulse to slamColor during descent
                if (immediate) { currentSpin = activeSpinSpeed; currentColor = chargingColor; SetRendererColor(chargingColor); }
                SetRenderersEnabled(true);
                SetColliderEnabled(true);
                break;

            case SlamOrbState.Dead:
                SetRenderersEnabled(false);
                SetColliderEnabled(false);
                if (bottomTrigger) bottomTrigger.enabled = false;
                break;

            case SlamOrbState.Respawning:
                SetRenderersEnabled(false);
                SetColliderEnabled(false);
                if (bottomTrigger) bottomTrigger.enabled = false;
                break;
        }
    }

    private void SetRenderersEnabled(bool en)
    {
        if (renderers != null)
            foreach (var r in renderers) if (r) r.enabled = en;
    }

    private void SetRendererColor(Color c)
    {
        currentColor = c;
        if (renderers != null)
            foreach (var r in renderers) if (r) r.material.color = c;
    }

    private void SetColliderEnabled(bool en)
    {
        if (bodyCollider) bodyCollider.enabled = en;
        // bottomTrigger is managed separately during descent
    }

    // ===== Public control (called by action)

    /// Start/continue the slam loop until killed
    public void ActivateSlam()
    {
        if (!IsServer) return;
        if (state == SlamOrbState.Dead || state == SlamOrbState.Respawning) return;
        if (IsBusy) return;

        if (activeRoutine != null) StopCoroutine(activeRoutine);
        activeRoutine = StartCoroutine(ActiveLoop());
    }

    private IEnumerator ActiveLoop()
    {
        ServerSetState(SlamOrbState.Active);

        while (state == SlamOrbState.Active)
        {
            //here
            activeRoutine = null;

            // if we exited the loop without dying and aren't looping forever, end the session cleanly
            if (!activeUntilKilled && state == SlamOrbState.Active)
            {
                ServerSetState(SlamOrbState.Idle, immediate: true);
                EffectCompleted?.Invoke();
            }
            // pick target client (alternate between players if possible)
            ulong targetClient = PickTargetClient();
            GameObject targetGO = GetPlayerObject(targetClient);
            if (!targetGO)
            {
                // fallback: pick any
                var any = NetworkManager.Singleton.LocalClientId; // not great; just break
                yield return new WaitForSeconds(0.25f);
                continue;
            }

            // Move above target and charge
            yield return MoveAboveTarget(targetGO.transform.position, hoverAbovePlayerHeight, moveSpeed);
            // charge color
            targetColor = chargingColor;
            yield return new WaitForSeconds(hoverChargeTime);

            // pre-hop up
            yield return MoveToPosition(transform.position + Vector3.up * preHopUp, preHopSpeed);

            // slam down
            if (!RaycastGround(out Vector3 groundPos))
            {
                // if no ground, just slam down by a fixed distance
                groundPos = transform.position + Vector3.down * 20f;
            }

            // enable bottom trigger just for descent window
            if (bottomTrigger) bottomTrigger.enabled = true;

            // color bright during slam
            targetColor = slamColor;
            yield return MoveToGround(groundPos + Vector3.up * groundClearance, slamSpeed);

            // impact: do wave damage/knockback
            if (state == SlamOrbState.Active)
                yield return DoImpactWave(groundPos, waveMaxRadius, waveDuration);

            // disable bottom trigger after impact
            if (bottomTrigger) bottomTrigger.enabled = false;

            if (!activeUntilKilled || state != SlamOrbState.Active)
                break;

            // pick the other player for next cycle
            lastTargetClient = targetClient;
            ulong nextClient = PickOtherClient(targetClient);

            // rise up, moving towards next player’s top position
            var nextObj = GetPlayerObject(nextClient);
            Vector3 riseTarget = transform.position;
            if (nextObj)
                riseTarget = nextObj.transform.position + Vector3.up * hoverAbovePlayerHeight;

            yield return MoveToPosition(riseTarget, riseSpeed);
            // loop; will hover-charge again
        }

        activeRoutine = null;
    }

    private bool RaycastGround(out Vector3 groundPos)
    {
        groundPos = Vector3.zero;
        Ray ray = new Ray(transform.position + Vector3.up * 2f, Vector3.down);
        if (Physics.Raycast(ray, out var hit, groundRayLen, groundMask, QueryTriggerInteraction.Ignore))
        {
            groundPos = hit.point;
            return true;
        }
        return false;
    }

    private IEnumerator MoveAboveTarget(Vector3 playerPos, float height, float speed)
    {
        Vector3 target = playerPos + Vector3.up * Mathf.Abs(height);
        while ((transform.position - target).sqrMagnitude > 0.16f && state == SlamOrbState.Active)
        {
            Vector3 dir = (target - transform.position).normalized;
            transform.position += dir * speed * Time.deltaTime;
            yield return null;
        }
    }

    private IEnumerator MoveToPosition(Vector3 target, float speed)
    {
        while ((transform.position - target).sqrMagnitude > 0.16f && state == SlamOrbState.Active)
        {
            Vector3 dir = (target - transform.position).normalized;
            transform.position += dir * speed * Time.deltaTime;
            yield return null;
        }
    }

    private IEnumerator MoveToGround(Vector3 target, float speed)
    {
        // straight-line move down to target (already ground + clearance)
        while ((transform.position - target).sqrMagnitude > 0.04f && state == SlamOrbState.Active)
        {
            Vector3 dir = (target - transform.position).normalized;
            transform.position += dir * speed * Time.deltaTime;
            yield return null;
        }
    }

    private IEnumerator DoImpactWave(Vector3 center, float maxRadius, float duration)
    {
        float t0 = Time.time;
        float tEnd = t0 + Mathf.Max(0.01f, duration);

        // Optional: tell clients to play VFX
        PlayImpactVFXClientRpc(center);

        // Expand radius and apply damage/knockback inside ring
        float lastRadius = 0f;
        while (Time.time < tEnd && state == SlamOrbState.Active)
        {
            float alpha = Mathf.InverseLerp(t0, tEnd, Time.time);
            float radius = Mathf.Lerp(0f, maxRadius, alpha);

            // sphere shell between lastRadius..radius (cheap: just use radius)
            var hits = Physics.OverlapSphere(center, radius, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits.Length; i++)
            {
                var col = hits[i];
                if (col.gameObject.layer != playerLayer) continue;

                var hp = col.GetComponentInParent<PlayerHealth>();
                if (hp == null) continue;

                // Damage once per impact window; to avoid multiple hits, you can track a HashSet<ulong> per wave
                Vector3 hitPoint = col.ClosestPoint(center);
                hp.ApplyDamageFromHitPoint(Mathf.RoundToInt(waveDamage), hitPoint, waveKnockback, waveUpBias);
            }

            lastRadius = radius;
            yield return null;
        }
    }

    [ClientRpc]
    private void PlayImpactVFXClientRpc(Vector3 pos)
    {
        // TODO: spawn your wave VFX at pos on clients
        Instantiate(vfxGameobject, pos, Quaternion.identity);
    }

    // ===== Damage & flash

    public bool IsAlive => state != SlamOrbState.Dead && state != SlamOrbState.Respawning;

    public void TakeDamage(float amount)
    {
        if (!IsServer) return;
        if (state != SlamOrbState.Active) return;

        hp = Mathf.Max(0f, hp - Mathf.Abs(amount));

        if (hp > 0f)
        {
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
        flashActive = true;
        flashUntil = Time.time + 0.1f;
    }

    
    private void Kill()
    {
        if (activeRoutine != null)
        {
            StopCoroutine(activeRoutine);
            activeRoutine = null;
        }
        // turn off descent trigger if any
        if (bottomTrigger) bottomTrigger.enabled = false;

        ServerSetState(SlamOrbState.Dead);

        // notify action(s) that the active session finished
        EffectCompleted?.Invoke();

        StartCoroutine(RespawnAfter(respawnDelay));
    }

    private IEnumerator RespawnAfter(float seconds)
    {
        ServerSetState(SlamOrbState.Respawning);
        yield return new WaitForSeconds(seconds);

        hp = maxHP;

        // reposition to a fresh orbit angle
        orbitAngleDeg = Random.Range(0f, 360f);
        if (boss && boss.core)
        {
            Vector3 ringDir = Quaternion.Euler(0f, orbitAngleDeg, 0f) * Vector3.forward;
            transform.position = boss.core.position + ringDir * orbitRadius + Vector3.up * orbitHeight;
        }

        ServerSetState(SlamOrbState.Idle, immediate: true);
    }

    // ===== Target helpers

    private ulong PickTargetClient()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null) return NetworkManager.ServerClientId;

        // Two-player game: alternate; otherwise random
        var keys = new List<ulong>(nm.ConnectedClients.Keys);
        if (keys.Count == 0) return NetworkManager.ServerClientId;
        if (keys.Count == 1) return keys[0];

        // alternate between two players
        if (lastTargetClient == ulong.MaxValue) return keys[Random.Range(0, keys.Count)];
        return PickOtherClient(lastTargetClient);
    }

    private ulong PickOtherClient(ulong current)
    {
        var nm = NetworkManager.Singleton;
        if (nm == null) return current;

        foreach (var kv in nm.ConnectedClients)
        {
            if (kv.Key != current) return kv.Key;
        }
        return current;
    }

    private GameObject GetPlayerObject(ulong clientId)
    {
        var nm = NetworkManager.Singleton;
        if (nm == null) return null;
        nm.ConnectedClients.TryGetValue(clientId, out var conn);
        return conn != null ? conn.PlayerObject?.gameObject : null;
    }

    // ===== Bottom trigger callback

    // Called by SlamBottomTrigger when a player touches the bottom during descent
    public void OnBottomHit(Collider playerCol)
    {
        if (!IsServer) return;
        if (state != SlamOrbState.Active) return;

        // apply contact damage/knockback
        var hp = playerCol.GetComponentInParent<PlayerHealth>();
        if (hp == null) return;

        Vector3 hitPoint = playerCol.ClosestPoint(transform.position);
        hp.ApplyDamageFromHitPoint(Mathf.RoundToInt(contactDamage), hitPoint, contactKnockback, contactUpBias);
    }
}

// Small helper component placed on the bottomTrigger child to route trigger events back to the SlamOrb
public class SlamBottomTrigger : MonoBehaviour
{
    private SlamOrb owner;
    private static int PlayerLayer = -1;

    public void Bind(SlamOrb orb)
    {
        owner = orb;
        if (PlayerLayer < 0) PlayerLayer = LayerMask.NameToLayer("Player");
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!owner || !owner.IsServer) return;
        if (other.gameObject.layer != PlayerLayer) return;
        owner.OnBottomHit(other);
    }
}

