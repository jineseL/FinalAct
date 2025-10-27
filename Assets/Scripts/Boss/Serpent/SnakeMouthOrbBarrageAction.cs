using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Unity.Netcode;
using UnityEngine;
using System;

/// Fast perch + vertical pop + tracked mega-orb volley.
/// - Chooses the farthest controller waypoint from the first picked target.
/// - Flies there, ascends vertically, locks movement, tracks target while charging.
/// - Optional tail/body wag (ramps up with charge), optional head recoil on fire.
/// - Repeats N volleys (new target each time, stays on the same perch).
/// - Finishes by relocating to a waypoint and entering idle.
/// - AI stays paused for the whole sequence.
public class SnakeBigOrbVolleyAction : AttackActionBase
{
    private static readonly Type[] WiggleSig3 = { typeof(bool), typeof(float), typeof(float) };
    private static readonly Type[] WiggleSig4 = { typeof(bool), typeof(float), typeof(float), typeof(bool) };

    [Header("Bindings")]
    [SerializeField] private SnakeBossController controller;
    [SerializeField] private Transform mouth;               // On the head

    [Header("Waypoints / Travel")]
    [SerializeField] private float arriveRadius = 2.5f;     // radial arrival for the perch
    [SerializeField] private float travelSpeed = 12f;       // forward speed to the perch
    [SerializeField] private float travelTimeoutPad = 0.8f; // extra budget
    [SerializeField] private float ascendHeight = 8f;       // how high above perch.y to pop
    [SerializeField] private float ascendSpeed = 10f;
    [SerializeField] private float ascendTimeout = 2.5f;

    [Tooltip("Use controller's internal waypoint list; otherwise use Fallback Waypoints.")]
    [SerializeField] private bool useControllerWaypoints = true;
    [SerializeField] private List<Transform> fallbackWaypoints = new();

    [Header("Volley (multi-shot)")]
    [SerializeField] private int volleys = 3;

    [Tooltip("Per-volley charge times (seconds). If empty or shorter than volleys, missing entries use last value or 1.0.")]
    [SerializeField] private List<float> chargeTimes = new() { 1.0f };

    [Tooltip("Per-volley orb start scales. If empty/short, missing use last or 0.2.")]
    [SerializeField] private List<float> orbStartScales = new() { 0.2f };

    [Tooltip("Per-volley orb end scales. If empty/short, missing use last or 1.0.")]
    [SerializeField] private List<float> orbEndScales = new() { 1.0f };

    [Header("Orb Projectile")]
    [SerializeField] private NetworkObject orbPrefab;
    [SerializeField] private float orbSpeed = 36f;
    [SerializeField] private float orbLife = 6f;

    [Header("Look/Track while Charging")]
    [SerializeField, Range(0.85f, 1f)] private float faceDot = 0.97f; // optional early gate (unused by default; we always track)
    [SerializeField] private float lookReassertPeriod = 0.18f;        // how often we refresh the AttackOverride to keep tracking
    [SerializeField] private float stationarySpeed = 0f;              // keep 0 to prevent forward drift

    [Header("Body Wag During Charge")]
    [SerializeField] private bool enableWag = true;
    [Tooltip("Base wag amplitude while charge starts (interpreted by ChainSnakeSolver if supported).")]
    [SerializeField] private float wagBaseAmplitude = 1.2f;
    [Tooltip("Base wag frequency (Hz).")]
    [SerializeField] private float wagBaseFrequency = 0.9f;
    [Tooltip("End wag amplitude at release (stronger wiggle).")]
    [SerializeField] private float wagEndAmplitude = 2.4f;
    [Tooltip("End wag frequency at release.")]
    [SerializeField] private float wagEndFrequency = 1.6f;
    [Tooltip("If true, we try to damp vertical wag (best-effort; depends on solver API).")]
    [SerializeField] private bool dampVerticalWag = true;
    [Header("Charge Wag Vertical Guard")]
    [SerializeField, Range(0f, 0.2f)] private float chargePerLinkRiseFrac = 0.02f; // ~2% of link length
    [SerializeField, Range(0f, 1f)] private float chargeUpwardYScale = 0.15f;  // tiny vertical blend

    [Header("Head Recoil on Fire")]
    [SerializeField] private bool enableRecoil = true;
    [SerializeField] private float recoilPitchUpDeg = 10f;    // nose-up kick
    [SerializeField] private float recoilYawBackDeg = 4f;     // tiny yaw opposite to player (visual flavor)
    [SerializeField] private float recoilInTime = 0.08f;
    [SerializeField] private float recoilOutTime = 0.18f;

    [Header("Handoff / AI")]
    [SerializeField] private bool relocateAndIdleAtEnd = true;

    [Header("Scoring")]
    [SerializeField, Range(0f, 1f)] private float baseScore = 1f;

    // --- internals ---
    private Coroutine _mainCo;
    private bool _active;
    private ulong _lastTargetId = ulong.MaxValue;
    private FieldInfo _pitchField;

    public override bool CanExecute(BossContext ctx)
    {
        if (!base.CanExecute(ctx)) return false;
        if (ctx.Boss == null || !ctx.Boss.IsServer) return false;
        if (!controller || !mouth) return false;
        if (!orbPrefab) return false;

        var pool = GetWaypointPool();
        if (pool == null || pool.Count == 0) return false;

        // Must have at least one player
        if (ctx.Players == null || ctx.Players.Count == 0) return false;
        return true;
    }

    public override float ReturnScore(BossContext ctx) => CanExecute(ctx) ? baseScore : 0f;

    public override void ExecuteMove(BossContext ctx)
    {
        if (!CanExecute(ctx)) return;

        IsBusy = true;
        MarkUsed(ctx);

        // Build a clean target list now (NetworkObjects)
        var targets = new List<NetworkObject>();
        foreach (var go in ctx.Players)
        {
            if (!go) continue;
            var no = go.GetComponent<NetworkObject>();
            if (no && no.IsSpawned) targets.Add(no);
        }
        if (targets.Count == 0) { IsBusy = false; return; }

        _active = true;
        SnakeUtilityAi.active = false;   // pause AI thinking for the whole sequence

        if (_mainCo != null) controller.StopCoroutine(_mainCo);
        _mainCo = controller.StartCoroutine(MainRoutine(targets));
    }

    private IEnumerator MainRoutine(List<NetworkObject> players)
    {
        // pick initial target
        var target = PickTarget(players, prev: _lastTargetId);
        _lastTargetId = target ? target.OwnerClientId : ulong.MaxValue;

        // 1) pick FARthest waypoint from target
        var perch = PickFarthestWaypointFrom(target ? target.transform.position : controller.Head.position);
        if (!perch)
        {
            Cleanup(); yield break;
        }

        // 2) fly to perch (radial arrival)
        controller.StopChase();
        controller.ClearAttackOverride();
        controller.SetSteeringSuppression(noSlither: false, noRoll: false);
        controller.SetSpeedImmediate(Mathf.Max(0f, travelSpeed));

        float travelBudget = TravelTimeout(controller.Head.position, perch.position, travelSpeed, travelTimeoutPad);
        float travelDeadline = Time.time + travelBudget + 0.25f;

        // hard-pin override toward perch every frame until within arriveRadius
        while (_active && Time.time < travelDeadline)
        {
            Vector3 targetPos = perch.position;
            controller.SetAttackOverride(targetPos, 0.25f, verticalY: false);

            if ((controller.Head.position - targetPos).sqrMagnitude <= arriveRadius * arriveRadius)
            {
                controller.ClearAttackOverride();
                break;
            }
            yield return null;
        }
        controller.ClearAttackOverride();

        // 3) ascend vertically by ascendHeight
        float targetY = perch.position.y + Mathf.Max(0f, ascendHeight);
        float eps = Mathf.Max(0.02f, controller.VerticalArriveEpsilon);
        float ascendDeadline = Time.time + Mathf.Max(0.1f, ascendTimeout) + 0.25f;

        controller.SetSpeedImmediate(Mathf.Max(0f, ascendSpeed));
        while (_active && Time.time < ascendDeadline)
        {
            Vector3 climbTo = new Vector3(controller.Head.position.x, targetY, controller.Head.position.z);
            controller.SetAttackOverride(climbTo, 0.25f, verticalY: true);

            float y = controller.Head.position.y;
            if (Mathf.Abs(y - targetY) <= eps)
            {
                controller.ClearAttackOverride();
                break;
            }
            yield return null;
        }
        controller.ClearAttackOverride();

        // lock movement; rotate-only tracking
        controller.SetSpeedImmediate(Mathf.Max(0f, stationarySpeed));
        controller.SetSteeringSuppression(noSlither: true, noRoll: false);

        // Ensure pitch is unlocked (in case a previous action locked it)
        ForcePitchUnlockedAndPositive();

        // 4) volley loop (stay on perch, new target each time)
        for (int v = 0; v < Mathf.Max(1, volleys) && _active; v++)
        {
            // new target each volley (random)
            target = PickTarget(players, prev: _lastTargetId);
            _lastTargetId = target ? target.OwnerClientId : _lastTargetId;

            // spin a tiny tracker that keeps re-asserting look at the target
            float chargeT = GetIndexed(chargeTimes, v, 1.0f);
            float startS = GetIndexed(orbStartScales, v, 0.2f);
            float endS = GetIndexed(orbEndScales, v, 1.0f);

            // start wag ramp (best effort)
            Coroutine wagCo = null;
            if (enableWag) wagCo = controller.StartCoroutine(WagRampCoroutine(chargeT));

            // begin look track (reassert small-ttl AttackOverride to target each frame slice)
            Coroutine lookCo = controller.StartCoroutine(LookTrackCoroutine(target, chargeT));

            // spawn/charge orb
            var no = UnityEngine.Object.Instantiate(orbPrefab, mouth.position, mouth.rotation);
            no.Spawn(true);
            NetworkSfxRelay.All_Play2D("ChargeUp");

            var proj = no.GetComponent<MouthOrbProjectile>();
            if (proj) proj.ServerStartCharge(mouth, chargeT, startS, endS);

            // wait charge
            yield return new WaitForSeconds(chargeT);

            // fire
            if (proj && proj.IsServer)
            {
                proj.ServerLaunch(mouth.forward, orbSpeed, orbLife);
            }

            // head recoil (purely visual; NetworkTransform will replicate)
            if (enableRecoil) yield return controller.StartCoroutine(HeadRecoilOnce());

            // stop look coroutine from the charge phase; we’ll restart fresh next volley
            if (lookCo != null) controller.StopCoroutine(lookCo);

            // ease wag back to base (or zero) quickly (best effort)
            if (enableWag && wagCo != null) controller.StopCoroutine(wagCo);
            if (enableWag) yield return controller.StartCoroutine(WagEaseOutCoroutine(0.18f));
        }

        // 5) finish: relocate & idle, then resume AI
        controller.StopChase();
        controller.ClearAttackOverride();
        controller.SetSteeringSuppression(noSlither: false, noRoll: false);

        if (relocateAndIdleAtEnd)
        {
            // Use controller's simple relocate+face+idle path
            yield return controller.StartCoroutine(controller.CoRelocateToWaypointAndIdle());
        }
        else
        {
            controller.BeginFaceThenIdleImmediate();
        }

        Cleanup();
    }

    // ---------- Helpers ----------

    private void Cleanup()
    {
        if (!_active) return;
        _active = false;

        controller.StopChase();
        controller.ClearAttackOverride();
        controller.SetSteeringSuppression(noSlither: false, noRoll: false);
        SnakeUtilityAi.active = true;
        IsBusy = false;

        if (_mainCo != null) { controller.StopCoroutine(_mainCo); _mainCo = null; }
    }

    private static float TravelTimeout(Vector3 a, Vector3 b, float speed, float pad)
    {
        float d = Vector3.Distance(a, b);
        return (d / Mathf.Max(0.01f, speed)) + Mathf.Max(0f, pad);
    }

    private NetworkObject PickTarget(List<NetworkObject> players, ulong prev)
    {
        if (players == null || players.Count == 0) return null;
        if (players.Count == 1) return players[0];

        // random but try to avoid repeating the same owner twice
        int idx = UnityEngine.Random.Range(0, players.Count);
        if (players[idx].OwnerClientId == prev) idx = (idx + 1) % players.Count;
        return players[idx];
    }

    private Transform PickFarthestWaypointFrom(Vector3 pos)
    {
        var pool = GetWaypointPool();
        if (pool == null || pool.Count == 0) return null;

        Transform best = null;
        float bestSqr = -1f;
        for (int i = 0; i < pool.Count; i++)
        {
            var t = pool[i];
            if (!t) continue;
            float sq = (t.position - pos).sqrMagnitude;
            if (sq > bestSqr) { bestSqr = sq; best = t; }
        }
        return best;
    }

    private List<Transform> GetWaypointPool()
    {
        if (!controller) return fallbackWaypoints;

        if (useControllerWaypoints)
        {
            var fi = typeof(SnakeBossController).GetField("waypoints",
                BindingFlags.Instance | BindingFlags.NonPublic);
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

    private void ForcePitchUnlockedAndPositive()
    {
        if (_pitchField == null)
            _pitchField = typeof(SnakeBossController).GetField("pitchTurnSpeedDeg", BindingFlags.Instance | BindingFlags.NonPublic);
        if (_pitchField != null)
        {
            float p = (float)_pitchField.GetValue(controller);
            if (p <= 0.0001f) _pitchField.SetValue(controller, 55f); // safe default
        }
    }

    // Keeps re-asserting a short AttackOverride at the target so we rotate in place without chase boost.
    private IEnumerator LookTrackCoroutine(NetworkObject target, float duration)
    {
        float until = Time.time + Mathf.Max(0.01f, duration);
        float next = 0f;

        // zero speed so the controller won't drift forward
        controller.SetSpeedImmediate(stationarySpeed);
        controller.StopChase();

        while (_active && Time.time < until)
        {
            if (target != null && target.IsSpawned)
            {
                // Refresh frequently; short TTL prevents sticky overrides
                if (Time.time >= next)
                {
                    controller.SetAttackOverride(target.transform.position, Mathf.Max(0.05f, lookReassertPeriod), verticalY: false);
                    next = Time.time + lookReassertPeriod * 0.5f;
                }
            }
            yield return null;
        }
        controller.ClearAttackOverride();
    }

    // Head recoil: small pitch-up + slight yaw. Quick in, quick out.
    private IEnumerator HeadRecoilOnce()
    {
        var head = controller.Head;
        if (!head) yield break;

        float t = 0f;
        Quaternion start = head.rotation;
        Quaternion kick = Quaternion.AngleAxis(recoilPitchUpDeg, head.right) * Quaternion.AngleAxis(-recoilYawBackDeg, head.up);
        Quaternion peak = kick * start;

        // in
        float inT = Mathf.Max(0.01f, recoilInTime);
        while (t < inT)
        {
            float k = t / inT;
            head.rotation = Quaternion.Slerp(start, peak, k);
            t += Time.deltaTime;
            yield return null;
        }
        head.rotation = peak;

        // out
        t = 0f;
        float outT = Mathf.Max(0.01f, recoilOutTime);
        while (t < outT)
        {
            float k = t / outT;
            head.rotation = Quaternion.Slerp(peak, start, k);
            t += Time.deltaTime;
            yield return null;
        }
        head.rotation = start;
    }

    // Best-effort wag ramp: if ChainSnakeSolver exposes SetIdleWiggle/EnableIdleWiggle we drive it.
    private IEnumerator WagRampCoroutine(float chargeTime)
    {
        if (!enableWag) yield break;

        // begin the vertical guard for this charge window
        controller.BeginChargeWagClamp(chargePerLinkRiseFrac, chargeUpwardYScale);

        float T = Mathf.Max(0.05f, chargeTime);
        float t = 0f;
        while (_active && t < T)
        {
            float k = t / T;
            float amp = Mathf.Lerp(wagBaseAmplitude, wagEndAmplitude, k);
            float freq = Mathf.Lerp(wagBaseFrequency, wagEndFrequency, k);
            controller.SetIdleWiggle(true, amp, freq, /*dampVertical:*/ false); // vertical is handled by the clamp
            t += Time.deltaTime;
            yield return null;
        }
        controller.SetIdleWiggle(true, wagEndAmplitude, wagEndFrequency, false);

    }

    private IEnumerator WagEaseOutCoroutine(float easeTime)
    {
        float T = Mathf.Max(0.05f, easeTime);
        float t = 0f;
        float curAmp = wagEndAmplitude, curFreq = wagEndFrequency;

        while (_active && t < T)
        {
            float k = t / T;
            float amp = Mathf.Lerp(curAmp, 0f, k);
            float freq = Mathf.Lerp(curFreq, Mathf.Max(0.1f, wagBaseFrequency * 0.5f), k);
            controller.SetIdleWiggle(true, amp, freq, false);
            t += Time.deltaTime;
            yield return null;
        }

        controller.SetIdleWiggle(false, 0f, 0f, false);

        // turn off the vertical guard now that charging for this volley ended
        controller.EndChargeWagClamp();
    }

    private Component GetSolver()
    {
        // ChainSnakeSolver is serialized on the controller.
        var fi = typeof(SnakeBossController).GetField("chainSolver",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        return fi != null ? (Component)fi.GetValue(controller) : null;
    }

   
}
