using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Unity.Netcode;
using UnityEngine;

/// Moves on a fixed Y plane (first waypoint's Y), yaw-only, continuously firing homing missiles.
public class SnakeSimpleMissileAction : AttackActionBase
{
    [Header("Boss Binding")]
    [SerializeField] private SnakeBossController controller;

    [Header("Missile Prefab")]
    [SerializeField] private NetworkObject missilePrefab;

    [Header("Launch Points")]
    [SerializeField] private Transform[] shootPoints;
    [SerializeField] private float launchInterval = 0.15f;

    [Header("Counts")]
    [SerializeField] private int missilesPerPoint = 1;
    [SerializeField] private int maxTotalMissiles = 60;

    [Header("Targeting")]
    [SerializeField] private bool roundRobinTargets = true;

    [Header("Missile Settings")]
    [SerializeField] private float initialSpeed = 10f;
    [SerializeField] private float rotateDegPerSec = 90f;
    [SerializeField] private float lifetime = 8f;

    [Header("Explosion/Damage")]
    [SerializeField] private float explosionRadius = 2.5f;
    [SerializeField] private int explosionDamage = 15;
    [SerializeField] private float explosionKnockback = 16f;
    [SerializeField] private float explosionUpBias = 0.1f;
    [SerializeField] private LayerMask explodeOnLayers;

    [Header("Missile Health")]
    [SerializeField] private int missileHP = 1;

    [Header("Execution Constraints")]
    [SerializeField] private float minBossWorldY = 2f;

    // ---------------- Moving barrage bits ----------------
    [Header("Travel Path")]
    [SerializeField] private int waypointsToVisit = 3;
    [SerializeField] private float travelSpeed = 6.0f;
    [SerializeField] private float arriveRadius = 2.5f;
    [SerializeField] private float travelTimeoutPad = 0.8f;
    [SerializeField] private bool useControllerWaypoints = true;
    [SerializeField] private List<Transform> candidateWaypoints = new();

    [Header("Hand-off")]
    [SerializeField] private bool endWithFaceAndIdle = true;
    [SerializeField] private bool disableAiDuringMove = true;

    // -------- Pitch management (yaw-only while traveling) --------
    [Header("Yaw-only Steering While Traveling")]
    [SerializeField] private bool yawOnlyDuringTravel = true;

    [Header("Flatten Before Travel")]
    [SerializeField] private float pitchFlattenSpeedDeg = 140f;
    [SerializeField] private float pitchFlattenTimeout = 1.25f;
    [SerializeField] private float flattenForwardDistance = 10f;
    [SerializeField] private float flattenPitchEpsilonDeg = 1.5f;

    [Header("Vertical Arrivals")]
    [SerializeField] private float planeAlignSpeed = 8f;
    [SerializeField] private float planeAlignEpsilon = 0.05f;

    [Header("End Climb (after last waypoint)")]
    [SerializeField] private float endClimbHeightAbovePlane = 6f;
    [SerializeField] private float endClimbSpeed = 10f;
    [SerializeField] private float endClimbTimeout = 3.0f;

    [Header("Safety")]
    [Tooltip("If pitch is ever 0 due to bad state, restore to this value at start of move.")]
    [SerializeField] private float fallbackPitchTurnSpeedDeg = 55f;

    [Header("Travel Pitch Guard")]
    [Tooltip("If abs(head pitch) exceeds this while traveling, snap-level back toward 0° immediately.")]
    [SerializeField] private float travelMaxPitchAbsDeg = 6f;
    [SerializeField] private float travelPitchSnapSpeedDeg = 220f;
    [SerializeField] private float travelPitchHysteresisDeg = 1f;
    [SerializeField] private float travelPitchSnapLookahead = 12f;
    [Tooltip("Failsafe: if a travel/climb phase exceeds this time, force progress to avoid getting stuck.")]
    [SerializeField] private float hardPhaseTimeout = 6.0f;

    // reflection cache & yaw-only state
    private FieldInfo _pitchField;
    private float _savedPitchSpeed = 0f;
    private bool _yawOnlyActive = false;

    private int rrIndex = 0;
    private bool keepFiring = false;
    private Coroutine fireLoopCo;
    private bool _active = false;
    private Coroutine _mainCo = null;

    private bool Valid() => _active && controller != null && controller.Head != null;

    // --------------- Utility AI hooks ---------------
    public override bool CanExecute(BossContext ctx)
    {
        if (!base.CanExecute(ctx)) return false;
        if (ctx.Boss == null || !ctx.Boss.IsServer) return false;

        if (!controller) controller = ctx.Boss as SnakeBossController ?? controller;
        if (!controller) return false;

        if (!missilePrefab || shootPoints == null || shootPoints.Length == 0) return false;
        if (ctx.Players == null || ctx.Players.Count == 0) return false;

        var head = controller.Head;
        if (!head || head.position.y < minBossWorldY) return false;

        var pool = GetWaypointPool();
        if (pool == null || pool.Count == 0) return false;

        return true;
    }

    public override float ReturnScore(BossContext ctx) => CanExecute(ctx) ? 1f : 0f;

    public override void ExecuteMove(BossContext ctx)
    {
        if (!CanExecute(ctx)) return;

        IsBusy = true;
        MarkUsed(ctx);

        if (disableAiDuringMove) SnakeUtilityAi.active = false;

        controller.ClearMinYClamp();
        controller.SuppressAltitudeGuard(true);
        controller.DisableRoamTarget();

        HardResetSteeringAtStart();
        controller.ExitIdleHover();
        controller.SetSpeedImmediate(0f); // wait until ClimbToPlaneY starts driving

        // ensure pitch wasn't left locked from a previous run
        ForcePitchUnlockedAndPositive();

        // Build targets (ctx.Players is IReadOnlyList)
        var targets = new List<NetworkObject>();
        if (ctx.Players != null)
        {
            foreach (var go in ctx.Players)
            {
                if (!go) continue;
                var no = go.GetComponent<NetworkObject>();
                if (no && no.IsSpawned) targets.Add(no);
            }
        }

        _active = true;
        _mainCo = controller.StartCoroutine(MainRoutine(targets));
    }

    private void HardResetSteeringAtStart()
    {
        controller.StopChase();
        controller.ClearAttackOverride();
        controller.SetSteeringSuppression(noSlither: false, noRoll: false);
        SetYawOnlySteering(false);
    }

    private void ForcePitchUnlockedAndPositive()
    {
        if (_pitchField == null)
            _pitchField = typeof(SnakeBossController).GetField("pitchTurnSpeedDeg", BindingFlags.Instance | BindingFlags.NonPublic);

        if (_pitchField != null)
        {
            float p = (float)_pitchField.GetValue(controller);
            if (p <= 0.0001f)
                _pitchField.SetValue(controller, Mathf.Max(1f, fallbackPitchTurnSpeedDeg));
        }
        _yawOnlyActive = false;
    }

    // --------------- Core Routine ---------------
    private IEnumerator MainRoutine(List<NetworkObject> targets)
    {
        var pool = GetWaypointPool();
        if (pool == null || pool.Count == 0) { Cleanup(); yield break; }

        Transform first = GetClosestWaypointXZ(pool, controller.Head.position);
        if (!first) { Cleanup(); yield break; }

        float planeY = first.position.y;
        List<Transform> rest = PickDistinctWaypointsExcluding(pool, first, Mathf.Max(0, waypointsToVisit - 1));

        controller.SetSteeringSuppression(noSlither: false, noRoll: false);
        controller.StopChase();
        controller.ClearAttackOverride();
        //controller.SetSpeedImmediate(travelSpeed);

        // 1) Climb to plane Y (Y-only)
        yield return ClimbToPlaneY(planeY);

        // 2) Flatten pitch
        yield return FlattenPitchAtCurrentPos();

        // Lock pitch for yaw-only travel
        SetYawOnlySteering(true);

        // Now that steering is locked and we’re about to traverse on the plane,
        // give the snake its forward travel speed.
        controller.SetSpeedImmediate(travelSpeed);

        // Start firing
        keepFiring = true;
        fireLoopCo = controller.StartCoroutine(FireLoop(targets));

        // 3) Traverse remaining waypoints on the fixed plane (XZ arrival only)
        for (int i = 0; i < rest.Count; i++)
        {
            var wp = rest[i];
            if (!wp) continue;
            yield return MoveToPointOnPlane(wp.position, planeY);
        }

        // stop firing
        keepFiring = false;
        if (fireLoopCo != null) { controller.StopCoroutine(fireLoopCo); fireLoopCo = null; }

        // 4) Optional climb above plane
        if (endClimbHeightAbovePlane > 0.01f)
            yield return ClimbAbovePlaneY(planeY, endClimbHeightAbovePlane);

        // release yaw-only BEFORE handing off to idle
        SetYawOnlySteering(false);

        controller.StopChase();
        controller.ClearAttackOverride();
        controller.SetSteeringSuppression(noSlither: true, noRoll: false);

        if (endWithFaceAndIdle)
            controller.BeginFaceThenIdleImmediate();

        Cleanup();
    }

    // === PHASES (hard-pin override & timeouts) =====================
    private IEnumerator MoveToPointOnPlane(Vector3 waypointPos, float planeY)
    {
        float aa = Mathf.Max(0.1f, arriveRadius);
        float aa2 = aa * aa;

        // time budget based on XZ distance:
        Vector3 start = controller.Head.position; start.y = 0f;
        Vector3 end = waypointPos; end.y = 0f;
        float distXZ = Vector3.Distance(start, end);
        float budget = (distXZ / Mathf.Max(0.01f, travelSpeed)) + Mathf.Max(0.1f, travelTimeoutPad);
        float deadline = Time.time + Mathf.Min(hardPhaseTimeout, budget + 0.5f);

        while (Valid() && Time.time < deadline)
        {
            controller.StopChase();

            // travel pitch guard: keep pitch near 0° while traveling
            if (Mathf.Abs(GetPitchDeg()) > travelMaxPitchAbsDeg)
            {
                yield return QuickLevelDuringTravel(planeY);
            }

            Vector3 targetPos = new Vector3(waypointPos.x, planeY, waypointPos.z);
            controller.SetAttackOverride(targetPos, 0.25f, verticalY: false);

            Vector3 dxz = controller.Head.position - targetPos; dxz.y = 0f;
            if (dxz.sqrMagnitude <= aa2)
            {
                controller.ClearAttackOverride();
                break;
            }
            yield return null;
        }

        controller.ClearAttackOverride(); // safety
    }

    private IEnumerator ClimbToPlaneY(float planeY)
    {
        float eps = Mathf.Max(0.01f, planeAlignEpsilon);

        // timeout based on vertical distance (plus hard cap)
        float d = Mathf.Abs(controller.Head.position.y - planeY);
        float budget = (d / Mathf.Max(0.01f, planeAlignSpeed)) + 0.75f;
        float deadline = Time.time + Mathf.Min(hardPhaseTimeout, budget + 0.5f);

        // temporarily disable slither noise during the climb
        controller.SetSteeringSuppression(noSlither: true, noRoll: false);

        // stash pitch speed and boost if needed for faster leveling
        var pf = typeof(SnakeBossController).GetField("pitchTurnSpeedDeg", BindingFlags.Instance | BindingFlags.NonPublic);
        float origPitch = pf != null ? (float)pf.GetValue(controller) : fallbackPitchTurnSpeedDeg;

        while (Valid() && Time.time < deadline)
        {
            controller.StopChase();
            SetYawOnlySteering(false); // ensure pitch is unlocked for climb

            // If we are BELOW the plane and the nose is DOWN, freeze forward speed to avoid descending.
            bool below = (controller.Head.position.y < planeY - eps);
            bool noseDown = (controller.Head.forward.y < 0f);

            if (below && noseDown)
            {
                controller.SetSpeedImmediate(0f); // no forward drift downward
                if (pf != null) pf.SetValue(controller, Mathf.Max(origPitch, travelPitchSnapSpeedDeg)); // quick level
            }
            else
            {
                if (pf != null) pf.SetValue(controller, Mathf.Max(1f, origPitch));
                controller.SetSpeedImmediate(planeAlignSpeed);
            }

            // Use a tiny XZ look-ahead so pitch math never degenerates (z0)
            Vector3 fwdXZ = new Vector3(controller.Head.forward.x, 0f, controller.Head.forward.z);
            if (fwdXZ.sqrMagnitude < 1e-6f) fwdXZ = controller.Head.right;
            fwdXZ.Normalize();

            Vector3 climbTo = new Vector3(controller.Head.position.x, planeY, controller.Head.position.z)
                              + fwdXZ * 0.05f;

            controller.SetAttackOverride(climbTo, 0.25f, verticalY: true);

            // arrival purely on Y
            if (Mathf.Abs(controller.Head.position.y - planeY) <= eps)
            {
                controller.ClearAttackOverride();
                break;
            }

            yield return null;
        }

        // restore pitch speed and steering suppression for next phases
        if (pf != null) pf.SetValue(controller, Mathf.Max(1f, origPitch));
        controller.SetSteeringSuppression(noSlither: false, noRoll: false);

        controller.ClearAttackOverride(); // safety
    }

    private IEnumerator FlattenPitchAtCurrentPos()
    {
        if (!Valid()) yield break;

        SetYawOnlySteering(false);

        var pf = typeof(SnakeBossController).GetField("pitchTurnSpeedDeg", BindingFlags.Instance | BindingFlags.NonPublic);
        float origPitch = pf != null ? (float)pf.GetValue(controller) : 0f;
        if (pf != null) pf.SetValue(controller, Mathf.Max(0f, pitchFlattenSpeedDeg));

        float t = 0f, timeout = Mathf.Clamp(pitchFlattenTimeout, 0.1f, hardPhaseTimeout);
        float deadline = Time.time + timeout;

        while (Valid() && Time.time < deadline)
        {
            controller.StopChase();

            Vector3 fwd = controller.Head.forward;
            Vector3 fwdXZ = new Vector3(fwd.x, 0f, fwd.z);
            if (fwdXZ.sqrMagnitude < 1e-4f) fwdXZ = controller.Head.right;
            fwdXZ.Normalize();

            Vector3 tgt = controller.Head.position + fwdXZ * Mathf.Max(1f, flattenForwardDistance);
            controller.SetAttackOverride(tgt, 0.2f, verticalY: false);

            float pitchDeg = GetPitchDeg();
            if (Mathf.Abs(pitchDeg) <= flattenPitchEpsilonDeg)
            {
                controller.ClearAttackOverride();
                break;
            }

            t += Time.deltaTime;
            yield return null;
        }

        controller.ClearAttackOverride();
        if (pf != null) pf.SetValue(controller, Mathf.Max(1f, origPitch));
    }

    private IEnumerator ClimbAbovePlaneY(float planeY, float offset)
    {
        float targetY = planeY + Mathf.Max(0f, offset);
        float eps = Mathf.Max(0.02f, controller != null ? controller.VerticalArriveEpsilon : 0.15f);

        float d = Mathf.Abs(controller.Head.position.y - targetY);
        float budget = (d / Mathf.Max(0.01f, endClimbSpeed)) + 0.75f;
        float deadline = Time.time + Mathf.Min(hardPhaseTimeout, budget + 0.5f);

        while (Valid() && Time.time < deadline)
        {
            controller.StopChase();

            SetYawOnlySteering(false);
            controller.SetSteeringSuppression(noSlither: false, noRoll: false);
            controller.SetSpeedImmediate(endClimbSpeed);

            Vector3 climbTo = new Vector3(controller.Head.position.x, targetY, controller.Head.position.z);
            controller.SetAttackOverride(climbTo, 0.25f, verticalY: true);

            float y = controller.Head.position.y;
            if (y >= (targetY - eps))
            {
                controller.ClearAttackOverride();
                break;
            }
            yield return null;
        }

        controller.SetSpeedImmediate(0f);
        controller.StopChase();
        controller.ClearAttackOverride();
    }

    // ----- Travel Pitch Guard -----
    private IEnumerator QuickLevelDuringTravel(float planeY)
    {
        var pf = typeof(SnakeBossController).GetField("pitchTurnSpeedDeg", BindingFlags.Instance | BindingFlags.NonPublic);
        float originalPitchSpeed = pf != null ? (float)pf.GetValue(controller) : fallbackPitchTurnSpeedDeg;

        SetYawOnlySteering(false);
        if (pf != null) pf.SetValue(controller, Mathf.Max(originalPitchSpeed, travelPitchSnapSpeedDeg));

        float timeout = Mathf.Min(0.75f, hardPhaseTimeout);
        float deadline = Time.time + timeout;

        while (Valid() && Time.time < deadline)
        {
            Vector3 fwd = controller.Head.forward;
            Vector3 fwdXZ = new Vector3(fwd.x, 0f, fwd.z);
            if (fwdXZ.sqrMagnitude < 1e-4f) fwdXZ = controller.Head.right;
            fwdXZ.Normalize();

            Vector3 pos = controller.Head.position;
            Vector3 tgt = new Vector3(pos.x, planeY, pos.z) + fwdXZ * Mathf.Max(1f, travelPitchSnapLookahead);
            controller.SetAttackOverride(tgt, 0.2f, verticalY: false);

            if (Mathf.Abs(GetPitchDeg()) <= Mathf.Max(0.1f, travelMaxPitchAbsDeg - travelPitchHysteresisDeg))
                break;

            yield return null;
        }

        controller.ClearAttackOverride();
        if (pf != null) pf.SetValue(controller, Mathf.Max(1f, originalPitchSpeed));
        SetYawOnlySteering(true); // resume yaw-only
    }

    private float GetPitchDeg()
    {
        return Mathf.Asin(Mathf.Clamp(controller.Head.forward.y, -1f, 1f)) * Mathf.Rad2Deg;
    }

    // --------------- Continuous fire loop ---------------
    private IEnumerator FireLoop(List<NetworkObject> targets)
    {
        int launched = 0, spIndex = 0;

        while (_active && keepFiring && launched < Mathf.Max(1, maxTotalMissiles))
        {
            var point = shootPoints[spIndex % shootPoints.Length];
            spIndex++;

            if (point)
            {
                for (int b = 0; b < Mathf.Max(1, missilesPerPoint); b++)
                {
                    if (!keepFiring || launched >= maxTotalMissiles) break;

                    NetworkObject targetNO = PickTargetForShot(targets, point.position);

                    var noMissile = Instantiate(missilePrefab, point.position, point.rotation);
                    var missile = noMissile.GetComponent<SnakeHomingMissile>();
                    if (missile != null)
                    {
                        var cfg = new SnakeHomingMissile.Config
                        {
                            targetRef = targetNO,
                            speed = initialSpeed,
                            rotateDegPerSec = rotateDegPerSec,
                            maxLifetime = lifetime,
                            explosionRadius = explosionRadius,
                            explosionDamage = explosionDamage,
                            explosionKnockback = explosionKnockback,
                            explosionUpBias = explosionUpBias,
                            explodeOnLayers = explodeOnLayers,
                            hitPoints = missileHP
                        };
                        missile.ApplyConfig(cfg);
                    }

                    NetworkSfxRelay.All_Play2D("MissileLaunch", 1f, Random.Range(0.96f, 1.04f));
                    noMissile.Spawn(true);
                    missile?.Activate();

                    launched++;
                }
            }
            yield return new WaitForSeconds(Mathf.Max(0.01f, launchInterval));
        }
    }

    private NetworkObject PickTargetForShot(List<NetworkObject> candidates, Vector3 fromPos)
    {
        if (candidates == null || candidates.Count == 0) return null;

        if (roundRobinTargets)
        {
            var no = candidates[rrIndex % candidates.Count];
            rrIndex++;
            return no;
        }
        else
        {
            NetworkObject best = null;
            float bestSqr = float.PositiveInfinity;
            for (int i = 0; i < candidates.Count; i++)
            {
                var no = candidates[i];
                if (!no || !no.IsSpawned) continue;
                float sq = (no.transform.position - fromPos).sqrMagnitude;
                if (sq < bestSqr) { bestSqr = sq; best = no; }
            }
            return best ?? candidates[0];
        }
    }

    // --------------- Helpers / Teardown ---------------
    private void Cleanup()
    {
        if (!_active) return;
        _active = false;

        keepFiring = false;
        if (fireLoopCo != null && controller != null) { controller.StopCoroutine(fireLoopCo); fireLoopCo = null; }

        // Do not StopCoroutine(_mainCo) here; this may be called from inside it.
        _mainCo = null;

        if (controller)
        {
            controller.StopChase();
            controller.ClearAttackOverride();
            controller.SetSteeringSuppression(noSlither: false, noRoll: false);
            controller.SuppressAltitudeGuard(false); // restore guard
        }

        SetYawOnlySteering(false);
        SnakeUtilityAi.active = true;
        IsBusy = false;
    }

    private List<Transform> GetWaypointPool()
    {
        if (!controller) return candidateWaypoints;

        if (useControllerWaypoints)
        {
            var fi = typeof(SnakeBossController).GetField("waypoints", BindingFlags.Instance | BindingFlags.NonPublic);
            var list = fi != null ? (List<Transform>)fi.GetValue(controller) : null;
            if (list != null && list.Count > 0) return list;
        }
        return candidateWaypoints;
    }

    private static Transform GetClosestWaypointXZ(List<Transform> pool, Vector3 from)
    {
        Transform best = null;
        float bestSqr = float.PositiveInfinity;
        Vector3 fromXZ = from; fromXZ.y = 0f;

        for (int i = 0; i < pool.Count; i++)
        {
            var t = pool[i];
            if (!t) continue;
            Vector3 txz = t.position; txz.y = 0f;
            float sq = (txz - fromXZ).sqrMagnitude;
            if (sq < bestSqr) { bestSqr = sq; best = t; }
        }
        return best;
    }

    private static List<Transform> PickDistinctWaypointsExcluding(List<Transform> pool, Transform exclude, int count)
    {
        var outList = new List<Transform>(Mathf.Min(count, Mathf.Max(0, pool.Count - 1)));
        if (pool == null || pool.Count == 0) return outList;

        var idx = new List<int>(pool.Count);
        for (int i = 0; i < pool.Count; i++)
        {
            if (!pool[i]) continue;
            if (pool[i] == exclude) continue;
            idx.Add(i);
        }

        for (int i = 0; i < idx.Count; i++)
        {
            int k = Random.Range(i, idx.Count);
            (idx[i], idx[k]) = (idx[k], idx[i]);
        }

        int take = Mathf.Min(count, idx.Count);
        for (int i = 0; i < take; i++)
            outList.Add(pool[idx[i]]);

        return outList;
    }

    private void SetYawOnlySteering(bool on)
    {
        if (!yawOnlyDuringTravel || controller == null) return;

        if (_pitchField == null)
            _pitchField = typeof(SnakeBossController)
                .GetField("pitchTurnSpeedDeg", BindingFlags.Instance | BindingFlags.NonPublic);

        if (_pitchField == null) return;

        if (on && !_yawOnlyActive)
        {
            _savedPitchSpeed = (float)_pitchField.GetValue(controller);
            _pitchField.SetValue(controller, 0f);   // lock pitch
            _yawOnlyActive = true;
        }
        else if (!on && _yawOnlyActive)
        {
            _pitchField.SetValue(controller, Mathf.Max(1f, _savedPitchSpeed)); // restore
            _yawOnlyActive = false;
        }
    }

    private void OnDisable()
    {
        _active = false;
        SetYawOnlySteering(false);
        keepFiring = false;

        if (fireLoopCo != null && controller != null) { controller.StopCoroutine(fireLoopCo); fireLoopCo = null; }
        if (_mainCo != null && controller != null) { controller.StopCoroutine(_mainCo); _mainCo = null; }

        if (controller)
        {
            controller.ClearAttackOverride();
            controller.SetSpeedImmediate(0f);
            controller.SuppressAltitudeGuard(false);
            controller.StopChase();
        }
    }

    private void OnDestroy()
    {
        OnDisable();
    }
}
