using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// Spawns turrets by "yeeting" them from the snake's mouth upward,
/// in volleys of up to 2. Each turret travels along a smooth arc to a
/// designated landing Transform, spins while flying, then enters Turret state.
/// After the final volley the boss briefly holds, faces a player, and idles.
public class SnakeDeployTurretsAction : AttackActionBase
{
    [Header("Bindings")]
    [SerializeField] private SnakeBossController controller;    // server-only boss controller
    [SerializeField] private SnakeUtilityAi ai;                 // optional; if null, will GetComponent
    [SerializeField] private Transform mouth;                   // spawn origin
    [SerializeField] private NetworkObject turretPrefab;        // prefab with NetworkObject + NetworkTransform + DeployedTurret

    [Header("Landing Slots (unique pick)")]
    [SerializeField] private List<Transform> landingSlots = new();

    [Tooltip("If true, each landing slot can be used only once per encounter.")]
    [SerializeField] private bool neverReuseSlots = true;

    // runtime: which slots have been consumed already (per encounter)
    private readonly HashSet<int> _usedSlotIndices = new();

    // Call this at encounter start if you want to reset usage.
    public void ResetSlotUsage() => _usedSlotIndices.Clear();


    [Header("Prefab Orientation Fix")]
    [SerializeField] private Vector3 spawnRotationFixEuler = Vector3.zero;
    // If your prefab's +Z is up, set this to (-90, 0, 0) so +Y becomes up.

    [Header("Turret Count")]
    [Tooltip("How many turrets to spawn per player (total = players * this, clamped to slots).")]
    [Min(1)] public int turretsPerPlayer = 2;
    [Tooltip("Max turrets fired per volley.")]
    [Min(1)] public int maxPerVolley = 2;

    [Header("Face Up / Charge Up")]
    [Tooltip("Seconds to rotate to global up before the first volley.")]
    [Min(0f)] public float lookUpTime = 0.6f;
    [Tooltip("Forward creep while looking up, to avoid freeze-looking. 0 = rotate only.")]
    [Min(0f)] public float lookUpForwardSpeed = 0.5f;

    [Tooltip("Seconds of charge-up VFX before each volley (also between volleys).")]
    [Min(0f)] public float chargeUpTime = 0.65f;

    [Tooltip("After last volley, keep looking up for this long before facing a player.")]
    [Min(0f)] public float postFireHoldTime = 0.35f;

    [Header("Arc Flight")]
    [Tooltip("Extra height above the midpoint of start->target for the arc (world units).")]
    public float arcHeight = 6f;
    [Tooltip("Normalized travel progress rate scales by this start speed (units/sec along normalized path). Larger = faster start.")]
    public float startSpeed = 1.6f;
    [Tooltip("Normalized travel progress rate scales by this end speed (smaller than startSpeed to slow down near landing).")]
    public float endSpeed = 0.35f;
    [Tooltip("Spin about world Y while traveling, in deg/sec.")]
    public float spinYDegPerSec = 180f;
    [Tooltip("How close to landing slot to switch to Turret state.")]
    public float arriveEpsilon = 0.25f;

    [Header("Scoring")]
    [Range(0f, 1f)] public float baseScore = 1f;


    public override bool CanExecute(BossContext ctx)
    {
        if (!base.CanExecute(ctx)) return false;
        if (ctx.Boss == null || !ctx.Boss.IsServer) return false;

        if (!controller) controller = ctx.Boss as SnakeBossController ?? controller;
        if (!controller || !controller.Head) return false;
        if (!mouth || !turretPrefab) return false;

        int numPlayers = Mathf.Max(0, ctx.Players?.Count ?? 0);
        if (numPlayers == 0) return false; // nothing to do

        if (landingSlots == null || landingSlots.Count == 0) return false;
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

        if (!ai) ai = GetComponent<SnakeUtilityAi>();
        IsBusy = true;
        MarkUsed(ctx);

        // Pause the AI so nothing else starts until we finish.
        if (ai) SnakeUtilityAi.active = false; else SnakeUtilityAi.active = false;

        controller.StartCoroutine(RunRoutine(ctx));
    }

    private IEnumerator RunRoutine(BossContext ctx)
    {
        // ----- Decide how many + which slots (unique across the whole fight) -----
        int numPlayers = Mathf.Max(1, ctx.Players?.Count ?? 1);
        int requested = Mathf.Max(1, turretsPerPlayer) * numPlayers;

        List<int> availableIdx = GetAvailableSlotIndices();
        int availableCount = availableIdx.Count;

        // Clamp to what we actually have left
        int totalToSpawn = Mathf.Clamp(requested, 0, availableCount);

        if (totalToSpawn <= 0)
        {
            // Nothing left to place to just face+idle and finish.
            controller.BeginFaceThenIdle();
            CleanupAndFinish();
            yield break;
        }

        // Shuffle the available list and take the first N
        Shuffle(availableIdx);
        List<int> chosenIdx = availableIdx.GetRange(0, totalToSpawn);

        // ----- 1) Face UP (rotate-only or with tiny creep) -----
        controller.ClearAttackOverride();
        controller.SetSteeringSuppression(noSlither: true, noRoll: false);

        Vector3 upTarget = controller.Head.position + Vector3.up * 50f; // big up target to make pitch reach +90-ish
        controller.SetAttackOverride(upTarget, lookUpTime + (chargeUpTime + 0.25f) * (1 + totalToSpawn / Mathf.Max(1, maxPerVolley)) + 1.0f, verticalY: false);

        if (lookUpForwardSpeed > 0f) controller.SetSpeedImmediate(lookUpForwardSpeed);
        else controller.SetSpeedImmediate(0f);

        float t = 0f;
        while (t < lookUpTime)
        {
            if (!this || !controller || !controller.Head) { CleanupAndFinish(); yield break; }
            t += Time.deltaTime;
            yield return null;
        }

        // ----- 2) Fire in volleys (up to 2 each) -----
        int index = 0;
        while (index < chosenIdx.Count)
        {
            // charge-up window...
            float c = 0f;
            while (c < chargeUpTime)
            {
                if (!this || !controller || !controller.Head) { CleanupAndFinish(); yield break; }
                c += Time.deltaTime;
                yield return null;
            }

            int firesThisVolley = Mathf.Min(maxPerVolley, chosenIdx.Count - index);
            for (int j = 0; j < firesThisVolley; j++)
            {
                int slotIndex = chosenIdx[index++];
                Transform slot = (slotIndex >= 0 && slotIndex < landingSlots.Count) ? landingSlots[slotIndex] : null;
                if (!slot) continue;

                SpawnAndLaunchTurret(slot.position);

                if (neverReuseSlots)
                    _usedSlotIndices.Add(slotIndex);   // permanently consume the slot
            }
        }


        // ----- 3) Hold looking up briefly -----
        float hold = 0f;
        while (hold < postFireHoldTime)
        {
            if (!this || !controller || !controller.Head) { CleanupAndFinish(); yield break; }
            hold += Time.deltaTime;
            yield return null;
        }

        // Clear rotate-up lock, zero forward creep, then run your face to straighten to idle
        controller.ClearAttackOverride();
        controller.SetSpeedImmediate(0f);
        controller.SetSteeringSuppression(noSlither: true, noRoll: false);

        // Face a target player, then enter idle (body straightening will kick in)
        controller.BeginFaceThenIdle();

        CleanupAndFinish();
    }

    private void CleanupAndFinish()
    {
        if (controller)
        {
            controller.SetSteeringSuppression(noSlither: false, noRoll: false);
            // Let the controller manage idle speed—it will keep speed 0 during idle anyway.
        }

        if (ai) SnakeUtilityAi.active = true; else SnakeUtilityAi.active = true;
        IsBusy = false;
    }

    // ---------- helpers ----------
    private List<int> GetAvailableSlotIndices()
    {
        var list = new List<int>(landingSlots.Count);
        for (int i = 0; i < landingSlots.Count; i++)
        {
            if (!landingSlots[i]) continue;
            if (neverReuseSlots && _usedSlotIndices.Contains(i)) continue;
            list.Add(i);
        }
        return list;
    }

    private static void Shuffle<T>(List<T> list)
    {
        for (int i = 0; i < list.Count; i++)
        {
            int k = Random.Range(i, list.Count);
            (list[i], list[k]) = (list[k], list[i]);
        }
    }

    private void SpawnAndLaunchTurret(Vector3 landingPos)
    {
        NetworkObject no = Instantiate(turretPrefab, mouth.position, Quaternion.identity);

        var turret = no.GetComponent<DeployedTurret>();
        if (turret)
        {
            var init = new DeployedTurret.InitParams
            {
                startPos = mouth.position,
                targetPos = landingPos,
                arcHeight = arcHeight,
                startSpeed = Mathf.Max(0.01f, startSpeed),
                endSpeed = Mathf.Max(0.01f, endSpeed),
                spinYDegPerSec = spinYDegPerSec,
                arriveEps = Mathf.Max(0.01f, arriveEpsilon),
            };
            turret.InitializeServer(init);
        }

        no.Spawn(true);
    }



    private static List<Transform> PickUniqueRandomSlots(List<Transform> src, int count)
    {
        // Fisher–Yates shuffle then take first N
        var list = new List<Transform>(src.Count);
        for (int i = 0; i < src.Count; i++) if (src[i]) list.Add(src[i]);
        for (int i = 0; i < list.Count; i++)
        {
            int k = Random.Range(i, list.Count);
            (list[i], list[k]) = (list[k], list[i]);
        }
        if (count < list.Count) list.RemoveRange(count, list.Count - count);
        return list;
    }
}
