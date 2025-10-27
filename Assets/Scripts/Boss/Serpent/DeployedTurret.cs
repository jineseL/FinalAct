using Unity.Netcode;
using UnityEngine;
using System.Collections;

/// Always-upright turret that travels on a smooth arc, spins around its local Y
/// while flying (slowing to zero near landing), then becomes an active turret.
public class DeployedTurret : NetworkBehaviour
{
    public enum State { Traveling, Turret, JumpPad, Despawn }

    // NEW: networked state so clients can drive VFX consistently
    private NetworkVariable<State> stateNV = new NetworkVariable<State>(
        State.Traveling,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    [Header("Looping VFX")]
    [SerializeField] private GameObject turretLoopVfxRoot;  // assign in prefab (default disabled)
    [SerializeField] private GameObject jumpPadLoopVfxRoot; // assign in prefab (default disabled)
    [SerializeField] private float vfxFadeTime = 0.25f;     // seconds to ramp emission
                                                            // Cache each PS's intended ON rate from the prefab so we can fade to it later.
    private readonly System.Collections.Generic.Dictionary<ParticleSystem, float> _psOnRate = new();

    // If a PS was authored with 0, use this fallback when enabling (tweak in inspector if you want)
    [SerializeField] private float vfxDefaultOnRate = 15f;

    // ---- Tunables (set via InitializeServer) ----
    private Vector3 _start;
    private Vector3 _end;
    private float _arcHeight;
    private float _startSpeed;
    private float _endSpeed;
    private float _spinYMax;
    private float _arriveEps;

    // ---- Runtime ----
    private State _state = State.Traveling;
    private float _t;                       // 0..1 along path
    private float _approxLength;            // path length estimate
    private float _yaw;                     // accumulated spin yaw in degrees
    private Quaternion _uprightBasis;       // upright rotation basis (up=world up)

    [Header("Optional Anim/VFX")]
    [SerializeField] private Animator animator;
    //[SerializeField] private string animSetupTrigger = "Setup";

    [SerializeField] private string setupStateName = "TurretSettingUp";  // name or full path of the state
    [SerializeField] private int setupLayer = 0;               // animator layer
    [SerializeField] private float setupCrossfade = 0.12f;     // 0 = hard Play
    [SerializeField] private float setupNormalizedTime = 0f;   // start at beginning

    // If you prefer to spin only the visuals, assign this; otherwise the root spins.
    [SerializeField] private Transform visualRoot;

    // ---- Targeting / Firing ----
    [Header("Turret Targeting & Fire")]
    [SerializeField] private Transform head;           // the rotating gun head (will be aimed)
    [SerializeField] private Transform muzzle;         // projectile spawn point (child of head)
    [SerializeField] private NetworkObject projectilePrefab;

    [SerializeField] private float setupAnimDuration = 0.9f; // seconds to wait before disabling Animator
    [SerializeField] private bool disableAnimatorAfterSetup = true;

    [SerializeField] private float aimLerpSpeed = 6f;        // how fast head turns toward target
    [SerializeField] private float retargetEvery = 0.5f;     // seconds between nearest-player checks

    [SerializeField] private float firstShotCharge = 0.8f;   // delay before the very first shot
    [SerializeField] private float fireInterval = 1.25f;     // time between shots
    [SerializeField] private int projectileDamage = 15;
    [SerializeField] private float projectileSpeed = 32f;
    [SerializeField] private float projectileLifetime = 6f;

    // ================= JumpPad =================
    [Header("JumpPad")]
    [SerializeField] private Collider jumpPadTrigger;     // disabled until active (must be IsTrigger)
    [SerializeField] private Renderer padRenderer;         // the plate that changes color
    [SerializeField] private Color padColorFrom = Color.red;
    [SerializeField] private Color padColorTo = Color.green;
    [SerializeField] private float padDelayBeforeLerp = 0.35f;
    [SerializeField] private float padColorLerpTime = 0.6f;

    [SerializeField] private float bounceUpVelocity = 18f;
    [SerializeField] private float reHitCooldown = 0.25f;  // prevent multi-bounce spam
    private readonly System.Collections.Generic.Dictionary<ulong, float> _lastBounce = new();

    private Coroutine _turretLoopCo;
    private Coroutine _turretVfxCo, _padVfxCo;
    public struct InitParams
    {
        public Vector3 startPos;
        public Vector3 targetPos;
        public float arcHeight;
        public float startSpeed;
        public float endSpeed;
        public float spinYDegPerSec;
        public float arriveEps;
    }

    /// Call on the server before Spawn().
    public void InitializeServer(InitParams p)
    {
        _start = p.startPos;
        _end = p.targetPos;
        _arcHeight = Mathf.Max(0f, p.arcHeight);
        _startSpeed = Mathf.Max(0.01f, p.startSpeed);
        _endSpeed = Mathf.Max(0.01f, p.endSpeed);
        _spinYMax = p.spinYDegPerSec;
        _arriveEps = Mathf.Max(0.01f, p.arriveEps);

        // Always start upright: up = world up. Choose a yaw based on target on XZ (optional).
        Vector3 flatDir = Vector3.ProjectOnPlane((_end - _start), Vector3.up);
        if (flatDir.sqrMagnitude < 1e-6f) flatDir = Vector3.forward;
        _uprightBasis = Quaternion.LookRotation(flatDir.normalized, Vector3.up);

        _yaw = 0f;
        _t = 0f;
        transform.position = _start;
        ApplyUprightRotation(); // set initial upright rot

        _approxLength = ApproximateArcLength(_start, _end, _arcHeight);
        _state = State.Traveling;
    }
    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        // ensure both roots are OFF on clients at spawn
        SafeDisableVfx(turretLoopVfxRoot);
        SafeDisableVfx(jumpPadLoopVfxRoot);

        stateNV.OnValueChanged += OnStateChanged_Client;
        OnStateChanged_Client(stateNV.Value, stateNV.Value); // apply current state
    }


    public override void OnNetworkDespawn()
    {
        stateNV.OnValueChanged -= OnStateChanged_Client;
        base.OnNetworkDespawn();
    }
    private void SafeDisableVfx(GameObject root)
    {
        if (!root) return;
        var ps = root.GetComponentsInChildren<ParticleSystem>(true);
        for (int i = 0; i < ps.Length; i++)
        {
            ps[i].Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var em = ps[i].emission;
            em.rateOverTimeMultiplier = 0f;
        }
        var au = root.GetComponentsInChildren<AudioSource>(true);
        for (int i = 0; i < au.Length; i++) au[i].Stop();
        root.SetActive(false);
    }

    private void Update()
    {
        if (!IsServer) return;

        if (_state == State.Traveling)
            ServerTravelStep(Time.deltaTime);
        // else handle Turret / JumpPad as you flesh them out
    }
    private void OnTriggerEnter(Collider other)
    {
        if (!IsServer) return;
        if (_state != State.JumpPad) return;
        /*if (!jumpPadTrigger || other != jumpPadTrigger && !other.transform.IsChildOf(transform))
        {
            // If your trigger is a child, the event may come from that child; that's fine.
            // We'll just look for PlayerHealth on "other".
        }*/

        var ph = other.GetComponentInParent<PlayerHealth>();
        if (!ph || !ph.IsAlive) return;

        // Cooldown per player to avoid rapid multiple triggers
        var no = ph.GetComponent<NetworkObject>();
        if (!no) return;
        ulong id = no.OwnerClientId;

        float now = Time.time;
        if (_lastBounce.TryGetValue(id, out var last) && now - last < reHitCooldown)
            return;

        _lastBounce[id] = now;

        // Upward "velocity" impulse — your PlayerMotor treats this as external velocity
        Vector3 upVel = Vector3.up * Mathf.Max(0f, bounceUpVelocity);
        ph.ApplyKnockbackOnly(upVel); // this sends a targeted ClientRpc that calls PlayerMotor.ApplyExternalForce on the owner
    }

    private void ServerTravelStep(float dt)
    {
        if (_approxLength <= 0.001f) _approxLength = 1f;

        // Progress speed along the path: start fast to end slow
        float speedNow = Mathf.Lerp(_startSpeed, _endSpeed, _t);
        float delta = (speedNow / _approxLength) * Mathf.Max(0f, dt);
        _t = Mathf.Clamp01(_t + delta);

        // Position along quadratic bezier arc
        Vector3 p = EvaluateArc(_start, _end, _arcHeight, _t);
        transform.position = p;

        // Spin around local Y while staying upright.
        // Spin slows as we approach landing using a smooth ease.
        float spinEase = Mathf.SmoothStep(0f, 1f, _t);             // 0 to 1
        float spinNow = Mathf.Lerp(_spinYMax, 0f, spinEase);      // deg/sec
        _yaw += spinNow * dt;
        ApplyUprightRotation();

        // Arrival check
        if ((p - _end).sqrMagnitude <= _arriveEps * _arriveEps || _t >= 0.999f)
        {
            // Snap to final position and stop spinning completely.
            transform.position = _end;
            _yaw = 0f;             // hold final yaw (set to 0 to stop at basis); remove if you want to keep last angle.
            ApplyUprightRotation();

            EnterTurretState();
        }
    }

    private void ApplyUprightRotation()
    {
        // Build final rotation: upright basis * yaw around world up (local Y)
        Quaternion yawQ = Quaternion.AngleAxis(_yaw, Vector3.up);
        Quaternion final = yawQ * _uprightBasis;

        if (visualRoot)
        {
            // Keep the root strictly upright & non-spinning; only visuals yaw.
            transform.rotation = _uprightBasis;
            visualRoot.localRotation = Quaternion.AngleAxis(_yaw, Vector3.up);
        }
        else
        {
            // Spin the whole object while staying upright
            transform.rotation = final;
        }
    }

    private void EnterTurretState()
    {
        SetStateServer(State.Turret);

        // lock upright, stop spin, snap on land
        _yaw = 0f;
        ApplyUprightRotation();
        if (visualRoot) visualRoot.localRotation = Quaternion.identity;

        // Play setup animation on all clients
        PlaySetupAnimClientRpc(setupStateName, setupLayer, setupCrossfade, setupNormalizedTime);

        // Begin server-side post-setup routine (aim + fire)
        _turretLoopCo = StartCoroutine(ServerTurretAfterSetup());
    }

    [ClientRpc]
    private void PlaySetupAnimClientRpc(string stateName, int layer, float crossfade, float normalizedTime)
    {
        if (!animator || string.IsNullOrEmpty(stateName)) return;

        if (crossfade > 0f)
            animator.CrossFadeInFixedTime(stateName, crossfade, layer, normalizedTime);
        else
            animator.Play(stateName, layer, normalizedTime);
    }
    private IEnumerator ServerTurretAfterSetup()
    {
        // wait for the setup animation to finish
        float t = 0f;
        while (t < setupAnimDuration)
        {
            if (!IsServer) yield break;
            t += Time.deltaTime;
            yield return null;
        }

        // Disable Animator so it won't fight head rotation
        if (disableAnimatorAfterSetup && animator) animator.enabled = false;

        // target + fire loop
        float retargetTimer = 0f;
        float fireTimer = -firstShotCharge; // start with a charge-up before first shot

        Transform target = null;

        while (IsServer && _state == State.Turret)
        {
            // 1) Retarget every so often (nearest alive player)
            retargetTimer -= Time.deltaTime;
            if (retargetTimer <= 0f)
            {
                target = FindNearestPlayer();
                retargetTimer = Mathf.Max(0.05f, retargetEvery);
            }

            // 2) Aim head toward target (lerp/slerp)
            if (head && target)
            {
                Vector3 to = (target.position - head.position);
                if (to.sqrMagnitude > 0.0001f)
                {
                    Quaternion want = Quaternion.LookRotation(to.normalized, Vector3.up);
                    head.rotation = Quaternion.Slerp(head.rotation, want, 1f - Mathf.Exp(-aimLerpSpeed * Time.deltaTime));
                }
            }

            // 3) Fire on cadence
            fireTimer += Time.deltaTime;
            if (fireTimer >= fireInterval)
            {
                fireTimer = 0f;
                ServerFireProjectile();
            }

            yield return null;
        }
    }
    private void SetStateServer(State s)
    {
        if (!IsServer) return;
        _state = s;
        if (stateNV.Value != s) stateNV.Value = s; // notifies clients
    }

    private void ServerFireProjectile()
    {
        if (!projectilePrefab || !muzzle)
            return;

        // forward shot from the head/muzzle
        Vector3 fwd = (head ? head.forward : transform.forward);
        if (fwd.sqrMagnitude < 0.0001f) fwd = transform.forward;

        Quaternion rot = Quaternion.LookRotation(fwd, Vector3.up);

        NetworkObject no = Instantiate(projectilePrefab, muzzle.position, rot);
        var proj = no.GetComponent<TurretProjectile>();
        if (proj)
        {
            proj.InitializeServer(new TurretProjectile.Init
            {
                speed = projectileSpeed,
                damage = projectileDamage,
                lifetime = projectileLifetime,
            });
        }
        no.Spawn(true);
    }
    private IEnumerator ServerEnterJumpPad()
    {
        SetStateServer(State.JumpPad);

        // Ensure animator won't fight visuals anymore
        if (animator) animator.enabled = false;

        // Safety: make sure the jump pad trigger starts disabled (we'll enable after color lerp)
        if (jumpPadTrigger) jumpPadTrigger.enabled = false;

        // Optional small delay before the color lerp
        float t = 0f;
        while (t < padDelayBeforeLerp) { t += Time.deltaTime; yield return null; }

        // Drive the color lerp on clients
        StartPadColorLerpClientRpc(padColorFrom, padColorTo, padColorLerpTime);

        // Wait for lerp to finish (server-side timer)
        t = 0f;
        while (t < padColorLerpTime) { t += Time.deltaTime; yield return null; }

        // Enable jump-pad trigger
        if (jumpPadTrigger) jumpPadTrigger.enabled = true;
    }

    [ClientRpc]
    private void StartPadColorLerpClientRpc(Color from, Color to, float duration)
    {
        if (!padRenderer) return;
        StopCoroutine(nameof(PadColorLerpCo));
        StartCoroutine(PadColorLerpCo(from, to, duration));
    }

    private System.Collections.IEnumerator PadColorLerpCo(Color from, Color to, float duration)
    {
        // Use MPB for per-renderer color without duplicating materials
        var mpb = new MaterialPropertyBlock();
        int baseColorId = Shader.PropertyToID("_BaseColor");
        int colorId = Shader.PropertyToID("_Color");

        float t = 0f;
        while (t < duration)
        {
            float u = (duration <= 0f) ? 1f : Mathf.Clamp01(t / duration);
            Color c = Color.Lerp(from, to, Mathf.SmoothStep(0f, 1f, u));

            padRenderer.GetPropertyBlock(mpb);
            mpb.SetColor(baseColorId, c);
            mpb.SetColor(colorId, c);
            padRenderer.SetPropertyBlock(mpb);

            t += Time.deltaTime;
            yield return null;
        }

        // ensure final color
        padRenderer.GetPropertyBlock(mpb);
        mpb.SetColor(baseColorId, to);
        mpb.SetColor(colorId, to);
        padRenderer.SetPropertyBlock(mpb);
    }


    public void ServerOnHeadDestroyed()
    {
        if (!IsServer) return;

        // Stop turret loop if running
        if (_turretLoopCo != null) { StopCoroutine(_turretLoopCo); _turretLoopCo = null; }

        // Head is dead: switch to JumpPad
        if (_state != State.JumpPad)
            StartCoroutine(ServerEnterJumpPad());
    }

    private void OnStateChanged_Client(State oldState, State newState)
    {
        if (_turretVfxCo != null) { StopCoroutine(_turretVfxCo); _turretVfxCo = null; }
        if (_padVfxCo != null) { StopCoroutine(_padVfxCo); _padVfxCo = null; }

        switch (newState)
        {
            case State.Traveling:
                // both OFF
                SafeDisableVfx(turretLoopVfxRoot);
                SafeDisableVfx(jumpPadLoopVfxRoot);
                break;

            case State.Turret:
                // pad OFF, turret fade-IN
                SafeDisableVfx(jumpPadLoopVfxRoot);
                if (turretLoopVfxRoot)
                    _turretVfxCo = StartCoroutine(FadeVfxEmission(turretLoopVfxRoot, enable: true, vfxFadeTime));
                break;

            case State.JumpPad:
                // turret OFF, pad fade-IN
                SafeDisableVfx(turretLoopVfxRoot);
                if (jumpPadLoopVfxRoot)
                    _padVfxCo = StartCoroutine(FadeVfxEmission(jumpPadLoopVfxRoot, enable: true, vfxFadeTime));
                break;

            case State.Despawn:
                SafeDisableVfx(turretLoopVfxRoot);
                SafeDisableVfx(jumpPadLoopVfxRoot);
                break;
        }
    }

    private IEnumerator DeactivateAfter(GameObject root, float delay)
    {
        if (delay > 0f) yield return new WaitForSeconds(delay);
        // make sure it’s not needed again immediately due to state flip-flop
        if (root == null) yield break;
        // If you prefer to keep the GameObject active, skip this line
        root.SetActive(false);
    }

    private IEnumerator FadeVfxEmission(GameObject root, bool enable, float time)
    {
        if (!root) yield break;

        CacheOnRates(root);

        var psList = root.GetComponentsInChildren<ParticleSystem>(true);
        var audio = root.GetComponentsInChildren<AudioSource>(true);

        if (enable)
            root.SetActive(true);    // only activate when turning ON

        // build fade plan
        var modules = new (ParticleSystem.EmissionModule em, float start, float target)[psList.Length];
        for (int i = 0; i < psList.Length; i++)
        {
            var em = psList[i].emission;
            float start = em.rateOverTimeMultiplier;
            float target = enable ? _psOnRate[psList[i]] : 0f;
            modules[i] = (em, start, target);
            if (enable) psList[i].Play(true);
        }

        foreach (var a in audio)
        {
            if (!a) continue;
            if (enable) { if (!a.isPlaying) a.Play(); }
            else { a.Stop(); }
        }

        // if disabling an already-inactive root, just exit
        if (!enable && !root.activeSelf)
            yield break;

        if (time <= 0f)
        {
            for (int i = 0; i < modules.Length; i++)
                modules[i].em.rateOverTimeMultiplier = modules[i].target;

            if (!enable)
            {
                for (int i = 0; i < psList.Length; i++)
                    psList[i].Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                root.SetActive(false); // fully off after fade-out
            }
            yield break;
        }

        float t = 0f;
        while (t < time)
        {
            float u = Mathf.SmoothStep(0f, 1f, t / time);
            for (int i = 0; i < modules.Length; i++)
                modules[i].em.rateOverTimeMultiplier = Mathf.Lerp(modules[i].start, modules[i].target, u);
            t += Time.deltaTime;
            yield return null;
        }

        // final
        for (int i = 0; i < modules.Length; i++)
            modules[i].em.rateOverTimeMultiplier = modules[i].target;

        if (!enable)
        {
            for (int i = 0; i < psList.Length; i++)
                psList[i].Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            root.SetActive(false);  // OFF after fade-out completes
        }
    }

    private void CacheOnRates(GameObject root)
    {
        if (!root) return;
        var psList = root.GetComponentsInChildren<ParticleSystem>(true);
        for (int i = 0; i < psList.Length; i++)
        {
            var ps = psList[i];
            if (_psOnRate.ContainsKey(ps)) continue;
            var em = ps.emission;
            float authored = em.rateOverTimeMultiplier;
            _psOnRate[ps] = (authored > 0f) ? authored : vfxDefaultOnRate;
        }
    }

    // ===== helpers =====
    private Transform FindNearestPlayer()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null) return null;
        var list = nm.ConnectedClientsList;
        if (list == null || list.Count == 0) return null;

        Transform best = null;
        float bestSqr = float.PositiveInfinity;
        Vector3 pos = transform.position;

        for (int i = 0; i < list.Count; i++)
        {
            var po = list[i].PlayerObject;
            if (!po || !po.IsSpawned) continue;

            float sq = (po.transform.position - pos).sqrMagnitude;
            if (sq < bestSqr) { bestSqr = sq; best = po.transform; }
        }
        return best;
    }

    private static Vector3 EvaluateArc(Vector3 a, Vector3 b, float upHeight, float t)
    {
        // Quadratic bezier with apex above midpoint
        Vector3 mid = (a + b) * 0.5f + Vector3.up * upHeight;
        Vector3 p1 = Vector3.Lerp(a, mid, t);
        Vector3 p2 = Vector3.Lerp(mid, b, t);
        return Vector3.Lerp(p1, p2, t);
    }

    private static float ApproximateArcLength(Vector3 a, Vector3 b, float h)
    {
        Vector3 prev = a;
        float len = 0f;
        const int segs = 8;
        for (int i = 1; i <= segs; i++)
        {
            float t = i / (float)segs;
            Vector3 p = EvaluateArc(a, b, h, t);
            len += Vector3.Distance(prev, p);
            prev = p;
        }
        return Mathf.Max(0.01f, len);
    }

    // Public API: later you can flip to JumpPad, Despawn, etc.
    public void BecomeJumpPad()
    {
        if (!IsServer) return;
        if (_state != State.Turret) return;
        _state = State.JumpPad;
        // TODO: play VFX, enable jump trigger, etc.
    }
}

