using System.Collections;
using Unity.Netcode;
using UnityEngine;

/// Snake charge action:
/// 1) Track a player at slow speed until facing or timeout.
/// 2) Lock a charge point via the laser muzzle ray.
/// 3) Pause (full stop), then charge to that point.
/// 4) If not last cycle, climb back to a set Y altitude (vertical arrival),
///    then repeat.
/// 5) Laser stays on the whole move; AI is suppressed for the whole move.
public class SnakeChargeAttackAction : AttackActionBase
{
    [Header("Bindings")]
    [SerializeField] private SnakeBossController controller;  // server-side boss controller
    [SerializeField] private Transform laserMuzzle;           // origin for laser ray / charge lock
    [SerializeField] private LineRenderer laserLine;          // optional; created on client first time

    [Header("Layers and Aiming")]
    [SerializeField] private LayerMask aimMask;               // ground/walls layers
    [SerializeField] private float aimRayMaxDistance = 200f;  // laser ray length
    [SerializeField] private Transform fallbackGroundY;       // used if raycast misses (plane Y)

    [Header("Laser mode")]
    [SerializeField] private bool laserAimAtTarget = false; // false = forward-only, true = aim at player target

    [Header("Timings")]
    [SerializeField] private float trackMinTime = 0.8f;       // must track at least this long
    [SerializeField] private float trackMaxTimeout = 2.0f;    // tracking hard cap
    [SerializeField] private float pauseDuration = 0.20f;     // full stop before charge
    [SerializeField] private int cycles = 2;                  // how many charges per use

    [Header("Speeds")]
    [SerializeField] private float slowTrackSpeed = 3.5f;     // speed while tracking
    [SerializeField] private float chargeSpeed = 40f;         // speed while charging
    [SerializeField] private float returnClimbSpeed = 10f;    // speed while returning to altitude

    [Header("Charge Timeouts")]
    [SerializeField] private float chargeTimeoutPad = 0.8f;   // extra time beyond distance/speed
    [SerializeField] private float returnTimeout = 3.0f;      // safety time when climbing back

    [Header("Return Altitude Between Cycles")]
    [SerializeField] private Transform returnAltitudeMarker;  // Y to hit before next cycle
    //[SerializeField] private float returnYArriveEpsilon = 0.15f; // how close in Y is "arrived"

    [Header("Facing gate before charge")]
    [SerializeField, Range(0.0f, 1.0f)] private float facingDotToCharge = 0.9f; // require facing

    [Header("Laser look")]
    [SerializeField] private Color laserColor = Color.red;
    [SerializeField] private float laserWidth = 0.08f;

    [Header("Scoring")]
    [SerializeField, Range(0f, 1f)] private float baseScore = 1f;

    [Header("Chaining")]
    [SerializeField] private bool chainRelocateOnFinish = true;

    [Header("Grounded Gate")]
    [SerializeField] private bool requireTargetGroundedToCharge = true;

    // 0 = wait indefinitely until grounded. Otherwise cap the wait.
    [SerializeField] private float waitGroundedMax = 0f;

    // Grounded probe (server-side) — if you don't trust CharacterController.isGrounded.
    // Usually set this to your Ground layers (can reuse aimMask if they're identical).
    [SerializeField] private LayerMask groundedMask;

    // Small probe to check if the target is on/near the ground.
    [SerializeField] private float groundedProbeRadius = 0.25f;
    [SerializeField] private float groundedProbeDistance = 0.4f;


    // runtime
    private NetworkObject targetNo;
    private static Material s_sharedLaserMat; // one shared unlit material per client

    // ===== Utility AI hooks =====

    public override bool CanExecute(BossContext ctx)
    {
        if (!base.CanExecute(ctx)) return false;
        if (ctx.Boss == null || !ctx.Boss.IsServer) return false;

        if (!controller) controller = ctx.Boss as SnakeBossController ?? controller;
        if (!controller) return false;

        var nm = NetworkManager.Singleton;
        if (nm == null || nm.ConnectedClients.Count == 0) return false;

        if (!returnAltitudeMarker) return false;
        if (!laserMuzzle) return false;

        return true;
    }

    public override float ReturnScore(BossContext ctx)
    {
        if (!CanExecute(ctx)) return 0f;
        return baseScore;
    }

    public override void ExecuteMove(BossContext ctx)
    {
        if (!CanExecute(ctx)) return;

        IsBusy = true;
        MarkUsed(ctx);

        // Lock AI for the duration so no other move can start.
        SnakeUtilityAi.active = false;

        targetNo = PickRandomTarget();
        if (!targetNo || !targetNo.IsSpawned)
        {
            CleanupAndFinish(true);
            return;
        }

        SetupLaserForAllClients();
        controller.StartCoroutine(RunRoutine());
    }

    // ===== Main sequence =====

    private IEnumerator RunRoutine()
    {
        if (!controller || !controller.Head)
        {
            CleanupAndFinish(false);
            yield break;
        }

        // Laser stays on for the move until we explicitly turn it off.
        SetLaserActiveAllClients(true, laserWidth, laserColor, targetNo.NetworkObjectId, laserAimAtTarget);

        for (int i = 0; i < Mathf.Max(1, cycles); i++)
        {
            bool lastCycle = (i == cycles - 1);

            controller.ClearAttackOverride();
            controller.SetSteeringSuppression(noSlither: false, noRoll: false);

            // ---- 1) TRACK ----
            controller.SetSpeedImmediate(slowTrackSpeed);
            controller.BeginChase(targetNo, trackMaxTimeout + 0.6f);

            float elapsed = 0f;
            while (elapsed < trackMaxTimeout)
            {
                if (!this || !controller || !controller.Head) { CleanupAndFinish(false); yield break; }
                if (!targetNo || !targetNo.IsSpawned) break;

                Vector3 to = targetNo.transform.position - controller.Head.position;
                if (to.sqrMagnitude > 0.0001f)
                {
                    to.Normalize();
                    float dot = Vector3.Dot(controller.Head.forward, to);
                    if (elapsed >= trackMinTime && dot >= facingDotToCharge)
                        break;
                }

                elapsed += Time.deltaTime;
                yield return null;
            }
            // ---- 1.5) WAIT FOR TARGET TO BE GROUNDED (pre-charge) ----
            if (requireTargetGroundedToCharge)
            {
                float waited = 0f;
                // Keep softly chasing while we wait (so head stays aligned).
                // We’ll re-arm the chase window in small bites.
                while (true)
                {
                    if (!this || !controller || !controller.Head) { CleanupAndFinish(false); yield break; }
                    if (!targetNo || !targetNo.IsSpawned) break;

                    bool grounded = IsTargetGrounded(targetNo.transform);
                    if (grounded) break;

                    // optional cap
                    if (waitGroundedMax > 0f && waited >= waitGroundedMax)
                        break; // If you want to *never* charge when airborne, set waitGroundedMax=0 (infinite)

                    // keep a tiny forward crawl & re-assert a short chase so we keep facing cleanly
                    controller.SetSpeedImmediate(slowTrackSpeed);
                    controller.BeginChase(targetNo, 0.2f); // extend facing a bit

                    waited += Time.deltaTime;
                    yield return null;
                }
            }

            // ---- 2) LOCK CHARGE POINT ----
            //Vector3 chargePoint = ComputeLockPoint();
            Vector3 chargePoint = requireTargetGroundedToCharge ? ComputeGroundLockNearTarget(): ComputeLockPoint();
            controller.StopChase();
            controller.SetSteeringSuppression(noSlither: true, noRoll: true);

            float dist = Vector3.Distance(controller.Head.position, chargePoint);
            float chargeWindow = (dist / Mathf.Max(0.01f, chargeSpeed)) + chargeTimeoutPad;

            controller.SetAttackOverride(chargePoint, pauseDuration + chargeWindow + 0.3f);

            // ---- 3) PAUSE ----
            controller.SetSpeedImmediate(0f);
            yield return new WaitForSeconds(pauseDuration);

            // ---- 4) CHARGE ----
            controller.SetSpeedImmediate(chargeSpeed);

            float chargeDeadline = Time.time + chargeWindow + 0.3f;
            //bool groundHit = false;

            while (controller.AttackOverrideActive && Time.time < chargeDeadline)
            {
                if (!this || !controller || !controller.Head) { CleanupAndFinish(false); yield break; }

                if (controller.Head.position.y <= controller.GroundMinY + 0.02f)
                {
                    controller.ClearAttackOverride();
                    //groundHit = true;

                    if (lastCycle)
                    {
                        // LAST CYCLE: stop instantly and turn off laser right away
                        controller.SetSpeedImmediate(0f);
                        LaserOffAllClients();
                    }
                    else
                    {
                        // mid cycles: recover with slow speed
                        controller.SetSpeedImmediate(slowTrackSpeed);
                    }
                    break;
                }

                yield return null;
            }

            // ---- 5) RETURN TO ALTITUDE ----
            float targetY = returnAltitudeMarker.position.y;
            if (!lastCycle)
            {
                // Mid-cycle climb (unchanged)
                controller.SetSteeringSuppression(noSlither: false, noRoll: false);

                Vector3 climbTo = new Vector3(controller.Head.position.x, targetY, controller.Head.position.z);
                controller.SetSpeedImmediate(returnClimbSpeed);
                controller.SetAttackOverride(climbTo, returnTimeout, verticalY: true);

                bool reachedY = false;
                float retDeadline = Time.time + returnTimeout + 0.25f;

                while (Time.time < retDeadline)
                {
                    if (!this || !controller || !controller.Head) { CleanupAndFinish(false); yield break; }

                    if (controller.Head.position.y >= targetY)
                    {
                        reachedY = true;
                        controller.ClearAttackOverride();
                        break;
                    }

                    if (!controller.AttackOverrideActive)
                        controller.SetAttackOverride(climbTo, 0.5f, verticalY: true);

                    yield return null;
                }

                if (!reachedY)
                {
                    CleanupAndFinish(false);
                    yield break;
                }
            }
            else
            {
                // ===== LAST CYCLE: climb back to Y, then FACE+IDLE (no relocate) =====

                // Ensure laser is off 
                LaserOffAllClients();

                controller.SetSteeringSuppression(noSlither: false, noRoll: false);

                Vector3 climbTo = new Vector3(controller.Head.position.x, targetY, controller.Head.position.z);
                // If we hit ground we already zeroed speed; now jump to climb speed for a crisp recovery
                controller.SetSpeedImmediate(returnClimbSpeed);
                controller.SetAttackOverride(climbTo, returnTimeout, verticalY: true);
                float retDeadline = Time.time + returnTimeout + 0.25f;
                while (Time.time < retDeadline)
                {
                    if (!this || !controller || !controller.Head) { CleanupAndFinish(false); yield break; }

                    if (controller.Head.position.y >= targetY)
                    {
                        controller.ClearAttackOverride();
                        break;
                    }

                    if (!controller.AttackOverrideActive)
                        controller.SetAttackOverride(climbTo, 0.5f, verticalY: true);

                    yield return null;
                }

                // Hand off directly to the controller's face to straighten to idle sequence
                controller.BeginFaceThenIdle();

                // Finish this action cleanly (no relocate)
                SnakeUtilityAi.active = true; // re-arm AI; it will only think after idle ends
                IsBusy = false;
                yield break;
            }
        }

        // Safety fallback (normally last cycle returns earlier)
        CleanupAndFinish(false);
    }



    // ===== Helpers =====
    private Vector3 ComputeGroundLockNearTarget()
    {
        var t = targetNo ? targetNo.transform : null;
        if (!t) return ComputeLockPoint(); // fallback

        Vector3 xz = new Vector3(t.position.x, t.position.y + 2f, t.position.z);
        var mask = aimMask;
        if (Physics.Raycast(xz, Vector3.down, out var hit, aimRayMaxDistance, mask, QueryTriggerInteraction.Ignore))
            return hit.point;

        // fallback plane
        if (fallbackGroundY)
            return new Vector3(xz.x, fallbackGroundY.position.y, xz.z);

        return ComputeLockPoint();
    }
    // Ray from muzzle forward to ground/wall; fallback to a plane if no hit.
    private Vector3 ComputeLockPoint()
    {
        Vector3 origin = laserMuzzle ? laserMuzzle.position : controller.Head.position;
        Vector3 dir = laserMuzzle ? laserMuzzle.forward : controller.Head.forward;

        if (Physics.Raycast(origin, dir, out var hit, aimRayMaxDistance, aimMask, QueryTriggerInteraction.Ignore))
            return hit.point;

        if (fallbackGroundY)
        {
            float y = fallbackGroundY.position.y;

            // If ray is near-parallel to the plane, just project forward.
            if (Mathf.Abs(dir.y) < 0.0001f)
                return new Vector3(origin.x + dir.x * 20f, y, origin.z + dir.z * 20f);

            // Exact intersection with the Y plane.
            float t = (y - origin.y) / dir.y;
            if (t > 0f) return origin + dir * t;
        }

        // Ultimate fallback: forward point.
        return origin + dir.normalized * 20f;
    }

    private NetworkObject PickRandomTarget()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null) return null;
        var list = nm.ConnectedClientsList;
        if (list == null || list.Count == 0) return null;
        return list[Random.Range(0, list.Count)].PlayerObject;
    }

    // ===== Laser setup and RPCs =====

    private void SetupLaserForAllClients() => SetupLaserClientRpc();

    private void SetLaserActiveAllClients(bool active, float width, Color color, ulong targetId, bool aimAtTarget)
    {
        SetLaserActiveClientRpc(active, width, color, targetId, aimAtTarget);
    }

    // keep off helper
    private void LaserOffAllClients()
    {
        SetLaserActiveClientRpc(false, 0f, Color.clear, 0, laserAimAtTarget);
    }

    [ClientRpc]
    private void SetupLaserClientRpc()
    {
        if (!laserMuzzle) return;

        if (!laserLine)
            laserLine = laserMuzzle.GetComponentInChildren<LineRenderer>(true);

        if (!laserLine)
        {
            var go = new GameObject("LaserLine");
            go.transform.SetParent(laserMuzzle, false);
            laserLine = go.AddComponent<LineRenderer>();
            laserLine.positionCount = 2;
            laserLine.useWorldSpace = true;
            laserLine.textureMode = LineTextureMode.Stretch;
            laserLine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            laserLine.receiveShadows = false;

            // Give it its own material instance so we can set color reliably
            var baseShader = Shader.Find("Unlit/Color"); // simple unlit, respects material _Color
            if (baseShader == null)
                baseShader = Shader.Find("Sprites/Default"); // fallback that also works with color
            laserLine.material = new Material(baseShader);
        }

        var lp = laserLine.GetComponent<TargetLaserPointer>();
        if (!lp) lp = laserLine.gameObject.AddComponent<TargetLaserPointer>();
        lp.Bind(laserMuzzle);
        laserLine.enabled = false;
    }

    [ClientRpc]
    private void SetLaserActiveClientRpc(bool active, float width, Color color, ulong targetNetworkId, bool aimAtTarget)
    {
        if (!laserLine) return;
        var lp = laserLine.GetComponent<TargetLaserPointer>();
        if (!lp) return;

        if (active)
        {
            // Widths
            laserLine.startWidth = width;
            laserLine.endWidth = width;

            // Set start/end colors (works if shader reads vertex colors)...
            laserLine.startColor = color;
            laserLine.endColor = color;

            // ...but force material color too so it always shows the intended tint
            if (laserLine.material != null)
                laserLine.material.color = color;

            // Set mode and optional target
            lp.SetMode(aimAtTarget);

            Transform targetTf = null;
            var nm = NetworkManager.Singleton;
            if (nm && nm.SpawnManager != null &&
                nm.SpawnManager.SpawnedObjects.TryGetValue(targetNetworkId, out var no) &&
                no && no.IsSpawned)
            {
                targetTf = no.transform;
            }
            lp.SetTarget(targetTf, aimMask, aimRayMaxDistance);

            laserLine.enabled = true;
        }
        else
        {
            laserLine.enabled = false;
            lp.SetTarget(null, 0, 0f);
        }
    }

    // Client-side: draws a ray from muzzle to target each frame (or straight ahead if no target).
    private class TargetLaserPointer : MonoBehaviour
    {
        private Transform muzzle;
        private Transform target;
        private LayerMask mask;
        private float maxDist;
        private LineRenderer lr;

        // mode toggle: true = aim at target, false = forward from muzzle
        private bool aimAtTarget = false;

        public void Bind(Transform muzzle)
        {
            this.muzzle = muzzle;
            lr = GetComponent<LineRenderer>();
        }

        public void SetMode(bool aimAtTarget)
        {
            this.aimAtTarget = aimAtTarget;
        }

        public void SetTarget(Transform target, LayerMask mask, float maxDist)
        {
            this.target = target;
            this.mask = mask;
            this.maxDist = maxDist;
        }

        private void LateUpdate()
        {
            if (!lr || !lr.enabled || muzzle == null) return;

            Vector3 start = muzzle.position;
            Vector3 dir;

            if (aimAtTarget && target != null)
            {
                dir = (target.position - start);
                if (dir.sqrMagnitude < 0.0001f) dir = muzzle.forward;
            }
            else
            {
                dir = muzzle.forward; // forward-only mode
            }
            dir.Normalize();

            Vector3 end = start + dir * maxDist;
            if (Physics.Raycast(start, dir, out var hit, maxDist, mask, QueryTriggerInteraction.Ignore))
                end = hit.point;

            lr.SetPosition(0, start);
            lr.SetPosition(1, end);
        }
    }
    private bool IsTargetGrounded(Transform target)
    {
        if (!target) return false;

        // Try CharacterController first if present (cheap).
        //var cc = target.GetComponent<CharacterController>();
        //if (cc != null) return cc.isGrounded;

        // Fallback: spherecast down a short distance against ground layers.
        Vector3 start = target.position + Vector3.up * 0.05f;
        float dist = Mathf.Max(0.05f, groundedProbeDistance);
        float r = Mathf.Max(0.01f, groundedProbeRadius);
        var mask = groundedMask.value != 0 ? groundedMask : aimMask; // default to aimMask if not set

        return Physics.SphereCast(start, r, Vector3.down, out _, dist, mask, QueryTriggerInteraction.Ignore);
    }


    // ===== Common cleanup =====

    // smoother default cleanup (no speed snap)
    private void CleanupAndFinish(bool chainRelocate)
    {
        LaserOffAllClients();

        if (controller)
        {
            controller.ClearAttackOverride();
            controller.SetSteeringSuppression(noSlither: false, noRoll: false);

            // Smoothly blend back to cruise instead of snapping
            controller.ResetSpeedTarget();
        }

        SnakeUtilityAi.active = true;   // re-enable AI
        IsBusy = false;

        if (chainRelocate) TriggerRelocate();
    }

    //goes back to idle
    private void TriggerRelocate()
    {
        if (!chainRelocateOnFinish || !controller) return;

        var relocate = GetComponent<SnakeRelocateAction>();
        var ctx = controller.BuildContext();

        if (relocate && relocate.CanExecute(ctx))
            relocate.ExecuteMove(ctx);// Use the action so it manages IsBusy/cooldown/idle sequence for you.
        else
            controller.StartCoroutine(controller.CoRelocateToWaypointAndIdle());
    }
}
