using UnityEngine;

public class FpsSetUpFunctionHolders : MonoBehaviour
{
    public PlayerWeaponManager playerWeaponManager;

    /// <summary>
    /// Animation Event: called at the end of firing
    /// </summary>
    public void ResetAttack()
    {
        if (playerWeaponManager.currentWeapon != null)
        {
            playerWeaponManager.currentWeapon.ResetAttacking();
            playerWeaponManager.currentWeapon.TryAutoReloadAfterShot();
        }
    }
    /// <summary>
    /// Animation Event: called at the moment ammo should refill.
    /// </summary>
    public void AE_ReloadCommit()
    {
        if (playerWeaponManager.currentWeapon != null)
        {
            playerWeaponManager.currentWeapon.AE_ReloadCommit();
        }
    }
    /// <summary>
    /// Animation Event: called at the end of reload anim.
    /// </summary>
    public void AE_ReloadFinished()
    {
        if (playerWeaponManager.currentWeapon != null)
        {
            playerWeaponManager.currentWeapon.AE_ReloadFinished();
        }
    }
}
