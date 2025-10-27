using System.Collections;
using Unity.Netcode;
using UnityEngine;

/// Moves the boss to a new waypoint in front of its head, rises above it,
/// faces a player once (speed 0), then returns to idle (tail sway on).
public class SnakeRelocateAction : AttackActionBase
{
    [Header("Bindings")]
    [SerializeField] private SnakeBossController controller;

    [Header("Facing Gate After Rise")]
    [SerializeField] private float faceMinTime = 0.3f;     // minimum track time before we accept alignment
    [SerializeField, Range(0f, 1f)] private float faceDot = 0.95f; // required alignment to finish facing
    [SerializeField] private float faceTimeout = 2.5f;     // max seconds to try to face

    [Header("Scoring")]
    [SerializeField, Range(0f, 1f)] private float baseScore = 1f; // always acceptable by default

    public override bool CanExecute(BossContext ctx)
    {
        if (!base.CanExecute(ctx)) return false;                 // cooldown / IsBusy from base
        if (ctx.Boss == null || !ctx.Boss.IsServer) return false;
        if (!controller) controller = ctx.Boss as SnakeBossController ?? controller;
        if (!controller) return false;

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

        // Exit idle so we can travel
        if(controller.IsIdling)
        controller.ExitIdleHover();

        // Run the relocate sequence, then come back to idle and release.
        controller.StartCoroutine(RunSequence());
    }

    private IEnumerator RunSequence()
    {

        // 1) relocate to a waypoint 
        yield return controller.CoRelocateToWaypointAndIdle();


        IsBusy = false;
    }
}
