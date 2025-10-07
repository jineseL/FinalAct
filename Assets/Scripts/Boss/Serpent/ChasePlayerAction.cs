using System.Collections;
using UnityEngine;
using Unity.Netcode;

public class ChasePlayerAction : AttackActionBase
{
    [Header("Bindings")]
    [SerializeField] private SnakeBossController controller;

    [Header("Timing")]
    [SerializeField] private float chaseDuration = 3.0f; // how long to chase

    [Header("Scoring")]
    [SerializeField, Range(0f, 1f)] private float baseScore = 1f; // simple equal weight

    public override bool CanExecute(BossContext ctx)
    {
        if (!base.CanExecute(ctx)) return false; // cooldown / IsBusy from base
        if (ctx.Boss == null || !ctx.Boss.IsServer) return false;
        if (!controller) controller = ctx.Boss as SnakeBossController ?? controller;
        if (!controller) return false;

        // Must have at least one valid player
        var nm = NetworkManager.Singleton;
        if (nm == null || nm.ConnectedClients.Count == 0) return false;

        // Optional: don't run if already chasing
        if (controller.IsChasing) return false;

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

        // Pick a random player object on the server
        NetworkObject picked = null;
        var nm = NetworkManager.Singleton;

        // simple random pick from connected clients
        var clients = nm.ConnectedClientsList;
        if (clients.Count > 0)
        {
            var entry = clients[Random.Range(0, clients.Count)];
            picked = entry.PlayerObject;
        }

        if (picked == null || !picked.IsSpawned)
        {
            // fallback to closest
            float best = float.PositiveInfinity;
            foreach (var kv in nm.ConnectedClients)
            {
                var po = kv.Value.PlayerObject;
                if (!po || !po.IsSpawned) continue;
                float sq = (po.transform.position - controller.Head.position).sqrMagnitude;
                if (sq < best) { best = sq; picked = po; }
            }
        }

        if (picked == null)
        {
            // no valid target; do nothing but don't block future actions
            return;
        }

        // Engage chase
        bool started = controller.BeginChase(picked, chaseDuration);
        if (!started) return;

        // mark busy for the duration
        IsBusy = true;
        MarkUsed(ctx); // stamp cooldown
        controller.StartCoroutine(ReleaseAfter(chaseDuration));
    }

    private IEnumerator ReleaseAfter(float seconds)
    {
        yield return new WaitForSeconds(seconds + 0.05f);
        IsBusy = false;
    }
}
