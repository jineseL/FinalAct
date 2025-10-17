using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

[DisallowMultipleComponent]
public class SnakeBossController : NetworkBehaviour
{
    // ========================== References ==========================
    [Header("Refs")]
    [SerializeField] private Transform head;               // Head transform. Must have a NetworkTransform so clients see the same pose.
    [SerializeField] private ChainSnakeSolver chainSolver; // Local chain solver (runs on server and clients; no networking needed).
    [SerializeField] private BossHealth bossHealth;        // Boss health component for phase gating.
    [SerializeField] private SnakeUtilityAi ai;            // Separate server-only Utility AI component.

    // ========================== Waypoints ==========================
    [Header("Waypoints")]
    [SerializeField] private List<Transform> waypoints = new(); // Positions the head will travel to.
    [SerializeField] private float arriveDistance = 2.5f;        // Consider target "reached" within this radius.

    [Header("Waypoint Timeout")]
    [SerializeField] private float targetTimeoutSeconds = 2f; // If not reached in this time, pick a new target.
    private float currentTargetDeadline = 0f;                 // Server-only deadline for the current target.

    // ========================== Head Movement ==========================
    [Header("Head Movement")]
    [SerializeField] private float moveSpeed = 12f;           // Constant forward speed in local +Z.
    public float OriginalSpeed => moveSpeed;
    [SerializeField] private float yawTurnSpeedDeg = 70f;     // Max yaw degrees per second (local Y).
    [SerializeField] private float pitchTurnSpeedDeg = 55f;   // Max pitch degrees per second (local X).
    [SerializeField] private float rollUprightSpeedDeg = 240f;// Max roll correction per second (local Z).
    [SerializeField, Range(-1f, 1f)] private float rollUprightDotThreshold = 0.1f; // If head.up • worldUp > this, apply only mild leveling.
    [SerializeField, Range(0f, 1f)] private float mildLevelingFactor = 0.35f;       // Fraction of roll speed to use when mostly upright.

    // ========================== Slither (Yaw only) ==========================
    [Header("Slither (Yaw-only)")]
    [SerializeField] private bool slitherEnabled = true;          // Toggle the snake-like weave.
    [SerializeField] private float slitherFrequency = 0.8f;       // Oscillations per second.
    [SerializeField] private float slitherYawAmplitudeDeg = 15f;  // Peak yaw deviation added by slither (degrees).

    // Runtime slither state (yaw-only)
    private float slitherPhase = 0f;       // Phase accumulator for the sine wave.
    private float lastSlitherYawDeg = 0f;  // Last slither yaw angle to compute frame delta.

    [Header("Chase Mode")]
    [SerializeField] private float chaseDurationDefault = 3.0f; // seconds
    [SerializeField] private float chaseYawTurnSpeedDeg = 0f;   // Optional per-chase yaw override; <=0 uses normal
    [SerializeField] private float chasePitchTurnSpeedDeg = 0f; // Optional per-chase pitch override; <=0 uses normal

    [Header("Chase Altitude Guard")]
    [SerializeField] private Transform chaseMinAltitudeMarker; // drag a scene transform; its Y is the ground level used during chase
    [SerializeField] private float chaseMinAltitudeBuffer = 1.5f; // extra clearance above the marker while chasing

    [Header("Chase Speed Boost")]
    [SerializeField] private float chaseFacingTopSpeed = 20f;      // faster forward speed when head is facing target
    [SerializeField, Range(-1f, 1f)] private float chaseFacingDotThreshold = 0.95f; // how aligned head.forward must be

    [Header("Attack Target Override")]
    [SerializeField] private float attackArriveDistance = 2.5f;
    private bool attackOverrideActive;
    private Vector3 attackOverrideTarget;
    private float attackOverrideDeadline;

    // Speed control (base speed is moveSpeed). Actions can lower/restore.
    [Header("Speed Target")]
    [SerializeField] private float speedAccel = 18f;   // how fast forward speed rises to target
    [SerializeField] private float speedDecel = 18f;   // how fast forward speed falls to target
    private float speedTarget;                         // current forward-speed target (server)

    // Steering suppression flags (server-owned)
    private bool suppressSlither = false;
    private bool suppressRoll = false;

    // runtime
    private float currentForwardSpeed; // what we actually use this frame

    // Arrival mode for the current attack override
    private enum OverrideArrivalMode { Radial, VerticalY }
    private OverrideArrivalMode overrideArrivalMode = OverrideArrivalMode.Radial;

    [SerializeField] private float verticalArriveEpsilon = 0.25f; // how close in Y counts as arrived for VerticalY
    public float VerticalArriveEpsilon => verticalArriveEpsilon;  // optional getter

    // runtime chase state (server-only writes)
    private NetworkObject chaseTargetNo;
    private float chaseUntil;
    public bool IsChasing => IsServer && chaseTargetNo && chaseTargetNo.IsSpawned && Time.time < chaseUntil;
    public bool AttackOverrideActive => attackOverrideActive && Time.time < attackOverrideDeadline;

    public float GroundMinY => chaseMinAltitudeMarker ? chaseMinAltitudeMarker.position.y : float.NegativeInfinity;
    public Vector3 GetAttackOverrideTarget() => attackOverrideTarget;

    // Speed target for temporary slow/restore (used during charge)
    public void SetSpeedTarget(float target) { if (IsServer) speedTarget = Mathf.Max(0f, target); }
    public void ResetSpeedTarget() { if (IsServer) speedTarget = moveSpeed; }

    // ========================== Altitude Guard (Global) ==========================
    [Header("Altitude Guard (Global)")]
    [Tooltip("World-space baseline Y the head should never go below.")]
    [SerializeField] private Transform groundMarker;

    [Tooltip("Start aggressively pitching up once the head drops to this many units above the baseline.")]
    [SerializeField] private float softGuardAbove = 1.5f;

    [Tooltip("Absolute lower bound above the baseline. If the head reaches this, snap upward even faster.")]
    [SerializeField] private float hardGuardAbove = 0.1f;

    [Tooltip("How fast to pitch up when in the soft guard zone (deg/sec).")]
    [SerializeField] private float softLevelSpeedDeg = 360f;

    [Tooltip("How fast to pitch up when at/under the hard guard zone (deg/sec).")]
    [SerializeField] private float hardSnapSpeedDeg = 1080f;

    // ========================== Networked debug/state ==========================
    private NetworkVariable<int> phase = new(
        1, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server); // 1 or 2 for phase display/HUD.

    private NetworkVariable<int> currentTargetIndex = new(
        -1, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server); // Index of current waypoint (for debugging).

    // ========================== Phase Gate ==========================
    [Header("Phase Gate")]
    [SerializeField] private float phase2HpPct = 0.5f; // Switch to phase 2 when health <= 50%.

    public override void OnNetworkSpawn()
    {
        if (!bossHealth) bossHealth = GetComponent<BossHealth>();
        if (!chainSolver) chainSolver = GetComponent<ChainSnakeSolver>();
        if (!head) Debug.LogError("[SnakeBossController] Head is required (with NetworkTransform).");
        if (!ai) ai = GetComponent<SnakeUtilityAi>();

        if (IsServer)
        {
            // If only one player is connected, halve the boss max HP and refill to that max
            int connected = NetworkManager.Singleton?.ConnectedClientsList?.Count ?? 0;
            if (connected <= 1 && bossHealth != null)
            {
                bossHealth.ScaleMaxHp(0.5f, refill: true);
            }

            PickRandomTargetServer(); // Also sets the timeout deadline.

            float delay = 0f;
            speedTarget = moveSpeed;            // base target
            currentForwardSpeed = moveSpeed;    // actual current speed
            var refs = ArenaSceneRefs.Instance;
            if (refs && refs.runIntro) delay = Mathf.Max(delay, refs.cutsceneDuration);
            if (ai) Invoke(nameof(ActivateAI_Server), delay);
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

        Server_UpdatePhase();
        Server_MoveHead();
    }

    // ========================== Movement loop ==========================

    private void Server_MoveHead()
    {
        if (!head) return;
        bool chasing = IsChasing;
        Vector3 faceTargetPos = chasing ? GetChaseTargetPosition() : Vector3.zero;

        // Speed logic (boost when nicely lined up in a chase)
        float desiredSpeed = speedTarget;
        if (chasing && faceTargetPos != Vector3.zero)
        {
            Vector3 toTarget = (faceTargetPos - head.position);
            if (toTarget.sqrMagnitude > 0.0001f)
            {
                float dot = Vector3.Dot(head.forward, toTarget.normalized);
                if (dot >= chaseFacingDotThreshold)
                    desiredSpeed = Mathf.Max(desiredSpeed, chaseFacingTopSpeed);
            }
        }

        // Smooth toward desiredSpeed
        float rate = (desiredSpeed > currentForwardSpeed) ? speedAccel : speedDecel;
        currentForwardSpeed = Mathf.MoveTowards(currentForwardSpeed, desiredSpeed, rate * Time.deltaTime);

        // Move forward in local +Z
        head.position += head.forward * currentForwardSpeed * Time.deltaTime;

        // Compute all rotation deltas first
        float steerYaw = 0f, steerPitch = 0f;
        float rollStep = 0f;
        float slitherYawDelta = 0f;

        // prefer chase target if active, else waypoint, but override if an action says so
        Vector3 targetPos = AttackOverrideActive
            ? GetAttackOverrideTarget()
            : (chasing ? GetChaseTargetPosition() : GetCurrentTargetPosition());

        // Extra chase altitude guard (your existing per-chase guard)
        if (chasing && chaseMinAltitudeMarker)
        {
            float minY = chaseMinAltitudeMarker.position.y + chaseMinAltitudeBuffer;

            if (head.position.y <= minY && targetPos.y < head.position.y)
                targetPos.y = head.position.y;

            targetPos.y = Mathf.Max(targetPos.y, minY);
        }

        // Steer toward the current target using yaw+pitch
        if (targetPos != Vector3.zero)
        {
            // Temporarily boost turn speeds while chasing
            float saveYaw = yawTurnSpeedDeg;
            float savePitch = pitchTurnSpeedDeg;
            if (chasing)
            {
                if (chaseYawTurnSpeedDeg > 0f) yawTurnSpeedDeg = chaseYawTurnSpeedDeg;
                if (chasePitchTurnSpeedDeg > 0f) pitchTurnSpeedDeg = chasePitchTurnSpeedDeg;
            }

            // Aim toward target
            ComputeSteer(targetPos, out steerYaw, out steerPitch);

            // >>> NEW: global altitude guard (never go below baseline) <<<
            ApplyGlobalAltitudeGuard(ref steerPitch);

            // Restore turn speeds
            yawTurnSpeedDeg = saveYaw;
            pitchTurnSpeedDeg = savePitch;

            // ----- Arrival / timeout handling (three cases) -----
            if (AttackOverrideActive)
            {
                bool arrived;
                if (overrideArrivalMode == OverrideArrivalMode.VerticalY)
                    arrived = Mathf.Abs(head.position.y - targetPos.y) <= verticalArriveEpsilon;
                else
                    arrived = (head.position - targetPos).sqrMagnitude <= attackArriveDistance * attackArriveDistance;

                bool timedOut = Time.time >= attackOverrideDeadline;
                if (arrived || timedOut)
                {
                    ClearAttackOverride();
                    lastSlitherYawDeg = 0f;
                }
            }
            else if (chasing)
            {
                bool lostTarget = (!chaseTargetNo || !chaseTargetNo.IsSpawned);
                bool chaseOver = Time.time >= chaseUntil;
                if (lostTarget || chaseOver)
                {
                    StopChase();
                    PickRandomTargetServer(); // resume normal roaming
                    lastSlitherYawDeg = 0f;
                }
            }
            else
            {
                bool arrived = (head.position - targetPos).sqrMagnitude <= arriveDistance * arriveDistance;
                bool timedOut = Time.time > currentTargetDeadline;
                if (arrived || timedOut)
                {
                    PickRandomTargetServer();
                    lastSlitherYawDeg = 0f;
                }
            }
        }

        // Level roll toward world-up (no bank in yaw-only mode)
        rollStep = suppressRoll ? 0f : ComputeRollLevelStep();

        // Pure yaw slither delta
        slitherYawDelta = suppressSlither ? 0f : ComputeSlitherYawDelta();

        // Combine and apply once (local axes)
        float yawDelta = steerYaw + slitherYawDelta;
        float pitchDelta = steerPitch;
        float rollDelta = rollStep;
        ApplyLocalRotation(yawDelta, pitchDelta, rollDelta);

        // Secondary guard: if still timing out/arriving when not chasing
        if (targetPos != Vector3.zero)
        {
            if (chasing)
            {
                bool lostTarget = (!chaseTargetNo || !chaseTargetNo.IsSpawned);
                bool chaseOver = Time.time >= chaseUntil;
                if (lostTarget || chaseOver)
                {
                    StopChase();
                    PickRandomTargetServer();
                    lastSlitherYawDeg = 0f;
                }
            }
            else
            {
                bool arrived = (head.position - targetPos).sqrMagnitude <= arriveDistance * arriveDistance;
                bool timedOut = Time.time > currentTargetDeadline;
                if (arrived || timedOut)
                {
                    PickRandomTargetServer();
                    lastSlitherYawDeg = 0f;
                }
            }
        }
    }

    /// <summary>
    /// Computes limited yaw and pitch steps (degrees) to steer the head toward a target.
    /// </summary>
    private void ComputeSteer(Vector3 worldTarget, out float yawStepDeg, out float pitchStepDeg)
    {
        yawStepDeg = 0f; pitchStepDeg = 0f;

        Vector3 toWS = worldTarget - head.position;
        if (toWS.sqrMagnitude < 0.0001f) return;

        Vector3 toLocal = head.InverseTransformDirection(toWS.normalized);

        // yaw around local Y (left/right)
        float yawErrDeg = Mathf.Rad2Deg * Mathf.Atan2(toLocal.x, Mathf.Max(0.0001f, toLocal.z));
        yawStepDeg = Mathf.Clamp(yawErrDeg, -yawTurnSpeedDeg * Time.deltaTime, yawTurnSpeedDeg * Time.deltaTime);

        // pitch around local X (nose up/down). Negative makes nose go up for +Y target
        float pitchErrDeg = -Mathf.Rad2Deg * Mathf.Atan2(toLocal.y, Mathf.Max(0.0001f, toLocal.z));
        pitchStepDeg = Mathf.Clamp(pitchErrDeg, -pitchTurnSpeedDeg * Time.deltaTime, pitchTurnSpeedDeg * Time.deltaTime);
    }

    /// <summary>
    /// Global altitude guard:
    /// - If head Y is below soft guard height, forbid any nose-down pitch and push up quickly.
    /// - If at/under hard guard height, push up even faster (snap).
    /// This does not teleport; it just forces pitch so you never continue diving below the baseline.
    /// </summary>
    private void ApplyGlobalAltitudeGuard(ref float pitchStepDeg)
    {
        if (!groundMarker) return;

        float baselineY = groundMarker.position.y;
        float softY = baselineY + Mathf.Max(0f, softGuardAbove);
        float hardY = baselineY + Mathf.Max(0f, hardGuardAbove);
        float y = head.position.y;

        // Hard guard: at/under hardY => snap up strongly
        if (y <= hardY)
        {
            // In your system, negative pitch is nose-up. Force a minimum nose-up snap this frame.
            float minUp = -hardSnapSpeedDeg * Time.deltaTime;
            if (pitchStepDeg > minUp) pitchStepDeg = minUp;
            return;
        }

        // Soft guard: below softY => prevent any further nose-down; if pointing down, force nose-up quickly
        if (y <= softY)
        {
            // No more nose-down while in the soft zone
            if (pitchStepDeg > 0f) pitchStepDeg = 0f;

            // If the head is pointing downward (forward.y < 0), add an aggressive nose-up push
            if (head.forward.y < 0f)
            {
                float up = -softLevelSpeedDeg * Time.deltaTime; // negative = nose-up
                if (pitchStepDeg > up) pitchStepDeg = up;
            }
        }
    }

    /// <summary>
    /// Computes a roll step (degrees) to level head.up toward world up.
    /// Uses mild leveling when already fairly upright to avoid jitter.
    /// </summary>
    private float ComputeRollLevelStep()
    {
        float desiredRoll = Vector3.SignedAngle(head.up, Vector3.up, head.forward); // degrees
        float dot = Vector3.Dot(head.up, Vector3.up);
        float speed = rollUprightSpeedDeg;
        if (dot > rollUprightDotThreshold) speed *= mildLevelingFactor;
        return Mathf.Clamp(desiredRoll, -speed * Time.deltaTime, speed * Time.deltaTime);
    }

    /// <summary>
    /// Returns the yaw delta (degrees) for this frame based on a sinusoidal slither.
    /// Uses phase accumulation and delta so there is no drift or snap.
    /// </summary>
    private float ComputeSlitherYawDelta()
    {
        if (!slitherEnabled || slitherFrequency <= 0f) return 0f;

        slitherPhase += Mathf.PI * 2f * slitherFrequency * Time.deltaTime;

        float s = Mathf.Sin(slitherPhase);
        float desiredYaw = slitherYawAmplitudeDeg * s;    // absolute target for this frame
        float dYaw = desiredYaw - lastSlitherYawDeg;      // delta to apply this frame
        lastSlitherYawDeg = desiredYaw;
        return dYaw;
    }

    /// <summary>
    /// Applies a single combined local rotation (yaw, then pitch, then roll) to the head.
    /// Using one composite quaternion keeps rotation order deterministic and stable.
    /// </summary>
    private void ApplyLocalRotation(float yawDeg, float pitchDeg, float rollDeg)
    {
        Quaternion dq =
            Quaternion.AngleAxis(yawDeg, head.up) *
            Quaternion.AngleAxis(pitchDeg, head.right) *
            Quaternion.AngleAxis(rollDeg, head.forward);

        head.rotation = dq * head.rotation;
    }

    /// Suppress slither and roll while an action needs precise aiming.
    /// Call with (true, true) to disable both; restore with (false, false).
    public void SetSteeringSuppression(bool noSlither, bool noRoll)
    {
        if (!IsServer) return;
        suppressSlither = noSlither;
        suppressRoll = noRoll;
    }
    public void SetSpeedImmediate(float value)
    {
        if (!IsServer) return;
        value = Mathf.Max(0f, value);
        speedTarget = value;
        // currentForwardSpeed is your private runtime speed; set it directly here since we are inside the class.
        currentForwardSpeed = value;
    }
    // ========================== Phase / Context ==========================

    private void Server_UpdatePhase()
    {
        if (!bossHealth) return;
        float pct = bossHealth.MaxHP > 0f ? bossHealth.CurrentHP / bossHealth.MaxHP : 1f;
        int p = (pct <= phase2HpPct) ? 2 : 1;
        if (phase.Value != p) phase.Value = p;
    }

    public bool Phase2 => phase.Value >= 2;

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

    // Public API for actions:
    public void SetAttackOverride(Vector3 worldPos, float timeoutSeconds)
    {
        SetAttackOverride(worldPos, timeoutSeconds, verticalY: false);
    }

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

    // ========================== Waypoints ==========================
    private Vector3 GetCurrentTargetPosition()
    {
        if (waypoints == null || waypoints.Count == 0) return Vector3.zero;
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

        int next = Random.Range(0, waypoints.Count);
        if (waypoints.Count > 1 && next == currentTargetIndex.Value)
            next = (next + 1) % waypoints.Count;

        currentTargetIndex.Value = next;

        // Reset timeout for this new target
        currentTargetDeadline = Time.time + Mathf.Max(0.1f, targetTimeoutSeconds);
    }

    // ========================== Chase API ==========================
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

    public Transform Head => head;
}
