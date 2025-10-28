using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Unity.Netcode;
using UnityEngine;

public class SnakeBigOrbVolleyAction : AttackActionBase
{
    [Header("Bindings")]
    [Tooltip("Boss controller that owns movement/steering APIs.")]
    [SerializeField] private SnakeBossController controller;
    [Tooltip("World-space mouth transform. Orbs spawn here and follow it while charging.")]
    [SerializeField] private Transform mouth;

    [Header("Waypoints / Travel")]
    [Tooltip("How close (meters) the head must get to the chosen perch to count as arrived.")]
    [SerializeField] private float arriveRadius = 2.5f;
    [Tooltip("Forward speed while flying toward the perch.")]
    [SerializeField] private float travelSpeed = 12f;
    [Tooltip("Extra time (seconds) added to the travel ETA as a safety budget.")]
    [SerializeField] private float travelTimeoutPad = 0.8f;
    [Tooltip("Vertical pop height above the perch Y before charging begins.")]
    [SerializeField] private float ascendHeight = 8f;
    [Tooltip("Vertical climb speed during the pop.")]
    [SerializeField] private float ascendSpeed = 10f;
    [Tooltip("Maximum time (seconds) allowed for the pop before continuing anyway.")]
    [SerializeField] private float ascendTimeout = 2.5f;

    [Tooltip("If true, read waypoints from the controller; otherwise use Fallback Waypoints below.")]
    [SerializeField] private bool useControllerWaypoints = true;
    [Tooltip("Fallback list used when controller has no internal waypoints.")]
    [SerializeField] private List<Transform> fallbackWaypoints = new();

    [Header("Volley (multi-shot)")]
    [Tooltip("Number of orbs fired in this sequence.")]
    [SerializeField] private int volleys = 3;
    [Tooltip("Per-volley charge duration (seconds). If shorter than 'volleys', last value repeats.")]
    [SerializeField] private List<float> chargeTimes = new() { 1.0f };
    [Tooltip("Per-volley orb start scale. If shorter than 'volleys', last value repeats.")]
    [SerializeField] private List<float> orbStartScales = new() { 0.2f };
    [Tooltip("Per-volley orb end scale. If shorter than 'volleys', last value repeats.")]
    [SerializeField] private List<float> orbEndScales = new() { 1.0f };

    [Header("Orb Projectile")]
    [Tooltip("Networked orb prefab (must include MouthOrbProjectile + NetworkObject).")]
    [SerializeField] private NetworkObject orbPrefab;
    [Tooltip("Orb flight speed after launch.")]
    [SerializeField] private float orbSpeed = 36f;
    [Tooltip("Orb lifetime (seconds) before self-detonation if nothing is hit.")]
    [SerializeField] private float orbLife = 6f;

    [Header("Look Up (before charge)")]
    [Tooltip("How close to straight up the head must face before we stop the look-up (dot with Vector3.up).")]
    [SerializeField, Range(0.7f, 0.999f)] private float lookUpDot = 0.92f;
    [Tooltip("Small forward speed used while rotating up to make the turn feel alive.")]
    [SerializeField] private float lookUpMoveSpeed = 3.0f;
    [Tooltip("Temporary yaw turn speed (deg/s) while looking up.")]
    [SerializeField] private float lookUpYawDeg = 140f;
    [Tooltip("Temporary pitch turn speed (deg/s) while looking up.")]
    [SerializeField] private float lookUpPitchDeg = 120f;
    [Tooltip("Hard timeout (seconds) for the look-up phase.")]
    [SerializeField] private float lookUpMaxTime = 1.0f;
    [Tooltip("How far above the head the 'look up' target is placed (+Y).")]
    [SerializeField] private float lookUpAimDistance = 15f;
    [Header("Apex Offset")]
    [SerializeField] private float perchForwardOffset = 6f;
    [SerializeField] private bool apexUseAscendHeight = true; // true: perchY + ascendHeight, false: perchY

    [Header("Charge (head frozen)")]
    [Tooltip("If true, the body wags during charge while the head stays frozen.")]
    [SerializeField] private bool enableWag = true;
    [Tooltip("Wag amplitude (deg) at the start of charge.")]
    [SerializeField] private float wagBaseAmplitude = 1.1f;
    [Tooltip("Wag frequency (Hz) at the start of charge.")]
    [SerializeField] private float wagBaseFrequency = 0.9f;
    [Tooltip("Wag amplitude (deg) at the end of charge.")]
    [SerializeField] private float wagEndAmplitude = 2.4f;
    [Tooltip("Wag frequency (Hz) at the end of charge.")]
    [SerializeField] private float wagEndFrequency = 1.5f;
    [Tooltip("Tail amplification while charging. >1 makes the tail wag more than the front.")]
    [SerializeField] private float wagTailMultiplierDuringCharge = 2.0f;

    [Header("Aim (short nudge motion)")]
    [Tooltip("Small speed used while snapping from 'look up' to face the target.")]
    [SerializeField] private float aimMoveSpeed = 5.0f;
    [Tooltip("Temporary yaw turn speed (deg/s) while aiming at the target.")]
    [SerializeField] private float aimYawDeg = 160f;
    [Tooltip("Temporary pitch turn speed (deg/s) while aiming at the target.")]
    [SerializeField] private float aimPitchDeg = 140f;
    [Tooltip("Fire threshold—how aligned the head must be with the target direction (dot).")]
    [SerializeField, Range(0.8f, 1f)] private float fireWhenDotAtLeast = 0.985f;
    [Tooltip("Hard timeout (seconds) for the aim phase (fires even if not perfectly aligned).")]
    [SerializeField] private float aimMaxTime = 0.6f;

    [Header("Recoil (moves whole body)")]
    [Tooltip("If true, apply a brief body-wide recoil move after firing.")]
    [SerializeField] private bool enableRecoil = true;
    [Tooltip("Backward displacement range (meters) applied during recoil.")]
    [SerializeField] private Vector2 recoilBackRange = new Vector2(3.0f, 6.0f);
    [Tooltip("Max random sideways displacement (meters) during recoil.")]
    [SerializeField] private float recoilLateralMax = 2.0f;
    [Tooltip("Upward bias (meters) added to the recoil move.")]
    [SerializeField] private float recoilUpBias = 0.5f;
    [Tooltip("Speed of the recoil move (meters/second).")]
    [SerializeField] private float recoilMoveSpeed = 9.0f;
    [Tooltip("Duration (seconds) of the recoil move target.")]
    [SerializeField] private float recoilTime = 0.22f;
    [Tooltip("Pause (seconds) after recoil to let the pose read before next cycle.")]
    [SerializeField] private float postFireHold = 0.10f;
    [Tooltip("Visual head kick magnitude (degrees).")]
    [SerializeField] private float recoilKickDeg = 12f;
    [Tooltip("Kick-in duration (seconds).")]
    [SerializeField] private float recoilKickIn = 0.06f;
    [Tooltip("Kick-out duration (seconds).")]
    [SerializeField] private float recoilKickOut = 0.16f;

    [Header("End / AI")]
    [Tooltip("If true, relocate to a waypoint and enter idle at the end of the sequence.")]
    [SerializeField] private bool relocateAndIdleAtEnd = true;

    [Header("Scoring")]
    [Tooltip("Utility score returned when this action is available.")]
    [SerializeField, Range(0f, 1f)] private float baseScore = 1f;

    // internals
    private Coroutine _mainCo;
    private bool _active;
    private ulong _lastTargetId = ulong.MaxValue;
    private FieldInfo _yawField;
    private FieldInfo _pitchField;

    public override bool CanExecute(BossContext ctx)
    {
        if (!base.CanExecute(ctx)) return false;
        if (ctx.Boss == null || !ctx.Boss.IsServer) return false;
        if (!controller || !mouth || !orbPrefab) return false;
        var pool = GetWaypointPool(); if (pool == null || pool.Count == 0) return false;
        return ctx.Players != null && ctx.Players.Count > 0;
    }
    public override float ReturnScore(BossContext ctx) => CanExecute(ctx) ? baseScore : 0f;

    public override void ExecuteMove(BossContext ctx)
    {
        if (!CanExecute(ctx)) return;
        IsBusy = true;
        MarkUsed(ctx);

        // Lock roll and slither from the first frame.
        controller.SetSteeringSuppression(noSlither: true, noRoll: false);
        controller.SetHardUprightLock(true);
        controller.SnapHeadRollUprightNow();
        controller.DisableRoamTarget();

        // Build targets
        var targets = new List<NetworkObject>();
        foreach (var go in ctx.Players)
        {
            if (!go) continue;
            var no = go.GetComponent<NetworkObject>();
            if (no && no.IsSpawned) targets.Add(no);
        }
        if (targets.Count == 0) { IsBusy = false; return; }

        _active = true;
        SnakeUtilityAi.active = false;

        if (_mainCo != null) controller.StopCoroutine(_mainCo);
        _mainCo = controller.StartCoroutine(MainRoutine(targets));
    }

    private IEnumerator MainRoutine(List<NetworkObject> players)
    {
        if (_yawField == null)
            _yawField = typeof(SnakeBossController).GetField("yawTurnSpeedDeg", BindingFlags.Instance | BindingFlags.NonPublic);
        if (_pitchField == null)
            _pitchField = typeof(SnakeBossController).GetField("pitchTurnSpeedDeg", BindingFlags.Instance | BindingFlags.NonPublic);

        var target = PickTarget(players, _lastTargetId);
        _lastTargetId = target ? target.OwnerClientId : ulong.MaxValue;

        var perch = PickFarthestWaypointFrom(target ? target.transform.position : controller.Head.position);
        if (!perch) { Cleanup(); yield break; }

        // travel to perch (roll locked)
        controller.StopChase();
        controller.ClearAttackOverride();
        controller.SetSteeringSuppression(noSlither: false, noRoll: false);
        controller.SetSpeedImmediate(Mathf.Max(0f, travelSpeed));

        float travelBudget = TravelTimeout(controller.Head.position, perch.position, travelSpeed, travelTimeoutPad);
        float travelDeadline = Time.time + travelBudget + 0.25f;

        while (_active && Time.time < travelDeadline)
        {
            Vector3 targetPos = perch.position;
            controller.SetAttackOverride(targetPos, 0.25f, verticalY: false);
            if ((controller.Head.position - targetPos).sqrMagnitude <= arriveRadius * arriveRadius)
            { controller.ClearAttackOverride(); break; }
            yield return null;
        }
        controller.ClearAttackOverride();

        // ascend
        Vector3 apex = perch.position + (perch.forward * Mathf.Max(0f, perchForwardOffset));
        apex.y = apexUseAscendHeight
            ? perch.position.y + Mathf.Max(0f, ascendHeight)   // perch Y + ascendHeight
            : perch.position.y;                                 // exactly the perch Y

        float apexSpeed = Mathf.Max(2f, ascendSpeed);
        controller.SetSpeedImmediate(apexSpeed);

        // natural radial travel (not vertical-only)
        float apexBudget = TravelTimeout(controller.Head.position, apex, apexSpeed, travelTimeoutPad);
        float apexDeadline = Time.time + apexBudget + 0.25f;

        while (_active && Time.time < apexDeadline)
        {
            controller.SetAttackOverride(apex, 0.25f, verticalY: false);

            bool arrived = (controller.Head.position - apex).sqrMagnitude <= arriveRadius * arriveRadius;
            if (arrived)
            {
                controller.ClearAttackOverride();
                break;
            }
            yield return null;
        }
        controller.ClearAttackOverride();

        // Lock slither + roll while volleying
        controller.SetSteeringSuppression(noSlither: true, noRoll: false);
        EnsurePitchUnlocked();

        // volley loop
        for (int v = 0; v < Mathf.Max(1, volleys) && _active; v++)
        {
            target = PickTarget(players, _lastTargetId);
            _lastTargetId = target ? target.OwnerClientId : _lastTargetId;

            float chargeT = GetIndexed(chargeTimes, v, 1.0f);
            float startS = GetIndexed(orbStartScales, v, 0.2f);
            float endS = GetIndexed(orbEndScales, v, 1.0f);

            // A) LOOK UP (with a bit of speed)
            yield return LookUpThenStop();

            // B) CHARGE (head frozen; body wags). Orb follows mouth until launch.
            var orbNO = Object.Instantiate(orbPrefab, mouth.position, mouth.rotation);
            orbNO.Spawn(true);
            NetworkSfxRelay.All_Play2D("ChargeUp");
            var proj = orbNO.GetComponent<MouthOrbProjectile>();
            if (proj) proj.ServerStartCharge(mouth, chargeT, startS, endS);

            Coroutine wagCo = null;
            if (enableWag) wagCo = controller.StartCoroutine(WagRampCoroutine(chargeT));
            yield return ChargeFreezeHead(chargeT); // head completely frozen

            // C) AIM (short nudge motion) — keep orb glued to mouth until fire
            yield return AimAtThenFire(target, proj);

            // D) RECOIL and ease wag out
            if (enableWag && wagCo != null) controller.StopCoroutine(wagCo);
            yield return RecoilMoveWithKick(mouth.forward);
            if (postFireHold > 0f) yield return new WaitForSeconds(postFireHold);
            yield return WagEaseOutCoroutine(0.18f);
            controller.SnapHeadRollUprightNow();
        }

        // finish
        controller.StopChase();
        controller.ClearAttackOverride();
        controller.SetSteeringSuppression(noSlither: false, noRoll: false); // unlock roll at the very end

        if (relocateAndIdleAtEnd)
            yield return controller.StartCoroutine(controller.CoRelocateToWaypointAndIdle());
        else
            controller.BeginFaceThenIdleImmediate();

        Cleanup();
    }

    // ---------------- stages ----------------

    private IEnumerator LookUpThenStop()
    {
        float saveYaw = _yawField != null ? (float)_yawField.GetValue(controller) : 0f;
        float savePit = _pitchField != null ? (float)_pitchField.GetValue(controller) : 0f;
        if (_yawField != null) _yawField.SetValue(controller, lookUpYawDeg);
        if (_pitchField != null) _pitchField.SetValue(controller, lookUpPitchDeg);

        controller.SetSpeedImmediate(lookUpMoveSpeed);
        controller.StopChase();

        float end = Time.time + Mathf.Max(0.05f, lookUpMaxTime);
        while (_active && Time.time < end)
        {
            Vector3 upAim = controller.Head.position + Vector3.up * Mathf.Max(1f, lookUpAimDistance);
            controller.SetAttackOverride(upAim, 0.25f, verticalY: true);

            float dotUp = Vector3.Dot(controller.Head.forward.normalized, Vector3.up);
            if (dotUp >= lookUpDot) break;
            yield return null;
        }
        controller.ClearAttackOverride();

        controller.SetSpeedImmediate(0f); // ensure we fully stop before charge
        if (_yawField != null) _yawField.SetValue(controller, saveYaw);
        if (_pitchField != null) _pitchField.SetValue(controller, savePit);
    }

    private IEnumerator ChargeFreezeHead(float duration)
    {
        // Freeze ALL steering and forward motion
        float saveYaw = _yawField != null ? (float)_yawField.GetValue(controller) : 0f;
        float savePit = _pitchField != null ? (float)_pitchField.GetValue(controller) : 0f;

        if (_yawField != null) _yawField.SetValue(controller, 0f);
        if (_pitchField != null) _pitchField.SetValue(controller, 0f);
        controller.SetSteeringSuppression(noSlither: true, noRoll: true);
        controller.StopChase();
        controller.ClearAttackOverride();
        controller.SetSpeedImmediate(0f);

        // Hard freeze the head transform (position + rotation)
        Quaternion freezeRot = controller.Head ? controller.Head.rotation : Quaternion.identity;
        Vector3 freezePos = controller.Head ? controller.Head.position : Vector3.zero;

        float until = Time.time + Mathf.Max(0.01f, duration);
        while (_active && Time.time < until)
        {
            if (controller.Head)
            {
                controller.Head.position = freezePos;
                controller.Head.rotation = freezeRot;
            }
            yield return null;
        }

        // keep roll locked after charge, but restore yaw/pitch speeds
        controller.SetSteeringSuppression(noSlither: true, noRoll: false);
        if (_yawField != null) _yawField.SetValue(controller, saveYaw);
        if (_pitchField != null) _pitchField.SetValue(controller, savePit);
    }

    private IEnumerator AimAtThenFire(NetworkObject target, MouthOrbProjectile proj)
    {
        var solver = controller.GetComponent<ChainSnakeSolver>();
        if (solver) solver.PushFollowTight(0.9f);   // hug the head during aim

        float saveYaw = _yawField != null ? (float)_yawField.GetValue(controller) : 0f;
        float savePit = _pitchField != null ? (float)_pitchField.GetValue(controller) : 0f;
        if (_yawField != null) _yawField.SetValue(controller, aimYawDeg);
        if (_pitchField != null) _pitchField.SetValue(controller, aimPitchDeg);

        controller.SetSpeedImmediate(aimMoveSpeed);
        controller.StopChase();

        Transform orbTf = proj ? proj.transform : null;

        float end = Time.time + Mathf.Max(0.05f, aimMaxTime);
        while (_active && Time.time < end)
        {
            if (target != null && target.IsSpawned)
            {
                controller.SetAttackOverride(target.transform.position, 0.25f, verticalY: false);

                // keep orb glued to mouth until we fire
                if (orbTf && mouth)
                {
                    orbTf.position = mouth.position;
                    orbTf.rotation = mouth.rotation;
                }

                var head = controller.Head;
                if (head)
                {
                    Vector3 to = (target.transform.position - head.position);
                    if (to.sqrMagnitude > 0.0001f)
                    {
                        float dot = Vector3.Dot(head.forward, to.normalized);
                        if (dot >= fireWhenDotAtLeast) break;
                    }
                }
            }
            yield return null;
        }
        controller.ClearAttackOverride();

        if (proj && proj.IsServer)
            proj.ServerLaunch(mouth.forward, orbSpeed, orbLife);

        controller.SetSpeedImmediate(0f);
        if (_yawField != null) _yawField.SetValue(controller, saveYaw);
        if (_pitchField != null) _pitchField.SetValue(controller, savePit);

        if (solver) solver.PopFollowTight();
    }

    private IEnumerator RecoilMoveWithKick(Vector3 fireDir)
    {
        if (!enableRecoil) yield break;

        var solver = controller.GetComponent<ChainSnakeSolver>();
        if (solver) solver.PushFollowTight(0.75f); // keep body together during recoil

        var head = controller.Head; if (!head) yield break;
        fireDir = fireDir.sqrMagnitude > 0.0001f ? fireDir.normalized : head.forward;

        Vector3 side = Vector3.Cross(Vector3.up, fireDir);
        if (side.sqrMagnitude < 1e-6f) side = Vector3.Cross(head.up, fireDir).normalized;
        else side.Normalize();

        float back = Random.Range(recoilBackRange.x, recoilBackRange.y);
        float lat = Random.Range(-recoilLateralMax, recoilLateralMax);
        Vector3 target = head.position - fireDir * back + side * lat + Vector3.up * recoilUpBias;

        controller.SetSpeedImmediate(recoilMoveSpeed);
        controller.SetAttackOverride(target, recoilTime, verticalY: true);

        yield return StartCoroutine(HeadKickOnce(fireDir));

        float until = Time.time + Mathf.Max(0.05f, recoilTime);
        while (_active && Time.time < until) yield return null;

        controller.ClearAttackOverride();
        controller.SetSpeedImmediate(0f);

        if (solver) solver.PopFollowTight();
    }

    private IEnumerator HeadKickOnce(Vector3 fireDir)
    {
        var head = controller.Head; if (!head) yield break;

        Quaternion start = head.rotation;
        Quaternion kick = Quaternion.FromToRotation(head.forward, Vector3.Slerp(head.forward, -fireDir, 0.6f)) *
                          Quaternion.AngleAxis(+recoilKickDeg * 0.6f, head.right);
        Quaternion peak = kick * start;

        float t = 0f, inT = Mathf.Max(0.01f, recoilKickIn);
        while (t < inT) { head.rotation = Quaternion.Slerp(start, peak, t / inT); t += Time.deltaTime; yield return null; }
        head.rotation = peak;

        t = 0f; float outT = Mathf.Max(0.01f, recoilKickOut);
        while (t < outT) { head.rotation = Quaternion.Slerp(peak, start, t / outT); t += Time.deltaTime; yield return null; }
        head.rotation = start;
    }

    // === Charge wag uses the exact idle solver path (no special vertical guard) ===
    private IEnumerator WagRampCoroutine(float chargeTime)
    {
        var solver = controller.GetComponent<ChainSnakeSolver>();
        if (!solver || !enableWag) yield break;

        //new 
        Debug.Log("new");
        // Ensure we get the straighten window + fresh phase each time we turn wiggle on for this attack
        solver.skipStraightenOnFirstEnable = false;       // do not skip
        solver.wiggleStraightenOnEnable = true;         // force straighten
        solver.randomizePhaseOnEnable = true;         // fresh phase
        solver.wiggleStraightenTime = Mathf.Max(0.15f, solver.wiggleStraightenTime); // safety

        // livelier tail while charging (temporary)
        float savedTail = solver.idleWiggleTailMultiplier;
        solver.idleWiggleTailMultiplier = wagTailMultiplierDuringCharge;

        float T = Mathf.Max(0.05f, chargeTime);
        float t = 0f;
        while (_active && t < T)
        {
            float k = t / T;
            float amp = Mathf.Lerp(wagBaseAmplitude, wagEndAmplitude, k); // bigger than idle
            float frq = Mathf.Lerp(wagBaseFrequency, wagEndFrequency, k);// faster than idle
            solver.SetIdleWiggle(true, amp, frq, solver.wiggleDampVertical);
            t += Time.deltaTime;
            yield return null;
        }
        solver.SetIdleWiggle(true, wagEndAmplitude, wagEndFrequency, solver.wiggleDampVertical);

        // restore tail scaling
        solver.idleWiggleTailMultiplier = savedTail;
    }

    private IEnumerator WagEaseOutCoroutine(float easeTime)
    {
        var solver = controller.GetComponent<ChainSnakeSolver>();
        if (!solver) yield break;

        float T = Mathf.Max(0.05f, easeTime);
        float t = 0f;
        float curAmp = wagEndAmplitude, curFreq = wagEndFrequency;

        while (_active && t < T)
        {
            float k = t / T;
            float amp = Mathf.Lerp(curAmp, 0f, k);
            float frq = Mathf.Lerp(curFreq, Mathf.Max(0.1f, wagBaseFrequency * 0.5f), k);
            solver.SetIdleWiggle(true, amp, frq, solver.wiggleDampVertical);
            t += Time.deltaTime;
            yield return null;
        }

        solver.SetIdleWiggle(false, 0f, 0f, solver.wiggleDampVertical);
    }

    // ---------- utils / teardown ----------

    private void Cleanup()
    {
        if (!_active) return;
        _active = false;

        controller.StopChase();
        controller.ClearAttackOverride();
        controller.SetSteeringSuppression(noSlither: false, noRoll: false); // unlock roll at end
        controller.SetHardUprightLock(false);
        SnakeUtilityAi.active = true;
        IsBusy = false;

        if (_mainCo != null) { controller.StopCoroutine(_mainCo); _mainCo = null; }
    }

    private static float TravelTimeout(Vector3 a, Vector3 b, float speed, float pad)
    {
        float d = Vector3.Distance(a, b); return (d / Mathf.Max(0.01f, speed)) + Mathf.Max(0f, pad);
    }
    private NetworkObject PickTarget(List<NetworkObject> players, ulong prev)
    {
        if (players == null || players.Count == 0) return null;
        if (players.Count == 1) return players[0];
        int i = Random.Range(0, players.Count);
        if (players[i].OwnerClientId == prev) i = (i + 1) % players.Count;
        return players[i];
    }
    private Transform PickFarthestWaypointFrom(Vector3 pos)
    {
        var pool = GetWaypointPool(); if (pool == null || pool.Count == 0) return null;
        Transform best = null; float bestSqr = -1f;
        for (int i = 0; i < pool.Count; i++)
        { var t = pool[i]; if (!t) continue; float sq = (t.position - pos).sqrMagnitude; if (sq > bestSqr) { bestSqr = sq; best = t; } }
        return best;
    }
    private List<Transform> GetWaypointPool()
    {
        if (!controller) return fallbackWaypoints;
        if (useControllerWaypoints)
        {
            var fi = typeof(SnakeBossController).GetField("waypoints", BindingFlags.Instance | BindingFlags.NonPublic);
            var list = fi != null ? (List<Transform>)fi.GetValue(controller) : null;
            if (list != null && list.Count > 0) return list;
        }
        return fallbackWaypoints;
    }
    private float GetIndexed(List<float> list, int i, float fallback)
    {
        if (list == null || list.Count == 0) return fallback;
        if (i < list.Count) return list[i];
        return list[list.Count - 1];
    }
    private void EnsurePitchUnlocked()
    {
        var pf = typeof(SnakeBossController).GetField("pitchTurnSpeedDeg", BindingFlags.Instance | BindingFlags.NonPublic);
        if (pf != null)
        {
            float p = (float)pf.GetValue(controller);
            if (p <= 0.0001f) pf.SetValue(controller, 55f);
        }
    }
}
