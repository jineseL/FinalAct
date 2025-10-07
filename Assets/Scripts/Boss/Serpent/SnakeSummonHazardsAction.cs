using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// Spawns 2-4 hazards (more when low HP) at random body points.
/// Each hazard prefab must have: NetworkObject + NetworkTransform (Sync Scale), Rigidbody (kinematic), Collider (trigger), and a HazardBase subclass.
public class SnakeSummonHazardsAction : AttackActionBase
{
    [Header("Hazard Prefabs (random pick)")]
    [SerializeField] private NetworkObject[] hazardPrefabs;

    [Header("Spawn Points on Body")]
    [SerializeField] private Transform[] bodyPoints;

    [Header("Counts")]
    [SerializeField] private int highHpMin = 2;
    [SerializeField] private int midHpMin = 3;
    [SerializeField] private int lowHpMin = 4;

    /*[Header("Charge-up Override (optional)")]
    [SerializeField] private float chargeTimeOverride = -1f; // <0 means use prefab’s value
    [SerializeField] private float targetScaleOverride = -1f; // <0 means use prefab’s value*/

    [Header("Gravity Hazard Help")]
    [SerializeField] private Transform arenaCenter; // used when only one player

    public override bool CanExecute(BossContext ctx)
    {
        if (!base.CanExecute(ctx)) return false;
        if (ctx.Boss == null || !ctx.Boss.IsServer) return false;
        if (hazardPrefabs == null || hazardPrefabs.Length == 0) return false;
        if (bodyPoints == null || bodyPoints.Length == 0) return false;
        return true;
    }

    public override float ReturnScore(BossContext ctx)
    {
        if (!CanExecute(ctx)) return 0f;
        // basic: more likely when players spread
         //float spread = Mathf.InverseLerp(2f, 12f, ctx.DistBetweenPlayers);
        return 1f;
    }

    public override void ExecuteMove(BossContext ctx)
    {
        if (!CanExecute(ctx)) return;

        IsBusy = true;
        MarkUsed(ctx);

        int count = DecideCount(ctx.BossHpPct);

        // Clamp by available body points so the loop doesn’t stall
        if (bodyPoints == null || bodyPoints.Length == 0) { IsBusy = false; return; }
        count = Mathf.Clamp(count, 1, bodyPoints.Length);

        StartCoroutine(SpawnRoutine(ctx, count));
    }

    private int DecideCount(float hpPct)
    {
        if (hpPct <= 0.33f) return lowHpMin;
        if (hpPct <= 0.66f) return midHpMin;
        return highHpMin;
    }

    private IEnumerator SpawnRoutine(BossContext ctx, int count)
    {
        // Shuffle body points so selection is varied
        var indices = new List<int>(bodyPoints.Length);
        for (int i = 0; i < bodyPoints.Length; i++) indices.Add(i);
        for (int i = indices.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (indices[i], indices[j]) = (indices[j], indices[i]);
        }

        // Shuffle hazard prefabs once so we cycle through all types before repeating
        var hazardPool = new List<NetworkObject>(hazardPrefabs);
        for (int i = hazardPool.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (hazardPool[i], hazardPool[j]) = (hazardPool[j], hazardPool[i]);
        }

        for (int i = 0; i < count; i++)
        {
            int bpIdx = indices[i];
            var point = bodyPoints[bpIdx];
            if (!point) continue;

            // Pick hazard type with variety: cycle pool if count > types
            var prefab = hazardPool[(i % hazardPool.Count)];
            if (!prefab)
            {
                Debug.LogWarning("[SnakeSummonHazardsAction] Null hazard prefab.");
                continue;
            }

            // Instantiate at the body point's current pose
            var no = Object.Instantiate(prefab, point.position, point.rotation);

            var hb = no.GetComponent<HazardBase>();
            if (!hb)
            {
                Debug.LogWarning($"[SnakeSummonHazardsAction] {prefab.name} missing HazardBase.");
            }
            else
            {
                // NEW: follow this body point WHILE CHARGING (HazardBase must implement SetChargeFollowTarget)
                hb.SetChargeFollowTarget(point);

                // Per-type hints
                if (hb is HomingCubeHazard homing)
                {
                    // choose closest player to this spawn point
                    NetworkObject best = null;
                    float bestSqr = float.PositiveInfinity;
                    foreach (var kv in NetworkManager.Singleton.ConnectedClients)
                    {
                        var po = kv.Value.PlayerObject;
                        if (!po || !po.IsSpawned) continue;
                        float sq = (po.transform.position - point.position).sqrMagnitude;
                        if (sq < bestSqr) { bestSqr = sq; best = po; }
                    }
                    if (best) homing.SetTarget(best);
                }
                else if (hb is GravitySuctionHazard grav)
                {
                    // midpoint between players, or between single player and arena center
                    Vector3? a = null, b = null;
                    foreach (var kv in NetworkManager.Singleton.ConnectedClients)
                    {
                        var po = kv.Value.PlayerObject;
                        if (!po || !po.IsSpawned) continue;
                        if (a == null) a = po.transform.position;
                        else { b = po.transform.position; break; }
                    }
                    Vector3 dest = point.position;
                    if (a != null && b != null) dest = Vector3.Lerp(a.Value, b.Value, 0.5f);
                    else if (a != null && arenaCenter) dest = Vector3.Lerp(a.Value, arenaCenter.position, 0.5f);
                    else if (a != null) dest = a.Value;

                    grav.SetDestination(dest);
                }

                // If your HazardBase doesn’t auto-start charging on spawn,
                // expose a public `BeginCharge()` and call it here (server-side).
                // hb.BeginCharge();
            }

            // Spawn into the network
            no.Spawn(true);

            // tiny stagger so they don't pop simultaneously
            yield return new WaitForSeconds(0.1f);
        }

        IsBusy = false;
    }

}
