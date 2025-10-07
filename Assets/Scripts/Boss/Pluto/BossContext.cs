using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Context for building in decision making, build a context in think() and use it as input for all decisions
/// </summary>
public sealed class BossContext
{
    public SnakeBossController Boss;
    public IReadOnlyList<GameObject> Players; //list of players
    public Vector3 BossPos; //boss position
    public float BossHpPct; //boss percentage
    public bool Phase2; //if boss is in phase2

    // Precomputed facts to avoid rework in orbs:
    public GameObject Primary; // main target for this tick
    public GameObject Secondary; //The other player, if present. In solo it is null, use if there is actions that need to consider both players
    public float DistToPrimary; //Distance from the boss to Primary. for actions that prefer close/far targets, or have range thresholds.
    public float DistBetweenPlayers;//Distance between the two players. In solo set to 0. Helps decide if an AoE that hits both is worthwhile vs a single-target move.
    public float TimeNow; //Current server time (Time.time) captured once per tick. Used for cooldown comparisons and timing without calling Time.time all over, but is not important for pluto


    // Availability
    public bool AnyGravityFree;
    public bool AnySlamFree;
    //public bool AnyMissileFree;
    // ctx.AnyGravityFree = actions.Any(a => a.Kind == OrbActionKind.Gravity && a.CanExecute(ctx));
    //to enforce rules:
    /*Do not pick two actions of the same kind in one tick.
    Prefer at most one Slam active at a time.
    Boost all Gravity actions in phase 2.*/

    // Add more over time
}
