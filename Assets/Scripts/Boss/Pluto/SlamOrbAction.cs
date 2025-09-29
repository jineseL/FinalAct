using UnityEngine;

public class SlamOrbAction : OrbActionBase
{
    [Header("Bindings")]
    [SerializeField] private SlamOrb orb;

    [Header("Scoring")]
    [SerializeField] private float constantBias = 1f; // this move is always valid when idle

    public override bool CanExecute(BossContext ctx)
    {
        if (!base.CanExecute(ctx)) return false; // action cooldown/busy
        if (IsBusy) return false;
        if (!orb || !orb.IsServer) return false;
        if (!orb.IsAlive) return false;

        // Only when not already active (busy)
        return !orb.IsBusy;
    }

    public override float ReturnScore(BossContext ctx)
    {
        if (!CanExecute(ctx)) return 0f;
        // keep it simple: allow AI mix by constant bias; tune later
        return Mathf.Clamp01(constantBias);
    }

    public override void ExecuteMove(BossContext ctx)
    {
        if (!CanExecute(ctx)) return;

        // lock this action so the selector cannot re-pick during the same tick
        IsBusy = true;
        MarkUsed(ctx);

        void OnDone()
        {
            // unlock when orb deactivates (dies) or if you later add a completion event
            orb.EffectCompleted -= OnDone;
            IsBusy = false;
        }
        orb.EffectCompleted += OnDone;
        // For now, we unlock when the orb leaves Active by death/respawn (the action can be chosen again later)
        // If you add an EffectCompleted event, subscribe here and clear IsBusy on invoke.

        orb.ActivateSlam();
    }
}
