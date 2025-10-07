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
    [SerializeField] private float begginingThink = 10f;
    private float thinkTimer;

    private SnakeBossController controller;
    public static bool active = false;

    public override void OnNetworkSpawn()
    {
        if (!IsServer) { enabled = false; return; }
        // Do not auto-activate; controller calls Activate after intro delay.
        thinkTimer = thinkInterval;
    }

    // Called by controller when AI should start (after intro / when alive)
    public void Activate(SnakeBossController ctrl)
    {
        if (!IsServer) return;
        controller = ctrl;
        active = true;
        thinkTimer = begginingThink; // think immediately
    }

    public void Deactivate()
    {
        if (!IsServer) return;
        active = false;
    }

    private void Update()
    {
        if (!IsServer || !active || controller == null) return;

        thinkTimer -= Time.deltaTime;
        if (thinkTimer > 0f) return;
        thinkTimer = thinkInterval;
        //think
        BossContext ctx = controller.BuildContext();

        const float EPS = 1e-4f;

        float bestScore = -1f;
        List<AttackActionBase> candidates = new();

        for (int i = 0; i < actions.Count; i++)
        {
            var a = actions[i];
            if (!a || !a.CanExecute(ctx)) continue;

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

        if (candidates.Count > 0)
        {
            var chosen = candidates[Random.Range(0, candidates.Count)];
            chosen.ExecuteMove(ctx);
        }
    }
}
