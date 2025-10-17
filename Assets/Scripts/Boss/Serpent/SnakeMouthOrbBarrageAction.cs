using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

public class SnakeMouthOrbBarrageAction : AttackActionBase
{
    [Header("Bindings")]
    [SerializeField] private SnakeBossController controller;
    [SerializeField] private Transform mouth;                       // spawn point on head
    [SerializeField] private Transform[] firingPerches;             // high positions to fly to
    //[SerializeField] private Transform arenaCenter;                 // optional, for target alternation when single player

    [Header("Orb Projectile")]
    [SerializeField] private NetworkObject orbPrefab;               // has MouthOrbProjectile + NetworkTransform
    [SerializeField] private float orbSpeed = 30f;
    [SerializeField] private float orbLife = 6f;
    [SerializeField] private float orbChargeTime = 1.2f;
    [SerializeField] private float orbStartScale = 0.2f;
    [SerializeField] private float orbEndScale = 1.0f;

    [Header("Sequence")]
    [SerializeField] private int volleys = 3;                       // how many orbs to shoot
    [SerializeField] private float perchTimeout = 6f;               // timeout to reach perch
    [SerializeField, Range(0.8f, 1f)] private float faceDot = 0.97f;// how aligned before charging
    [SerializeField] private float faceTimeout = 4f;                // max time to try to face player
    [SerializeField] private float slowSpeedDuringCharge = 4f;      // move slower while charging
    [SerializeField] private float interVolleyDelay = 0.15f;        // small rhythm delay

    [Header("Scoring")]
    [SerializeField, Range(0f, 1f)] private float baseScore = 1f;

    // target alternation aid
    private ulong lastTargetId = ulong.MaxValue;

    public override bool CanExecute(BossContext ctx)
    {
        if (!base.CanExecute(ctx)) return false;
        if (ctx.Boss == null || !ctx.Boss.IsServer) return false;
        if (!controller || !mouth) return false;
        if (firingPerches == null || firingPerches.Length == 0) return false;
        if (!orbPrefab) return false;
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

        controller.StartCoroutine(RunSequence());
    }

    private IEnumerator RunSequence()
    {
        // server only
        if (!controller || !controller.IsServer)
        {
            IsBusy = false;
            yield break;
        }
        SnakeUtilityAi.active = false;
        // Each volley:
        int lastPerch = -1;

        for (int v = 0; v < Mathf.Max(1, volleys); v++)
        {
            // 1) Pick a perch that is not same as last
            int perchIdx = Random.Range(0, firingPerches.Length);
            if (firingPerches.Length > 1 && perchIdx == lastPerch)
                perchIdx = (perchIdx + 1) % firingPerches.Length;

            lastPerch = perchIdx;
            var perch = firingPerches[perchIdx];
            if (!perch)
                continue;

            // Request controller to fly to this exact world point
            controller.SetAttackOverride(perch.position, perchTimeout);

            // Wait until override clears itself (arrival or timeout)
            float perchEnd = Time.time + perchTimeout + 0.25f;
            while (Time.time < perchEnd && controller.AttackOverrideActive)
                yield return null;

            // 2) Select a player to face (alternate between two if possible)
            var target = PickTargetNO();
            if (target != null)
            {
                controller.BeginChase(target, faceTimeout + 1f); // keep turning towards the player while we test alignment
            }

            // Wait until head faces target enough (or timeout)
            float faceEnd = Time.time + faceTimeout;
            //bool aligned = false;
            while (Time.time < faceEnd)
            {
                if (target == null || !target.IsSpawned) break;
                var head = controller.Head;
                if (head)
                {
                    Vector3 to = target.transform.position - head.position;
                    if (to.sqrMagnitude > 0.0001f)
                    {
                        float dot = Vector3.Dot(head.forward, to.normalized);
                        if (dot >= faceDot)
                        {
                            //aligned = true;
                            break;
                        }
                    }
                }
                yield return null;
            }

            // 3) Charge an orb at the mouth while slowing the boss
            controller.SetSpeedTarget(slowSpeedDuringCharge);

            // Spawn orb and tell it to charge-follow the mouth
            var no = Object.Instantiate(orbPrefab, mouth.position, mouth.rotation);
            no.Spawn(true);
            NetworkSfxRelay.All_Play2D("ChargeUp");

            var proj = no.GetComponent<MouthOrbProjectile>();
            if (proj)
            {
                proj.ServerStartCharge(mouth, orbChargeTime, orbStartScale, orbEndScale);
            }

            // wait for charge
            yield return new WaitForSeconds(orbChargeTime);

            // 4) Fire the orb forward from mouth
            if (proj && proj.IsServer) // still alive
            {
                proj.ServerLaunch(mouth.forward, orbSpeed, orbLife);
            }

            // restore speed target
            controller.ResetSpeedTarget();

            // tiny rhythm delay before next volley
            if (interVolleyDelay > 0f) yield return new WaitForSeconds(interVolleyDelay);
        }

        // Clean up: stop chasing and restore defaults
        controller.StopChase();
        controller.ClearAttackOverride();
        controller.ResetSpeedTarget();
        SnakeUtilityAi.active = true;
        IsBusy = false;
    }

    private NetworkObject PickTargetNO()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null) return null;

        // collect player NOs
        var players = new List<NetworkObject>(nm.ConnectedClients.Count);
        foreach (var kv in nm.ConnectedClients)
        {
            var po = kv.Value.PlayerObject;
            if (po && po.IsSpawned) players.Add(po);
        }
        if (players.Count == 0) return null;
        if (players.Count == 1) return players[0];

        // alternate between two
        if (players[0].OwnerClientId == lastTargetId) { lastTargetId = players[1].OwnerClientId; return players[1]; }
        lastTargetId = players[0].OwnerClientId; return players[0];
    }
}
