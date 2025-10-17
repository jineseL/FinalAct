using UnityEngine;
using Unity.Netcode;
using System.Linq;

public class Weapon1script : Weapons
{
    [Header("Blast Settings")]
    [SerializeField] private NetworkObject blastAoePrefab; // assign in Inspector
    public Transform firePoint;
    public float blastRadius = 5f;
    public float blastLifetime = 0.12f;
    public float playerKnockbackForce = 25f;
    public float enemyDamage = 25f; // you said you'll remove enemy damage from BlastAoE yourself
    public float selfRecoilForce = 18f;

    [Header("Pellet Settings")]
    [SerializeField] private NetworkObject pelletPrefab;      // prefab with NetworkObject + ShotgunPelletProjectile
    [SerializeField] private int pelletCount = 8;             // pellets per shot
    [SerializeField] private float spreadAngleDeg = 7f;       // shotgun cone half-angle
    [SerializeField] private float pelletSpeed = 60f;         // m/s
    [SerializeField] private float pelletLifetime = 1.2f;     // s
    [SerializeField] private float damageNear = 22f;          // damage at 0..falloffStart
    [SerializeField] private float damageFar = 6f;            // damage at falloffEnd+
    [SerializeField] private float falloffStart = 5f;         // meters
    [SerializeField] private float falloffEnd = 25f;          // meters
    [SerializeField] private LayerMask pelletHitMask;         // layers pellets can hit (ENEMIES / WORLD) — DO NOT pass via RPC

    [Header("Fps View")]
    public Transform FpsShootPoint1;
    public Transform FpsShootPoint2;
    public GameObject ShootVFX1;

    private bool autoReloadPending = false;

    public override void Fire()
    {
        if (isAttacking || isReloading) return;

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
        FpsFireServerRpc();

        // recoil for the owner
        WorldFire();

        autoReloadPending = (currentAmmoCount <= 0);
    }

    public override void AltFire()
    {
        if (isAttacking || isReloading) return;

        if (currentAmmoCount <= 0)
        {
            BeginReload();
            return;
        }
        if (currentAmmoCount <= altFireAmmoConsume)
        {
            return;
        }

        isAttacking = true;

        currentAmmoCount = Mathf.Max(0, currentAmmoCount - altFireAmmoConsume);

        AltFpsFire();
        AltWorldFire();

        autoReloadPending = (currentAmmoCount <= 0);
    }

    public override void Reload() => BeginReload();

    private void BeginReload()
    {
        if (isReloading) return;
        if (currentAmmoCount >= maxAmmo) return;
        if (isAttacking) { autoReloadPending = true; return; }

        isReloading = true;
        if (fpsAnimator) fpsAnimator.Play("FpsDoubleBarrelReload");
    }

    public override void AE_ReloadCommit()
    {
        currentAmmoCount = maxAmmo;
    }

    public override void AE_ReloadFinished()
    {
        isReloading = false;
        autoReloadPending = false;
    }

    public override void TryAutoReloadAfterShot()
    {
        if (autoReloadPending && !isReloading)
        {
            autoReloadPending = false;
            BeginReload();
        }
    }

    // ===== firing =====

    public void WorldFire()
    {
        if (!IsOwner) return;

        ApplySelfRecoilLocal();

        // keep your knockback AoE for players
        SpawnBlastServerRpc(firePoint.position, firePoint.forward);

        // spawn actual pellets (server only)
        SpawnPelletsServerRpc(firePoint.position, firePoint.rotation);
    }

    public void AltWorldFire()
    {
        if (!IsOwner) return;

        ApplySelfRecoilLocal();

        // You said ignore altfire for now, but this keeps parity
        SpawnBlastServerRpc(firePoint.position, firePoint.forward);

        // You can also spawn pellets here if alt-fire should shoot as well
        // SpawnPelletsServerRpc(...);
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

    [ServerRpc]
    private void SpawnPelletsServerRpc(Vector3 origin, Quaternion rot)
    {
        if (!pelletPrefab) return;

        for (int i = 0; i < pelletCount; i++)
        {
            // random cone in local space
            Vector2 r = Random.insideUnitCircle;           // uniform disc
            float yaw = r.x * spreadAngleDeg;              // left/right
            float pitch = -r.y * spreadAngleDeg;           // up/down (neg for typical view)
            Quaternion pelletRot = rot * Quaternion.Euler(pitch, yaw, 0f);

            var no = Instantiate(pelletPrefab, origin, pelletRot);
            var proj = no.GetComponent<ShotgunPelletProjectile>();
            if (proj != null)
            {
                proj.SetParams(
                    speed: pelletSpeed,
                    lifetime: pelletLifetime,
                    damageNear: damageNear,
                    damageFar: damageFar,
                    falloffStart: falloffStart,
                    falloffEnd: falloffEnd,
                    hitMask: pelletHitMask,
                    shooterClientId: OwnerClientId
                );
            }

            no.Spawn(true);
        }
    }

    [ServerRpc(RequireOwnership = true)]
    private void FpsFireServerRpc()
    {
        var others = NetworkManager.Singleton.ConnectedClientsIds.Where(id => id != OwnerClientId).ToArray();
        FpsFireClientRpc(new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = others } });
    }

    [ClientRpc]
    private void FpsFireClientRpc(ClientRpcParams rpcParams = default) => FpsFire();

    public void FpsFire()
    {
        if (fpsAnimator) fpsAnimator.Play("FpsDoubleBarrelFiring");
        SoundManager.PlaySfxAt("DoubleBarrelSFX", FpsShootPoint1.position, 0.8f, 1);

        if (currentAmmoCount % 2 == 0)
            Instantiate(ShootVFX1, FpsShootPoint1.position, Quaternion.LookRotation(FpsShootPoint1.forward, Vector3.forward));
        else
            Instantiate(ShootVFX1, FpsShootPoint2.position, Quaternion.LookRotation(FpsShootPoint2.forward, Vector3.forward));
    }

    public void AltFpsFire()
    {
        if (fpsAnimator) fpsAnimator.Play("FpsDoubleBarrelFiring");
        SoundManager.PlaySfxAt("DoubleBarrelSFX", FpsShootPoint1.position, 0.8f, 1);

        if (currentAmmoCount % 2 == 0)
            Instantiate(ShootVFX1, FpsShootPoint1.position, Quaternion.LookRotation(FpsShootPoint1.forward, Vector3.forward));
        else
            Instantiate(ShootVFX1, FpsShootPoint2.position, Quaternion.LookRotation(FpsShootPoint2.forward, Vector3.forward));
    }
}
