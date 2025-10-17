using UnityEngine;
using Unity.Netcode;
public class Weapons : NetworkBehaviour
{
    public string weaponName;
    public float damage;
    public int maxAmmo;
    public bool isAttacking;
    public bool isReloading;
    public int weaponIndex; // on the player
    public int currentAmmoCount;
    public int altFireAmmoConsume; //amount of ammo to use in altFire
    public PlayerManager owner { private set; get; }

    public GameObject fpsGun;
    public Animator fpsAnimator;

    public void EquipWeapon(PlayerManager player, GameObject fpsView)
    {
        owner = player;
        fpsGun = fpsView;
        currentAmmoCount = maxAmmo;
    }

    public void ResetAttacking() { isAttacking = false; }

    public void ResetReloading() { isReloading = false; }

    // left click
    public virtual void Fire() { }
    //right click
    public virtual void AltFire() { }
    public virtual void Reload() { }

    /// <summary>
    /// Animation Event: called at the moment ammo should refill.
    /// </summary>
    public virtual void AE_ReloadCommit()
    {
       
    }

    /// <summary>
    /// Animation Event: called at the end of reload anim.
    /// </summary>
    public virtual void AE_ReloadFinished()
    {
    }

    // NEW: lets animation event trigger auto-reload logic per weapon
    public virtual void TryAutoReloadAfterShot() { }
}

