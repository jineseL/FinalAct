using Unity.Netcode;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;

public class PlutoUtilityAi : MonoBehaviour
{
    [SerializeField] private bool useEpsilonGreedy = true;
    [SerializeField] private float epsilon = 0.1f;
    [SerializeField] private bool allowZeroScorePick = false;

    private PlutoBoss boss;
    private readonly List<OrbActionBase> actions = new();

    public void Initialize(PlutoBoss owner, IList<OrbActionBase> providedActions)
    {
        boss = owner;
        actions.Clear();
        if (providedActions != null)
        {
            foreach (var a in providedActions)
                if (a && !actions.Contains(a))
                    actions.Add(a);
        }
        LogActions("Initialize");
    }

    public BossContext BuildContext()
    {
        var ctx = new BossContext
        {
            Boss = boss,
            TimeNow = Time.time,
            BossPos = boss.core ? boss.core.position : boss.transform.position
        };

        var bh = boss.GetComponent<BossHealth>();
        ctx.BossHpPct = (bh && bh.MaxHP > 0f) ? (bh.CurrentHP / bh.MaxHP) : 0f;
        ctx.Phase2 = ctx.BossHpPct <= 0.5f;

        var list = GetAlivePlayers();
        ctx.Players = list;

        if (list.Count > 0)
        {
            GameObject primary = null;
            float bestSqr = float.PositiveInfinity;
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                float sq = (p.transform.position - ctx.BossPos).sqrMagnitude;
                if (sq < bestSqr) { bestSqr = sq; primary = p; }
            }
            ctx.Primary = primary;
            ctx.DistToPrimary = primary ? Vector3.Distance(ctx.BossPos, primary.transform.position) : 0f;

            if (list.Count > 1)
            {
                var secondary = list[0] == primary ? list[1] : list[0];
                ctx.Secondary = secondary;
                ctx.DistBetweenPlayers = Vector3.Distance(primary.transform.position, secondary.transform.position);
            }
            else
            {
                ctx.Secondary = null;
                ctx.DistBetweenPlayers = 0f;
            }
        }

        // Example availability flags, optional
        ctx.AnyGravityFree = actions.Any(a => a != null && a.CanExecute(ctx)); // broad
        ctx.AnySlamFree = ctx.AnyGravityFree; // placeholder, adjust when you add kinds

        return ctx;
    }

    public void SelectAndExecute(BossContext ctx)
    {
        if (boss == null || !boss.IsServer) return;

        if (actions.Count == 0) { LogActions("SelectAndExecute-Empty"); return; }

        var candidates = actions.Where(a => a != null && a.CanExecute(ctx)).ToList();
        if (candidates.Count == 0) return;

        if (candidates.Count == 1)
        {
            candidates[0].ExecuteMove(ctx);
            return;
        }

        float bestScore = float.NegativeInfinity;
        OrbActionBase best = null;
        var scored = new List<(OrbActionBase act, float score)>(candidates.Count);

        foreach (var a in candidates)
        {
            float s = Mathf.Clamp01(a.ReturnScore(ctx));
            scored.Add((a, s));
            if (s > bestScore) { bestScore = s; best = a; }
        }

        if (!allowZeroScorePick && bestScore <= 0f) return;

        if (useEpsilonGreedy && Random.value < Mathf.Clamp01(epsilon))
        {
            best = candidates[Random.Range(0, candidates.Count)];
        }
        else
        {
            const float tieEps = 0.0001f;
            var bestGroup = scored.Where(t => Mathf.Abs(t.score - bestScore) <= tieEps)
                                  .Select(t => t.act).ToList();
            if (bestGroup.Count > 1)
                best = bestGroup[Random.Range(0, bestGroup.Count)];
        }

        if (best != null) best.ExecuteMove(ctx);
    }

    private List<GameObject> GetAlivePlayers()
    {
        var list = new List<GameObject>();
        var nm = NetworkManager.Singleton;
        if (nm == null) return list;

        foreach (var kv in nm.ConnectedClients)
        {
            var po = kv.Value.PlayerObject;
            if (po != null && po.IsSpawned) list.Add(po.gameObject);
        }
        return list;
    }

    private void LogActions(string where)
    {
        Debug.Log($"[PlutoUtilityAi::{where}] actions={actions.Count}");
    }
}
