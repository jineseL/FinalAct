using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

[DisallowMultipleComponent]
public class SnakeUtilityAi : NetworkBehaviour
{
    [Header("Actions (Utility AI)")]
    [SerializeField] private List<AttackActionBase> actions = new();

    [Header("AI Tick")]
    [SerializeField] private float thinkInterval = 0.25f;
    private float thinkTimer;
    private bool thinking;

    [Header("Integration")]
    [SerializeField] private bool gateByControllerIdle = true;

    [Header("Selection Mode")]
    [Tooltip("If ON: pick a random runnable action (ignores scores). If OFF: pick highest score (ties random).")]
    [SerializeField] private bool useRandomSelection = false;

    private SnakeBossController controller;
    public static bool active = false;

    public override void OnNetworkSpawn()
    {
        if (!IsServer) { enabled = false; return; }
    }

    public void Activate(SnakeBossController ctrl)
    {
        if (!IsServer) return;
        controller = ctrl;
        active = true;
        thinkTimer = thinkInterval;
    }

    public void Deactivate()
    {
        if (!IsServer) return;
        active = false;
    }

    private bool AnyActionBusy()
    {
        for (int i = 0; i < actions.Count; i++)
        {
            var a = actions[i];
            if (a != null && a.IsBusy) return true;
        }
        return false;
    }

    private void Update()
    {
        if (!IsServer || !active || controller == null) return;

        if (thinking)
        {
            // 1) Don't think while an action is running
            if (AnyActionBusy())
            {
                thinkTimer = thinkInterval;
                return;
            }

            // 2) Don't think while controller is in idle (controller owns idle timing)
            if (gateByControllerIdle && controller.IsIdling)
                return;

            // 3) Timer gate
            thinkTimer -= Time.deltaTime;
            if (thinkTimer > 0f) return;

            var ctx = controller.BuildContext();

            // Build lists of runnable actions and top-score candidates
            const float EPS = 1e-4f;
            float bestScore = -1f;
            List<AttackActionBase> valid = new();
            List<AttackActionBase> candidates = new();

            for (int i = 0; i < actions.Count; i++)
            {
                var a = actions[i];
                if (!a || !a.CanExecute(ctx)) continue;

                valid.Add(a);

                float s = Mathf.Clamp01(a.ReturnScore(ctx));
                if (s > bestScore + EPS)
                {
                    bestScore = s;
                    candidates.Clear();
                    candidates.Add(a);
                }
                else if (Mathf.Abs(s - bestScore) <= EPS)
                {
                    candidates.Add(a);
                }
            }

            AttackActionBase chosen = null;

            if (useRandomSelection)
            {
                if (valid.Count > 0)
                    chosen = valid[Random.Range(0, valid.Count)];
            }
            else
            {
                if (candidates.Count > 0)
                    chosen = candidates[Random.Range(0, candidates.Count)];
            }

            if (chosen != null)
            {
                chosen.ExecuteMove(ctx);
                thinking = false;
            }
            else
            {
                // Nothing available yet; try again after a short delay
                thinkTimer = thinkInterval;
            }
        }
    }

    public void ForceThinkNow()
    {
        if (!IsServer || !active) return;
        thinking = true;
        thinkTimer = thinkInterval;
    }

    public bool HasReadyAction(SnakeBossController boss)
    {
        if (!active || boss == null || !boss.IsServer) return false;
        var ctx = boss.BuildContext();
        if (ctx == null) return false;

        foreach (var a in actions)
        {
            if (a == null) continue;
            if (a.CanExecute(ctx)) return true;
        }
        return false;
    }

    // Respect selection mode when the controller asks us to start immediately
    public bool TryStartNextActionImmediately(SnakeBossController boss)
    {
        if (!active || boss == null || !boss.IsServer) return false;

        var ctx = boss.BuildContext();
        if (ctx == null) return false;

        if (useRandomSelection)
        {
            List<AttackActionBase> ready = new();
            foreach (var a in actions)
            {
                if (a == null) continue;
                if (a.CanExecute(ctx)) ready.Add(a);
            }
            if (ready.Count > 0)
            {
                var chosen = ready[Random.Range(0, ready.Count)];
                chosen.ExecuteMove(ctx);
                return true;
            }
            return false;
        }
        else
        {
            AttackActionBase best = null;
            float bestScore = float.NegativeInfinity;

            foreach (var a in actions)
            {
                if (a == null) continue;
                if (!a.CanExecute(ctx)) continue;

                float s = a.ReturnScore(ctx);
                if (s > bestScore)
                {
                    bestScore = s;
                    best = a;
                }
            }

            if (best != null)
            {
                best.ExecuteMove(ctx);
                return true;
            }
            return false;
        }
    }
}
