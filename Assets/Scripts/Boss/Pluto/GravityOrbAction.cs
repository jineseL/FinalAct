using UnityEngine;

public class GravityOrbAction : OrbActionBase
{
    [Header("Bindings")]
    [SerializeField] private GravityOrb orb;              // reference to the orb on the same planet
    [SerializeField] private Transform activationAnchor;  // a transform directly below Pluto

    [Header("Scoring")]
    [SerializeField] private float farMin = 10f;          // prefer when primary is beyond this
    [SerializeField] private float farMax = 22f;          // and ramp up towards this
    [SerializeField] private float splitMin = 6f;         // prefer when players are split beyond this
    [SerializeField] private float splitMax = 18f;
    [SerializeField] private float phase2Boost = 1.15f;   // mild boost in phase 2

    public override float ReturnScore(BossContext ctx)
    {
        if (!CanExecute(ctx)) return 0f;
        if (ctx.Players == null || ctx.Players.Count == 0) return 0f;

        // prefer when players are far from Pluto and not clumped
        float farScore = Mathf.InverseLerp(farMin, farMax, ctx.DistToPrimary);
        float splitScore = Mathf.InverseLerp(splitMin, splitMax, ctx.DistBetweenPlayers);

        float score = 0.6f * farScore + 0.4f * splitScore;
        if (ctx.Phase2) score *= phase2Boost;

        return Mathf.Clamp01(score);
    }

    public override bool CanExecute(BossContext ctx)
    {
        if (!base.CanExecute(ctx)) return false;     // checks this action's cooldown/busy if you use it
        if (IsBusy) return false;                    // action-level lock
        if (!orb || !orb.IsServer) return false;
        if (!orb.IsAlive) return false;
        if (orb.IsActive || orb.IsBusy) return false; // orb-level lock while running
        return true;
    }

    public override void ExecuteMove(BossContext ctx)
    {
        if (!CanExecute(ctx)) return;
        // lock this action immediately so selector cannot choose it again
        IsBusy = true;

        // timestamp this action's cool down
        MarkUsed(ctx);

        void OnDone()
        {
            orb.EffectCompleted -= OnDone;
            IsBusy = false;
        }
        orb.EffectCompleted += OnDone;

        // choose anchor position
        Vector3 anchorPos;
        if (activationAnchor != null) anchorPos = activationAnchor.position;
        else anchorPos = orb.BelowPluto(depth: 6f); // default offset below Pluto if no anchor assigned

        // trigger the orb server-side behavior
        orb.ActivateBlackhole(anchorPos);
    }
}

