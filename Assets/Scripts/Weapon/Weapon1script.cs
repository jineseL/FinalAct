using UnityEngine;
using Unity.Netcode;

public class Weapon1script : Weapons
{
    [Header("Blast Settings")]
    [SerializeField] private NetworkObject blastAoePrefab; // assign in Inspector
    public Transform firePoint;
    public float blastRadius = 5f;
    public float blastLifetime = 0.12f;
    public float playerKnockbackForce = 25f;
    public float enemyDamage = 25f;
    public float selfRecoilForce = 18f;

    [Header("Fps View")]
    public Transform FpsShootPoint1;
    public Transform FpsShootPoint2;
    public GameObject ShootVFX1;

    // internal: track whether we should auto-reload after the shot anim ends
    private bool autoReloadPending = false;

    public override void Fire()
    {
        // block when attacking or reloading
        if (isAttacking || isReloading) return;

        // no ammo? start reload
        if (currentAmmoCount <= 0)
        {
            BeginReload();
            return;
        }

        isAttacking = true;

        // consume one shell
        currentAmmoCount = Mathf.Max(0, currentAmmoCount - 1);

        // local FP feedback (muzzle, anim)
        FpsFire();

        // world effect (server AoE)
        WorldFire();

        // if that was the last shell, schedule an auto-reload to run when the
        // firing animation finishes (via animation event)
        autoReloadPending = (currentAmmoCount == 0);
    }

    public override void Reload()
    {
        // manual reload (from input)
        BeginReload();
    }

    private void BeginReload()
    {
        if (isReloading) return;
        if (currentAmmoCount >= maxAmmo) return;
        if (isAttacking) { autoReloadPending = true; return; } // wait until shot anim ends

        isReloading = true;

        // trigger reload animation; hook Animation Events:
        //  - AE_ReloadCommit() at the point the shells are inserted
        //  - AE_ReloadFinished() at the end
        if (fpsAnimator) fpsAnimator.Play("FpsDoubleBarrelReload");
    }

    /// <summary>
    /// Animation Event: called at the moment ammo should refill.
    /// </summary>
    public override void AE_ReloadCommit()
    {
        // ammo is owner-authoritative in this prototype (fastest)
        currentAmmoCount = maxAmmo;
    }

    /// <summary>
    /// Animation Event: called at the end of reload anim.
    /// </summary>
    public override void AE_ReloadFinished()
    {
        isReloading = false;
        autoReloadPending = false; // clear any pending
    }

    public override void TryAutoReloadAfterShot()
    {
        // Called by your animation-event handler after it calls ResetAttacking()
        if (autoReloadPending && !isReloading)
        {
            autoReloadPending = false; // we’re acting on it now
            BeginReload();
        }
    }

    // ===== existing firing pieces =====

    public void WorldFire()
    {
        if (!IsOwner) return;

        ApplySelfRecoilLocal();
        SpawnBlastServerRpc(firePoint.position, firePoint.forward);
    }

    private void ApplySelfRecoilLocal()
    {
        var motor = owner.GetComponent<PlayerMotor>();
        if (motor != null)
            motor.ApplyExternalForce(-firePoint.forward.normalized * selfRecoilForce);
    }

    [ServerRpc]
    private void SpawnBlastServerRpc(Vector3 origin, Vector3 forward)
    {
        var aoeNO = Instantiate(blastAoePrefab, origin, Quaternion.LookRotation(forward, Vector3.up));
        var aoe = aoeNO.GetComponent<BlastAoE>();
        aoe.Configure(this.NetworkObject, blastRadius, blastLifetime, playerKnockbackForce, enemyDamage);
        aoeNO.Spawn(true);
        aoe.Activate();
    }

    public void FpsFire()
    {
        if (fpsAnimator) fpsAnimator.Play("FpsDoubleBarrelFiring");
        SoundManager.PlaySfxAt("DoubleBarrelSFX", FpsShootPoint1.position, 0.8f, 1);
        if (currentAmmoCount % 2 == 0) //Instantiate(ShootVFX1, firePoint);
        Instantiate(ShootVFX1, FpsShootPoint1.position, FpsShootPoint1.rotation);
        else //Instantiate(ShootVFX1, firePoint);
        Instantiate(ShootVFX1, FpsShootPoint2.position, FpsShootPoint2.rotation);
        
        // TODO: SFX
    }
}

