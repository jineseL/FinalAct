using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

[DisallowMultipleComponent]
public class SnakeUtilityAi : NetworkBehaviour
{
    [Header("Actions (Utility AI)")]
    [SerializeField] private List<AttackActionBase> actions = new();

    [Header("AI Tick")]
    [SerializeField] private float thinkInterval = 0.25f; //to act as a buffer in case i want to do any action befor any move execute
    //[SerializeField] private float initialThinkDelay = 10f;
    private float thinkTimer;
    private bool thinking;
    [Header("Integration")]
    [SerializeField] private bool gateByControllerIdle = true;

    private SnakeBossController controller;
    public static bool active = false;

    public override void OnNetworkSpawn()
    {
        if (!IsServer) { enabled = false; return; }
        // Do not auto-activate; controller calls Activate after intro delay.
        //thinkTimer = thinkInterval;
    }

    // Called by controller when AI should start (after intro / when alive)
    /*public void Activate(SnakeBossController ctrl)
    {
        if (!IsServer) return;
        controller = ctrl;
        active = true;
        thinkTimer = initialThinkDelay; // think immediately
    }*/
    public void Activate(SnakeBossController ctrl)
    {
        if (!IsServer) return;
        controller = ctrl;
        active = true;
        thinkTimer = thinkInterval; // controller will zero this when idle ends
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
            // 1) Do not think while any action is in progress
            if (AnyActionBusy())
            {
                thinkTimer = thinkInterval;
                return;
            }

            // 2) Do not think while the controller is idling (controller owns idle timing)
            if (gateByControllerIdle && controller.IsIdling)
            {
                //thinkTimer = thinkInterval; // keep it armed; controller will ForceThinkNow() when idle ends
                return;
            }

            // 3) Think act as a second buffer of thinking time
            thinkTimer -= Time.deltaTime;
            if (thinkTimer > 0f) return;
            //thinkTimer = thinkInterval;

            BossContext ctx = controller.BuildContext();

            const float EPS = 1e-4f;
            float bestScore = -1f;
            List<AttackActionBase> candidates = new();

            for (int i = 0; i < actions.Count; i++)
            {
                var a = actions[i];
                if (!a || !a.CanExecute(ctx)) continue;

                float s = Mathf.Clamp01(a.ReturnScore(ctx));
                if (s > bestScore + EPS) { bestScore = s; candidates.Clear(); candidates.Add(a); }
                else if (Mathf.Abs(s - bestScore) <= EPS) { candidates.Add(a); }
            }

            if (candidates.Count > 0)
            {
                var chosen = candidates[Random.Range(0, candidates.Count)];
                chosen.ExecuteMove(ctx);
                thinking = false;
            }
            else
            {
                // No valid actions yet: try again after the buffer
                thinkTimer = thinkInterval;
                // keep `thinking = true` so Update continues to count down
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

        // Replace "actions" with whatever collection you already use internally.
        foreach (var a in actions)
        {
            if (a == null) continue;
            if (a.CanExecute(ctx)) return true;
        }
        return false;
    }

    // Try to start the highest-score action immediately. Returns true if something started.
    public bool TryStartNextActionImmediately(SnakeBossController boss)
    {
        if (!active || boss == null || !boss.IsServer) return false;

        var ctx = boss.BuildContext();
        if (ctx == null) return false;

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
            // Start it right now
            best.ExecuteMove(ctx);
            return true;
        }
        return false;
    }

}
