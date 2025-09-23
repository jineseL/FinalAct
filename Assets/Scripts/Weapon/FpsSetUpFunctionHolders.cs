using UnityEngine;

public class FpsSetUpFunctionHolders : MonoBehaviour
{
    public PlayerWeaponManager playerWeaponManager;

    public void ResetAttack()
    {
        if(playerWeaponManager.currentWeapon!=null)
        playerWeaponManager.currentWeapon.ResetAttacking();
    }
}
