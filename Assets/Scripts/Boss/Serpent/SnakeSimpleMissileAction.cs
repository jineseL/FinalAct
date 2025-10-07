using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// Utility-AI action: spawns slow homing missiles from shoot points.
/// Server-only. Very simple continuous homing toward current player position.
public class SnakeSimpleMissileAction : AttackActionBase
{
    [Header("Missile Prefab")]
    [SerializeField] private NetworkObject missilePrefab;      // prefab with NetworkObject + NetworkTransform + SnakeHomingMissile

    [Header("Launch Points")]
    [SerializeField] private Transform[] shootPoints;          // missiles launch along point.forward (blue axis)
    [SerializeField] private float launchInterval = 0.15f;     // delay between each spawn

    [Header("Counts")]
    [SerializeField] private int missilesPerPoint = 1;         // burst count per point
    [SerializeField] private int maxTotalMissiles = 24;        // safety cap

    [Header("Targeting")]
    [SerializeField] private bool roundRobinTargets = true;    // cycle across players; else closest-per-shot

    [Header("Missile Settings")]
    [SerializeField] private float initialSpeed = 10f;            // this is actually useless
    [SerializeField] private float rotateDegPerSec = 90f;      // how fast missile can turn
    [SerializeField] private float lifetime = 8f;              // auto explode after this time

    [Header("Explosion/Damage")]
    [SerializeField] private float explosionRadius = 2.5f;
    [SerializeField] private int explosionDamage = 15;
    [SerializeField] private float explosionKnockback = 16f;
    [SerializeField] private float explosionUpBias = 0.1f;
    [SerializeField] private LayerMask explodeOnLayers;        // multi-select: Player, Ground, etc.

    [Header("Missile Health")]
    [SerializeField] private int missileHP = 1;                // 1-hit

    [Header("Execution Constraints")]
    [SerializeField] private float minBossWorldY = 6f;         // do not fire if boss head below this Y

    private int rrIndex = 0;

    public override bool CanExecute(BossContext ctx)
    {
        if (!base.CanExecute(ctx)) return false;
        if (ctx.Boss == null || !ctx.Boss.IsServer) return false;
        if (missilePrefab == null || shootPoints == null || shootPoints.Length == 0) return false;
        if (ctx.Players == null || ctx.Players.Count == 0) return false;

        var head = ctx.Boss.Head;
        if (!head || head.position.y < minBossWorldY) return false;

        return true;
    }

    public override float ReturnScore(BossContext ctx)
    {
        if (!CanExecute(ctx)) return 0f;//cannot execute
        // simple, always acceptable
        //todo proper scoring
        return 1f;
    }

    public override void ExecuteMove(BossContext ctx)
    {
        if (!CanExecute(ctx)) return;

        IsBusy = true;
        MarkUsed(ctx);

        // collect targets (NetworkObjects)
        var targets = new List<NetworkObject>(ctx.Players.Count);
        foreach (var go in ctx.Players)
        {
            var no = go.GetComponent<NetworkObject>();
            if (no && no.IsSpawned) targets.Add(no);
        }
        if (targets.Count == 0) { IsBusy = false; return; }

        StartCoroutine(LaunchRoutine(targets));
    }

    private IEnumerator LaunchRoutine(List<NetworkObject> targets)
    {
        int launched = 0;

        foreach (var point in shootPoints)
        {
            if (!point) continue;

            for (int i = 0; i < missilesPerPoint; i++)
            {
                if (launched >= maxTotalMissiles) break;

                // pick target
                NetworkObject targetNO = null;
                if (roundRobinTargets)
                {
                    targetNO = targets[rrIndex % targets.Count];
                    rrIndex++;
                }
                else
                {
                    float bestSqr = float.PositiveInfinity;
                    for (int t = 0; t < targets.Count; t++)
                    {
                        float sq = (targets[t].transform.position - point.position).sqrMagnitude;
                        if (sq < bestSqr) { bestSqr = sq; targetNO = targets[t]; }
                    }
                }

                // spawn configured missile
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
                NetworkSfxRelay.All_Play2D("Missileaunch",1, Random.Range(0.96f, 1.04f));
                noMissile.Spawn(true);
                missile?.Activate();

                launched++;
                yield return new WaitForSeconds(launchInterval);
            }
        }

        IsBusy = false;
    }
}
