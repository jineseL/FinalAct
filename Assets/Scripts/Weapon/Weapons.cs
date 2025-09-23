using UnityEngine;
using Unity.Netcode;
public class Weapons :NetworkBehaviour
{
    public string weaponName;
    public float damage;
    public int maxAmmo;
    public bool isAttacking;
    public bool isReloading;
    public int weaponIndex; // on the player
    public int currentAmmoCount;
    public PlayerManager owner {private set; get; }

    public GameObject fpsGun;
    public Animator fpsAnimator;
    public void EquipWeapon(PlayerManager player, GameObject fpsView)
    {
        owner = player;
        //owner.gameObject.GetComponent<PlayerWeaponManager>().EquipWeaponServerRpc(weaponIndex);
        fpsGun = fpsView;
        currentAmmoCount = maxAmmo;
    }
    public void ResetAttacking()
    {
        isAttacking = false;
    }
    public void ResetReloading()
    {
        isReloading = false;
    }
    public virtual void Fire()
    {
        
    }

    public virtual void Reload()
    {
        
    }

}
