using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Unity.Netcode;
using UnityEngine;

[DisallowMultipleComponent]
public class SnakeBossController : NetworkBehaviour
{
    // ========================== References ==========================
    [Header("Refs")]
    [SerializeField] private Transform head;               // Networked head (has NetworkTransform)
    [SerializeField] private ChainSnakeSolver chainSolver; // Body solver (local-only visuals)
    [SerializeField] private BossHealth bossHealth;        // For phase gating
    [SerializeField] private SnakeUtilityAi ai;            // Server-only AI brain

    // ========================== Opening Cutscene Stub ==========================
    [Header("Opening Cutscene (stub)")]
    [Tooltip("Currently unused; wire-up later.")]
    [SerializeField] private bool hasOpeningCutscene = false;
    [SerializeField] private float openingCutsceneDuration = 0f;

    // ========================== Waypoints / Roam ==========================
    [Header("Waypoints")]
    [SerializeField] private List<Transform> waypoints = new();
    [SerializeField] private float arriveDistance = 2.5f;

    [Header("Waypoint Timeout")]
    [SerializeField] private float targetTimeoutSeconds = 2f;
    private float currentTargetDeadline = 0f;

    private int lastWaypointIndex = -1; // avoid repeating the same one

    // ========================== Head Movement (core) ==========================
    [Header("Head Movement")]
    [SerializeField] private float moveSpeed = 12f;  // base forward speed (for actions)
    public float OriginalSpeed => moveSpeed;

    [SerializeField] private float yawTurnSpeedDeg = 70f;     // deg/sec
    [SerializeField] private float pitchTurnSpeedDeg = 55f;   // deg/sec
    [SerializeField] private float rollUprightSpeedDeg = 240f;// deg/sec

    [SerializeField, Range(-1f, 1f)] private float rollUprightDotThreshold = 0.1f;
    [SerializeField, Range(0f, 1f)] private float mildLevelingFactor = 0.35f;

    // ========================== Idle Facing & Limits ==========================
    [Header("Idle / Face / Track")]
    [SerializeField] private float idleDuration = 4.0f;            // how long idle lasts before AI can act
    [SerializeField, Range(0f, 1f)] private float idleFacingDot = 0.95f;
    [SerializeField] private float idleFacingMinTime = 0.3f;       // min time to try to face once
    [SerializeField] private float idleFaceSpeed = 3.0f;           // speed used while initially facing (kept tiny)
    [SerializeField] private float idleFaceTimeout = 3.0f;         // face safety
    [SerializeField] private float idleTrackCrawlSpeed = 0f;       // idle speed after facing (0 = rotate-only)

    [Header("Idle Facing Limits")]
    [Tooltip("Max nose-up allowed while idling.")]
    [SerializeField] private float idleMaxPitchUp = 25f;
    [Tooltip("Max nose-down allowed while idling.")]
    [SerializeField] private float idleMaxPitchDown = 15f;

    [Tooltip("Clamp yaw around the yaw when idle began (0 = disable).")]
    [SerializeField] private float idleYawClampDeg = 0f; // 0 => off
    private float idleBaselineYawDeg = 0f;               // captured on EnterIdle
    public bool IsIdling => inIdleHover;

    // ========================== Idle Wobble (in-place head drift) ==========================
    [Header("Idle Wobble (position-only)")]
    [SerializeField] private bool idleWobbleEnabled = true;
    [SerializeField] private float idleWobbleHorizAmp = 0.6f;   // small side movement
    [SerializeField] private float idleWobbleVertAmp = 0.25f;   // small up/down
    [SerializeField] private float idleWobbleChangeEvery = 1.0f;// change direction this often
    [SerializeField] private float idleWobbleLerpSpeed = 2.5f;  // how quickly we move toward new offset

    private bool idleWobbleActive = false;
    private Vector3 idleWobbleAnchor;
    private Vector3 idleWobbleCurOffset;
    private Vector3 idleWobbleTgtOffset;
    private float idleWobbleNextChange = 0f;

    // ========================== Optional body wiggle hook ==========================
    [Header("Body Wiggle (optional - via ChainSnakeSolver)")]
    [SerializeField] private bool bodyWiggleEnabled = true;
    [SerializeField] private float bodyWiggleAmplitude = 1.2f;
    [SerializeField] private float bodyWiggleFrequency = 0.8f;

    // ========================== Dynamic Min-Y Clamp ==========================
    [Header("Dynamic Min-Y Clamp")]
    [SerializeField] private bool minYClampEnabled = true;
    private bool minYClampActive = false;
    private float minYClampY = float.NegativeInfinity;

    // ========================== Slither (yaw weave) ==========================
    [Header("Slither (Yaw-only)")]
    [SerializeField] private bool slitherEnabled = true;     // used during actions; disabled during idle
    [SerializeField] private float slitherFrequency = 0.8f;
    [SerializeField] private float slitherYawAmplitudeDeg = 15f;
    private float slitherPhase = 0f;
    private float lastSlitherYawDeg = 0f;

    // ========================== Chase Mode ==========================
    [Header("Chase Mode")]
    [SerializeField] private float chaseDurationDefault = 3.0f;
    [SerializeField] private float chaseYawTurnSpeedDeg = 0f;    // <=0 uses defaults
    [SerializeField] private float chasePitchTurnSpeedDeg = 0f;

    [Header("Chase Speed Boost")]
    [SerializeField] private float chaseFacingTopSpeed = 20f;
    [SerializeField, Range(-1f, 1f)] private float chaseFacingDotThreshold = 0.95f;
    private bool suppressChaseBoost = false; // true while idling (so no speed pop)

    [Header("Chase Altitude Guard")]
    [SerializeField] private Transform chaseMinAltitudeMarker;
    [SerializeField] private float chaseMinAltitudeBuffer = 1.5f;

    // ========================== Attack Target Override ==========================
    [Header("Attack Target Override")]
    [SerializeField] private float attackArriveDistance = 2.5f;
    private bool attackOverrideActive;
    private Vector3 attackOverrideTarget;
    private float attackOverrideDeadline;
    private enum OverrideArrivalMode { Radial, VerticalY }
    private OverrideArrivalMode overrideArrivalMode = OverrideArrivalMode.Radial;
    [SerializeField] private float verticalArriveEpsilon = 0.25f;
    public float VerticalArriveEpsilon => verticalArriveEpsilon;

    // ========================== Speed control ==========================
    [Header("Speed Target")]
    [SerializeField] private float speedAccel = 18f;
    [SerializeField] private float speedDecel = 18f;
    private float speedTarget;
    private float currentForwardSpeed;

    public void SetSpeedTarget(float target) { if (IsServer) speedTarget = Mathf.Max(0f, target); }
    public void ResetSpeedTarget() { if (IsServer) speedTarget = moveSpeed; }
    public void SetSpeedImmediate(float value)
    {
        if (!IsServer) return;
        value = Mathf.Max(0f, value);
        speedTarget = value;
        currentForwardSpeed = value;
    }

    // ========================== Steering suppression ==========================
    private bool suppressSlither = false;
    private bool suppressRoll = false;

    // ========================== Runtime state ==========================
    private bool inRelocateSequence = false;  // while traveling to a waypoint
    private bool inIdleHover = false;         // idle state (we own facing + wobble)
    private float idleUntil = 0f;             // when idle ends

    private NetworkObject idleTrackTargetNoInPlace; // player we face during idle
    private bool idleTrackInPlace = false;

    // ========================== Altitude guard (global) ==========================
    [Header("Altitude Guard (Global)")]
    [SerializeField] private Transform groundMarker;
    [SerializeField] private float softGuardAbove = 1.5f;
    [SerializeField] private float hardGuardAbove = 0.1f;
    [SerializeField] private float softLevelSpeedDeg = 360f;
    [SerializeField] private float hardSnapSpeedDeg = 1080f;

    [SerializeField] private bool altitudeGuardSuppressed = false; //for soemthing else
    public void SuppressAltitudeGuard(bool on) { altitudeGuardSuppressed = on; }
    public bool IsAltitudeGuardSuppressed => altitudeGuardSuppressed;

    // ========================== Networked debug/state ==========================
    private NetworkVariable<int> phase = new(
        1, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private NetworkVariable<int> currentTargetIndex = new(
        -1, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // ========================== Phase Gate ==========================
    [Header("Phase Gate")]
    [SerializeField] private float phase2HpPct = 0.5f;
    public bool Phase2 => phase.Value >= 2;



    // ========================== Lifecycle ==========================
    public override void OnNetworkSpawn()
    {
        if (!bossHealth) bossHealth = GetComponent<BossHealth>();
        if (!chainSolver) chainSolver = GetComponent<ChainSnakeSolver>();
        if (!head) Debug.LogError("[SnakeBossController] Head is required (with NetworkTransform).");
        if (!ai) ai = GetComponent<SnakeUtilityAi>();

        if (IsServer)
        {
            int connected = NetworkManager.Singleton?.ConnectedClientsList?.Count ?? 0;
            if (connected <= 1 && bossHealth != null)
                bossHealth.ScaleMaxHp(0.5f, refill: true);

            currentTargetIndex.Value = -1;
            SetSpeedImmediate(0f);

            // Start in a simple idle
            EnterIdleNow(trackContinuously: true);

            // Activate AI after any intro cut
            float delay = 0f;
            var refs = ArenaSceneRefs.Instance;
            if (refs && refs.runIntro) delay = Mathf.Max(delay, refs.cutsceneDuration);
            if (ai) Invoke(nameof(ActivateAI_Server), delay);
            GetComponent<SnakeDeployTurretsAction>()?.ResetSlotUsage();
        }
    }

    private void ActivateAI_Server()
    {
        if (!IsServer || !ai) return;
        ai.Activate(this);
    }

    private void Update()
    {
        if (!IsServer) return;

        if (inIdleHover && Time.time >= idleUntil && idleDuration > 0f)
            ExitIdleHoverAndForceAi(); // method is reworked below to be "safe"

        // idle wobble position update (no forward drift)
        if (inIdleHover && idleWobbleActive)
            IdleWobbleStep(Time.deltaTime);

        Server_UpdatePhase();
        Server_MoveHead();
    }

    // ========================== Movement loop ==========================
    private void Server_MoveHead()
    {
        if (!head) return;

        bool chasing = IsChasing;
        Vector3 faceTargetPos = chasing ? GetChaseTargetPosition() : Vector3.zero;

        // Speed (idling keeps it 0)
        float desiredSpeed = speedTarget;

         // If there is no steering target at all this frame (no override, no chase,
         // not idling, and roam is disabled), do NOT drift forward. This prevents
         // the “surge toward player before climb pins” between action handoffs.
        if (!AttackOverrideActive && !IsChasing && !inIdleHover && currentTargetIndex.Value < 0)
        {
            desiredSpeed = 0f;
        }

        if (chasing && !suppressChaseBoost && faceTargetPos != Vector3.zero)
        {
            Vector3 toTarget = (faceTargetPos - head.position);
            if (toTarget.sqrMagnitude > 0.0001f)
            {
                float dot = Vector3.Dot(head.forward, toTarget.normalized);
                if (dot >= chaseFacingDotThreshold)
                    desiredSpeed = Mathf.Max(desiredSpeed, chaseFacingTopSpeed);
            }
        }

        float rate = (desiredSpeed > currentForwardSpeed) ? speedAccel : speedDecel;
        currentForwardSpeed = Mathf.MoveTowards(currentForwardSpeed, desiredSpeed, rate * Time.deltaTime);

        // Move forward along +Z (idling keeps speedTarget == 0, so no forward drift)
        head.position += head.forward * currentForwardSpeed * Time.deltaTime;

        // Choose steering target
        Vector3 targetPos;
        if (AttackOverrideActive) targetPos = GetAttackOverrideTarget();
        else if (IsChasing) targetPos = GetChaseTargetPosition();
        else if (inIdleHover && idleTrackInPlace && idleTrackTargetNoInPlace && idleTrackTargetNoInPlace.IsSpawned)
            targetPos = idleTrackTargetNoInPlace.transform.position;  // rotate-only tracking
        else if (inIdleHover) targetPos = Vector3.zero; // idle but not tracking
        else targetPos = GetCurrentTargetPosition();

        // Extra chase altitude guard
        if (chasing && chaseMinAltitudeMarker)
        {
            float minY = chaseMinAltitudeMarker.position.y + chaseMinAltitudeBuffer;
            if (head.position.y <= minY && targetPos.y < head.position.y) targetPos.y = head.position.y;
            targetPos.y = Mathf.Max(targetPos.y, minY);
        }

        // Steer yaw/pitch if we have a target (and clamp for idle)
        float steerYaw = 0f, steerPitch = 0f;
        if (targetPos != Vector3.zero)
        {
            float saveYaw = yawTurnSpeedDeg;
            float savePitch = pitchTurnSpeedDeg;

            if (chasing)
            {
                if (chaseYawTurnSpeedDeg > 0f) yawTurnSpeedDeg = chaseYawTurnSpeedDeg;
                if (chasePitchTurnSpeedDeg > 0f) pitchTurnSpeedDeg = chasePitchTurnSpeedDeg;
            }

            ComputeSteer(targetPos, out steerYaw, out steerPitch);

            // In idle: clamp pitch & optionally yaw (so head won't stare straight down/up or spin too far)
            if (inIdleHover)
            {
                // ---- Pitch clamp (absolute around horizon) ----
                float currPitch = Mathf.Asin(Mathf.Clamp(head.forward.y, -1f, 1f)) * Mathf.Rad2Deg; // +up/-down
                float proposedPitch = currPitch - steerPitch; // positive steerPitch is nose-down
                float clampedPitch = Mathf.Clamp(proposedPitch, -idleMaxPitchDown, idleMaxPitchUp);
                steerPitch = currPitch - clampedPitch;

                // ---- Yaw clamp (relative to baseline captured on EnterIdle) ----
                if (idleYawClampDeg > 0f)
                {
                    float currYaw = GetYawDeg(head.forward);
                    float proposedYaw = currYaw + steerYaw;
                    float deltaFromBase = Mathf.DeltaAngle(idleBaselineYawDeg, proposedYaw);
                    deltaFromBase = Mathf.Clamp(deltaFromBase, -idleYawClampDeg, idleYawClampDeg);
                    float clampedYaw = idleBaselineYawDeg + deltaFromBase;
                    steerYaw = Mathf.DeltaAngle(currYaw, clampedYaw);
                }
            }

            ApplyGlobalAltitudeGuard(ref steerPitch);

            yawTurnSpeedDeg = saveYaw;
            pitchTurnSpeedDeg = savePitch;
        }

        // Roll leveling + (optional) slither yaw (disabled during idle for a steady face)
        float rollStep = suppressRoll ? 0f : ComputeRollLevelStep();
        float slitherYawDelta = (inIdleHover || suppressSlither) ? 0f : ComputeSlitherYawDelta();

        float yawDelta = steerYaw + slitherYawDelta;
        ApplyLocalRotation(yawDelta, steerPitch, rollStep);

        // Roam maintenance when not idling/relocating/chasing/overriding
        if (targetPos != Vector3.zero && !IsChasing && !AttackOverrideActive && !inRelocateSequence && !inIdleHover)
        {
            bool arrived = (head.position - targetPos).sqrMagnitude <= arriveDistance * arriveDistance;
            bool timedOut = Time.time > currentTargetDeadline;
            if (arrived || timedOut)
            {
                PickRandomTargetServer();
                lastSlitherYawDeg = 0f;
            }
        }

        // Attack override arrival
        if (AttackOverrideActive)
        {
            bool arrived = (overrideArrivalMode == OverrideArrivalMode.VerticalY)
                ? Mathf.Abs(head.position.y - attackOverrideTarget.y) <= verticalArriveEpsilon
                : (head.position - attackOverrideTarget).sqrMagnitude <= attackArriveDistance * attackArriveDistance;

            if (arrived || Time.time >= attackOverrideDeadline)
            {
                ClearAttackOverride();
                lastSlitherYawDeg = 0f;
            }
        }
    }

    // ========================== Idle entry/exit ==========================
    public void BeginFaceThenIdleImmediate()
    {
        if (!IsServer) return;

        // Safety: stop any previous steering modes
        StopChase();
        ClearAttackOverride();

        // We want a steady rotate-only track in idle
        SetSteeringSuppression(noSlither: true, noRoll: false);

        // Choose a player to track in-place
        idleTrackTargetNoInPlace = PickRandomPlayerNO();
        idleTrackInPlace = (idleTrackTargetNoInPlace != null && idleTrackTargetNoInPlace.IsSpawned);

        // Crawl speed during idle rotation (0 = rotate in place)
        SetSpeedImmediate(Mathf.Max(0f, idleTrackCrawlSpeed));

        // Enter idle NOW (this sets speed target to 0 internally, and starts continuous tracking)
        EnterIdleNow(trackContinuously: true);
    }

    private void EnterIdleNow(bool trackContinuously)
    {
        inIdleHover = true;
        idleUntil = (idleDuration > 0f) ? Time.time + idleDuration : float.PositiveInfinity;

        // Freeze baseline yaw for yaw clamping
        idleBaselineYawDeg = GetYawDeg(head.forward);

        // Hard stop forward motion
        SetSpeedImmediate(0f);
        SetSpeedTarget(0f);
        suppressChaseBoost = true;
        ClearAttackOverride();
        StopChase();

        // Clean steering & start wobble
        SetSteeringSuppression(noSlither: true, noRoll: false);
        StartIdleWobble();
        TryEnableBodyWiggle(true);

        // Continuous rotate-only tracking
        if (trackContinuously)
        {
            // Preserve a previously chosen target if valid; otherwise pick one
            if (idleTrackTargetNoInPlace == null || !idleTrackTargetNoInPlace.IsSpawned)
                idleTrackTargetNoInPlace = PickRandomPlayerNO();

            idleTrackInPlace = (idleTrackTargetNoInPlace != null && idleTrackTargetNoInPlace.IsSpawned);
        }
        else
        {
            idleTrackInPlace = false;
            idleTrackTargetNoInPlace = null;
        }
    }

    public void ExitIdleHover()
    {
        StopIdleWobble();
        TryEnableBodyWiggle(false);

        inIdleHover = false;
        suppressChaseBoost = false;

        idleTrackInPlace = false;
        idleTrackTargetNoInPlace = null;

        SetSteeringSuppression(noSlither: false, noRoll: false);
        ResetSpeedTarget();
    }

    private void ExitIdleHoverAndForceAi()
    {
        if (!IsServer)
        {
            // Non-server should never drive AI. Stay in idle.
            idleUntil = Time.time + 1f;
            return;
        }

        // If we have an AI, only leave idle when it can actually start something now.
        if (ai && ai.TryStartNextActionImmediately(this))
        {
            // An action has been started by the AI. Now we can leave idle state.
            ExitIdleHover();
            return;
        }

        // No action is available right now. Stay in idle and try again shortly.
        // Keep rotate-only tracking and speed 0 enforced by idle state.
        if (!inIdleHover) EnterIdleNow(trackContinuously: true);

        // Extend idle a little and re-check soon.
        idleUntil = Time.time + 0.25f;

        // Optionally force the AI to recompute next frame, but do not leave idle.
        ForceAiThinkNow();
    }

    // Optional convenience if want to set per-idle durations from actions:
    public void EnterIdleFor(float seconds, bool trackContinuously = true)
    {
        idleDuration = Mathf.Max(0f, seconds);
        EnterIdleNow(trackContinuously);
    }

    // ========================== Idle wobble ==========================
    private void StartIdleWobble()
    {
        idleWobbleActive = idleWobbleEnabled;
        idleWobbleAnchor = head ? head.position : transform.position;
        idleWobbleCurOffset = Vector3.zero;
        idleWobbleTgtOffset = Vector3.zero;
        idleWobbleNextChange = Time.time + Mathf.Max(0.2f, idleWobbleChangeEvery);
    }

    private void StopIdleWobble()
    {
        idleWobbleActive = false;
        if (head) head.position = new Vector3(head.position.x, idleWobbleAnchor.y, head.position.z);
    }

    private void IdleWobbleStep(float dt)
    {
        if (!idleWobbleActive || !head) return;

        if (Time.time >= idleWobbleNextChange)
        {
            idleWobbleNextChange = Time.time + Mathf.Max(0.2f, idleWobbleChangeEvery);

            // plane perpendicular to current forward
            Vector3 fwd = head.forward;
            Vector3 a = Vector3.Cross(fwd, Vector3.up);
            if (a.sqrMagnitude < 1e-6f) a = Vector3.right;
            a.Normalize();
            Vector3 b = Vector3.Cross(fwd, a).normalized;

            float h = Random.Range(-idleWobbleHorizAmp, idleWobbleHorizAmp);
            float v = Random.Range(-idleWobbleVertAmp, idleWobbleVertAmp);
            idleWobbleTgtOffset = (a * h) + (b * v);
        }

        idleWobbleCurOffset = Vector3.MoveTowards(
            idleWobbleCurOffset, idleWobbleTgtOffset, idleWobbleLerpSpeed * dt);

        head.position = idleWobbleAnchor + idleWobbleCurOffset;
    }

    // Optional hook into ChainSnakeSolver (safe if not present)
    private void TryEnableBodyWiggle(bool on)
    {
        if (!chainSolver || !bodyWiggleEnabled) return;

        var t = chainSolver.GetType();

        // Preferred: SetIdleWiggle(bool, float amp, float freq)
        var m = t.GetMethod("SetIdleWiggle", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (m != null)
        {
            m.Invoke(chainSolver, new object[] { on, bodyWiggleAmplitude, bodyWiggleFrequency });
            return;
        }

        // Fallback: EnableIdleWiggle(bool, float amp, float freq)
        m = t.GetMethod("EnableIdleWiggle", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (m != null)
        {
            m.Invoke(chainSolver, new object[] { on, bodyWiggleAmplitude, bodyWiggleFrequency });
        }
        // If nothing matches, no error—head wobble still provides some motion.
    }

    // ========================== Relocate + Idle Sequence (simple) ==========================
    public IEnumerator CoRelocateToWaypointAndIdle()
    {
        inRelocateSequence = true;
        ExitIdleHover(); // just in case

        Transform choice = PickRandomWaypointAvoidingLast();
        if (!choice) { inRelocateSequence = false; yield break; }

        int idx = IndexOfWaypoint(choice);
        if (idx >= 0) { lastWaypointIndex = idx; currentTargetIndex.Value = idx; }

        Vector3 pos = choice.position;
        float speed = Mathf.Max(2f, OriginalSpeed);
        float budget = TravelTimeoutTo(pos, speed, 0.8f);

        SetSteeringSuppression(noSlither: false, noRoll: false);
        SetSpeedTarget(speed);
        SetAttackOverride(pos, budget, verticalY: false);
        yield return WaitAttackOverrideArriveRadial(arriveDistance, budget);

        // Face a player (with limits) then enter idle
        yield return CoFaceThenEnterIdle();

        inRelocateSequence = false;
    }

    private IEnumerator CoFaceThenEnterIdle()
    {
        var target = PickRandomPlayerNO();
        if (!target || !target.IsSpawned)
        {
            EnterIdleNow(trackContinuously: true);
            yield break;
        }

        // small nudge while facing
        SetSpeedImmediate(Mathf.Max(0f, idleFaceSpeed));
        SetSteeringSuppression(noSlither: true, noRoll: false);

        float savedPitch = pitchTurnSpeedDeg;
        // keep pitch reasonable during the "face" too
        pitchTurnSpeedDeg = Mathf.Min(pitchTurnSpeedDeg, Mathf.Max(1f, 2f * idleMaxPitchUp));

        float elapsed = 0f;
        float minTime = Mathf.Max(0f, idleFacingMinTime);
        float dotGate = Mathf.Clamp01(idleFacingDot);

        BeginChase(target, Mathf.Max(idleFaceTimeout, minTime) + 0.5f);
        while (elapsed < idleFaceTimeout)
        {
            if (!target || !target.IsSpawned) break;
            Vector3 to = target.transform.position - head.position;
            if (to.sqrMagnitude > 0.0001f)
            {
                to.Normalize();
                float dot = Vector3.Dot(head.forward, to);
                if (elapsed >= minTime && dot >= dotGate) break;
            }
            elapsed += Time.deltaTime;
            yield return null;
        }

        StopChase();
        pitchTurnSpeedDeg = savedPitch;

        // enter idle proper
        SetSpeedImmediate(Mathf.Max(0f, idleTrackCrawlSpeed));
        suppressChaseBoost = true;

        idleTrackTargetNoInPlace = target;
        idleTrackInPlace = true;

        EnterIdleNow(trackContinuously: true);
    }

    // ========================== Helpers (AI, travel, pickers) ==========================
    public void SetSteeringSuppression(bool noSlither, bool noRoll)
    {
        if (!IsServer) return;
        suppressSlither = noSlither;
        suppressRoll = noRoll;
    }
    /*private void ForceAiThinkNow()
    {
        if (!ai) return;
        var f = typeof(SnakeUtilityAi).GetField("thinkTimer", BindingFlags.Instance | BindingFlags.NonPublic);
        if (f != null) f.SetValue(ai, 0f);
    }*/

    private void ForceAiThinkNow()
    {
        if (!ai) return;
        ai.ForceThinkNow();   // call the AI's public method (we’ll implement it below)
    }
    private NetworkObject PickRandomPlayerNO()
    {
        var nm = NetworkManager.Singleton;
        if (!nm) return null;

        var list = nm.ConnectedClientsList;
        if (list == null || list.Count == 0) return null;

        List<NetworkObject> valid = new List<NetworkObject>(list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            var po = list[i].PlayerObject;
            if (po && po.IsSpawned) valid.Add(po);
        }

        if (valid.Count == 0) return null;
        return valid[Random.Range(0, valid.Count)];
    }

    private Transform PickRandomWaypointAvoidingLast()
    {
        var list = GetWaypointsSafe();
        if (list == null || list.Count == 0) return null;

        int next = Random.Range(0, list.Count);
        int guard = 0;
        while (list.Count > 1 && next == lastWaypointIndex && guard++ < 10)
            next = Random.Range(0, list.Count);

        if (list.Count > 1 && next == lastWaypointIndex)
            next = (next + 1) % list.Count;

        lastWaypointIndex = next;
        return list[next];
    }

    private int IndexOfWaypoint(Transform t)
    {
        if (!t || waypoints == null) return -1;
        for (int i = 0; i < waypoints.Count; i++)
            if (waypoints[i] == t) return i;
        return -1;
    }

    private List<Transform> GetWaypointsSafe() => waypoints ?? new List<Transform>();

    private float TravelTimeoutTo(Vector3 pos, float speed, float pad)
    {
        float dist = Vector3.Distance(head ? head.position : transform.position, pos);
        return (dist / Mathf.Max(0.01f, speed)) + pad;
    }

    private IEnumerator WaitAttackOverrideArriveRadial(float arriveRadius, float budget)
    {
        Vector3 target = GetAttackOverrideTarget();
        float deadline = Time.time + budget + 0.25f;

        while (AttackOverrideActive && Time.time < deadline)
        {
            if ((Head.position - target).sqrMagnitude <= arriveRadius * arriveRadius)
            {
                ClearAttackOverride();
                break;
            }
            yield return null;
        }
        if (AttackOverrideActive) ClearAttackOverride();
    }

    // ========================== Chase API ==========================
    private NetworkObject chaseTargetNo;
    private float chaseUntil;
    public bool IsChasing => IsServer && chaseTargetNo && chaseTargetNo.IsSpawned && Time.time < chaseUntil;

    public bool BeginChase(NetworkObject target, float duration)
    {
        if (!IsServer || target == null || !target.IsSpawned) return false;
        chaseTargetNo = target;
        chaseUntil = Time.time + Mathf.Max(0.1f, duration);
        return true;
    }
    public void StopChase()
    {
        if (!IsServer) return;
        chaseTargetNo = null;
        chaseUntil = 0f;
    }
    private Vector3 GetChaseTargetPosition()
    {
        return (IsChasing && chaseTargetNo && chaseTargetNo.IsSpawned)
            ? chaseTargetNo.transform.position
            : Vector3.zero;
    }

    // ========================== Attack Override API ==========================
    public bool AttackOverrideActive => attackOverrideActive && Time.time < attackOverrideDeadline;
    public Vector3 GetAttackOverrideTarget() => attackOverrideTarget;

    public void SetAttackOverride(Vector3 worldPos, float timeoutSeconds) =>
        SetAttackOverride(worldPos, timeoutSeconds, verticalY: false);

    public void SetAttackOverride(Vector3 worldPos, float timeoutSeconds, bool verticalY)
    {
        if (!IsServer) return;
        attackOverrideActive = true;
        attackOverrideTarget = worldPos;
        attackOverrideDeadline = Time.time + Mathf.Max(0.1f, timeoutSeconds);
        overrideArrivalMode = verticalY ? OverrideArrivalMode.VerticalY : OverrideArrivalMode.Radial;
    }
    public void ClearAttackOverride()
    {
        if (!IsServer) return;
        attackOverrideActive = false;
    }

    // ========================== Roaming target ==========================
    private Vector3 GetCurrentTargetPosition()
    {
        if (waypoints == null || waypoints.Count == 0) return Vector3.zero;
        if (currentTargetIndex.Value < 0) return Vector3.zero;
        int idx = Mathf.Clamp(currentTargetIndex.Value, 0, waypoints.Count - 1);
        Transform t = waypoints[idx];
        return t ? t.position : Vector3.zero;
    }

    private void PickRandomTargetServer()
    {
        if (!IsServer) return;

        if (waypoints == null || waypoints.Count == 0)
        {
            currentTargetIndex.Value = -1;
            currentTargetDeadline = 0f;
            return;
        }

        int curr = currentTargetIndex.Value;
        int last = lastWaypointIndex;

        int next = Random.Range(0, waypoints.Count);
        int guard = 0;
        while (waypoints.Count > 1 && (next == curr || next == last) && guard++ < 10)
            next = Random.Range(0, waypoints.Count);

        if (waypoints.Count > 1 && (next == curr || next == last))
            next = (next + 1) % waypoints.Count;

        currentTargetIndex.Value = next;
        lastWaypointIndex = next;
        currentTargetDeadline = Time.time + Mathf.Max(0.1f, targetTimeoutSeconds);
    }

    // ========================== Altitude Guard ==========================
    private void ApplyGlobalAltitudeGuard(ref float pitchStepDeg)
    {
        // Skip guard while suppressed or while idling (prevents idle nose-up drift)
        if (altitudeGuardSuppressed || inIdleHover) return;

        float enforcedBaseY = float.NegativeInfinity;
        if (groundMarker) enforcedBaseY = Mathf.Max(enforcedBaseY, groundMarker.position.y);
        if (minYClampActive) enforcedBaseY = Mathf.Max(enforcedBaseY, minYClampY);
        if (float.IsNegativeInfinity(enforcedBaseY)) return;

        float softY = enforcedBaseY + Mathf.Max(0f, softGuardAbove);
        float hardY = enforcedBaseY + Mathf.Max(0f, hardGuardAbove);
        float y = head.position.y;

        if (y <= hardY)
        {
            float minUp = -hardSnapSpeedDeg * Time.deltaTime; // negative = nose-up
            if (pitchStepDeg > minUp) pitchStepDeg = minUp;
            return;
        }

        if (y <= softY)
        {
            if (pitchStepDeg > 0f) pitchStepDeg = 0f;
            if (head.forward.y < 0f)
            {
                float up = -softLevelSpeedDeg * Time.deltaTime; // negative = nose-up
                if (pitchStepDeg > up) pitchStepDeg = up;
            }
        }
    }



    // ========================== Steering math ==========================
    private void ComputeSteer(Vector3 worldTarget, out float yawStepDeg, out float pitchStepDeg)
    {
        yawStepDeg = 0f; pitchStepDeg = 0f;

        Vector3 toWS = worldTarget - head.position;
        if (toWS.sqrMagnitude < 0.0001f) return;

        Vector3 toLocal = head.InverseTransformDirection(toWS.normalized);

        float yawErrDeg = Mathf.Rad2Deg * Mathf.Atan2(toLocal.x, Mathf.Max(0.0001f, toLocal.z));
        yawStepDeg = Mathf.Clamp(yawErrDeg, -yawTurnSpeedDeg * Time.deltaTime, yawTurnSpeedDeg * Time.deltaTime);

        float pitchErrDeg = -Mathf.Rad2Deg * Mathf.Atan2(toLocal.y, Mathf.Max(0.0001f, toLocal.z));
        pitchStepDeg = Mathf.Clamp(pitchErrDeg, -pitchTurnSpeedDeg * Time.deltaTime, pitchTurnSpeedDeg * Time.deltaTime);
    }

    private float ComputeRollLevelStep()
    {
        float desiredRoll = Vector3.SignedAngle(head.up, Vector3.up, head.forward);
        float dot = Vector3.Dot(head.up, Vector3.up);
        float speed = rollUprightSpeedDeg;
        if (dot > rollUprightDotThreshold) speed *= mildLevelingFactor;
        return Mathf.Clamp(desiredRoll, -speed * Time.deltaTime, speed * Time.deltaTime);
    }

    private float ComputeSlitherYawDelta()
    {
        if (!slitherEnabled || slitherFrequency <= 0f) return 0f;
        slitherPhase += Mathf.PI * 2f * slitherFrequency * Time.deltaTime;
        float s = Mathf.Sin(slitherPhase);
        float desiredYaw = slitherYawAmplitudeDeg * s;
        float dYaw = desiredYaw - lastSlitherYawDeg;
        lastSlitherYawDeg = desiredYaw;
        return dYaw;
    }

    private void ApplyLocalRotation(float yawDeg, float pitchDeg, float rollDeg)
    {
        Quaternion dq =
            Quaternion.AngleAxis(yawDeg, head.up) *
            Quaternion.AngleAxis(pitchDeg, head.right) *
            Quaternion.AngleAxis(rollDeg, head.forward);

        head.rotation = dq * head.rotation;
    }

    // Helpers
    public Transform Head => head;
    public float GroundMinY => chaseMinAltitudeMarker ? chaseMinAltitudeMarker.position.y : float.NegativeInfinity;

    public void DisableRoamTarget()
    {
        if (!IsServer) return;
        currentTargetIndex.Value = -1;
        currentTargetDeadline = 0f;
    }

    public void BeginFaceThenIdle()
    {
        if (!IsServer) return;
        StopChase(); // safety
        StartCoroutine(CoFaceThenEnterIdle());
    }

    public void RaiseMinYClamp(float y)
    {
        if (!IsServer || !minYClampEnabled) return;
        if (!minYClampActive || y > minYClampY)
        {
            minYClampActive = true;
            minYClampY = y;
        }
    }
    public void ClearMinYClamp()
    {
        if (!IsServer) return;
        minYClampActive = false;
        minYClampY = float.NegativeInfinity;
    }

    private static float GetYawDeg(Vector3 fwd)
    {
        Vector3 h = fwd; h.y = 0f;
        if (h.sqrMagnitude < 1e-6f) return 0f;
        h.Normalize();
        // 0° == +Z; use atan2(x,z) so positive to the right
        return Mathf.Atan2(h.x, h.z) * Mathf.Rad2Deg;
    }

    // ========================== Phase / Context ==========================
    private void Server_UpdatePhase()
    {
        if (!bossHealth) return;
        float pct = bossHealth.MaxHP > 0f ? bossHealth.CurrentHP / bossHealth.MaxHP : 1f;
        int p = (pct <= phase2HpPct) ? 2 : 1;
        if (phase.Value != p) phase.Value = p;
    }

    public BossContext BuildContext()
    {
        var nm = NetworkManager.Singleton;
        var players = new List<GameObject>(nm?.ConnectedClients.Count ?? 0);
        if (nm != null)
        {
            foreach (var kv in nm.ConnectedClients)
            {
                var po = kv.Value.PlayerObject;
                if (po && po.IsSpawned) players.Add(po.gameObject);
            }
        }

        GameObject primary = null;
        float bestSqr = float.PositiveInfinity;
        Vector3 headPos = head ? head.position : transform.position;

        for (int i = 0; i < players.Count; i++)
        {
            float sq = (players[i].transform.position - headPos).sqrMagnitude;
            if (sq < bestSqr) { bestSqr = sq; primary = players[i]; }
        }

        GameObject secondary = null;
        if (players.Count > 1)
            secondary = (players[0] == primary) ? players[1] : players[0];

        float distBetween = 0f;
        if (players.Count > 1)
            distBetween = Vector3.Distance(players[0].transform.position, players[1].transform.position);

        float hpPct = bossHealth && bossHealth.MaxHP > 0f ? bossHealth.CurrentHP / bossHealth.MaxHP : 1f;

        return new BossContext
        {
            Boss = this,
            Players = players,
            BossPos = headPos,
            BossHpPct = hpPct,
            Phase2 = Phase2,
            Primary = primary,
            Secondary = secondary,
            DistToPrimary = (primary ? Vector3.Distance(primary.transform.position, headPos) : 999f),
            DistBetweenPlayers = distBetween,
            TimeNow = Time.time
        };
    }
}
