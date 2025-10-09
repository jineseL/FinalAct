using UnityEngine;

public abstract class AttackActionBase : MonoBehaviour
{
    public bool IsBusy { get; protected set; }
    public float Cooldown = 3f;
    protected float lastUsed = -999f;

    public virtual bool CanExecute(BossContext ctx)
    {
        //cooldown
        //Debug.Log("ctx.TimeNow= " + ctx.TimeNow + "lastUsed + Cooldown = " + lastUsed + Cooldown);
        return !IsBusy && (ctx.TimeNow >= lastUsed + Cooldown);
    }

    public abstract float ReturnScore(BossContext ctx); // 0..1
    public abstract void ExecuteMove(BossContext ctx);

    protected void MarkUsed(BossContext ctx) => lastUsed = ctx.TimeNow;
}
