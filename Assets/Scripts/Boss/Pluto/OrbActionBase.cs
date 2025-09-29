using UnityEngine;

/// <summary>
/// each orb should inherit this, and is use for decision making
/// </summary>
public enum OrbActionKind { Gravity, Slam, Shield, Other } //simple way to label each action with a category so the boss can reason about groups of actions
public abstract class OrbActionBase : MonoBehaviour
{
    public bool IsBusy { get; protected set; }
    public float Cooldown = 3f;
    protected float lastUsed = -999f;
    public virtual OrbActionKind Kind => OrbActionKind.Other; //default value of Other
    //Derived classes override it to declare their category, like Gravity, Slam, Shield.
    //example
    /*public class GravityOrbAction : OrbActionBase
    {
        public override OrbActionKind Kind => OrbActionKind.Gravity;

        // ReturnScore / ExecuteMove...
    }*/
    public virtual bool CanExecute(BossContext ctx)
    {
        return !IsBusy && ctx.TimeNow >= lastUsed + Cooldown;
    }

    public abstract float ReturnScore(BossContext ctx); // 0..1
    public abstract void ExecuteMove(BossContext ctx);

    protected void MarkUsed(BossContext ctx)
    {
        lastUsed = ctx.TimeNow;
    }
}
