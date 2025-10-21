using System.Collections;
using Unity.Netcode;
using UnityEngine;

/// SporeGun:
/// LMB (Fire): 3-round burst of bullets that damage the boss or feed the spore if hit.
/// RMB (AltFire): Fires a Spore Seed that sticks to the boss and becomes a damage-absorbing spore.
/// Only one spore from this gun may exist at a time (per owner).
public class SporeGun : Weapons
{
    [Header("Refs")]
    [SerializeField] private Transform firePoint;          // muzzle
    [SerializeField] private Transform aimOrigin;          // optional camera; if null we use Camera.main

    [Header("Primary (burst bullets)")]
    [SerializeField] private NetworkObject bulletPrefab;   // prefab with NetworkObject + SporeBulletProjectile
    [SerializeField] private int burstCount = 3;
    [SerializeField] private float burstInterval = 0.07f;
    [SerializeField] private float bulletSpeed = 80f;
    [SerializeField] private float bulletLifetime = 1.2f;
    [SerializeField] private float bulletDamage = 10f;
    [SerializeField] private LayerMask bulletHitMask;      // used only on server (do not pass via RPC)
    [SerializeField] private float primaryCooldown = 0.18f;
    [SerializeField] private float selfRecoil = 6f;

    [Header("AltFire (spore seed)")]
    [SerializeField] private NetworkObject sporeSeedPrefab; // prefab with NetworkObject + SporeSticky
    [SerializeField] private float seedSpeed = 45f;
    [SerializeField] private float seedLifetime = 6f;
    [SerializeField] private LayerMask seedHitMask;        // used only on server (do not pass via RPC)
    [SerializeField] private float altCooldown = 1.0f;
    [SerializeField] private float altSelfRecoil = 10f;

    [Header("HUD/FX")]
    [SerializeField] private Animator fpsAnimator;
    [SerializeField] private Transform fpsMuzzle;
    [SerializeField] private GameObject muzzleFx;
    [SerializeField] private string fireSfxKey = "SporeGun_Fire";
    [SerializeField] private string altSfxKey = "SporeGun_Alt";

    private float _nextPrimary;
    private float _nextAlt;

    // Track currently spawned spore from this gun so only one exists at a time
    private NetworkObjectReference currentSporeRef;

    // ================= Weapons overrides =================

    public override void Fire()
    {
        if (!IsOwner || isReloading) return;
        if (currentAmmoCount <= 0) { BeginReload(); return; }
        if (Time.time < _nextPrimary) return;
        _nextPrimary = Time.time + primaryCooldown;

        // Consume 1 ammo per burst
        currentAmmoCount = Mathf.Max(0, currentAmmoCount - 1);

        LocalFpsKick(fireSfxKey);
        ApplyLocalRecoil(selfRecoil);

        Vector3 origin = firePoint ? firePoint.position : transform.position;
        Vector3 aimPoint = ComputeAimPoint();

        // NOTE: no LayerMask argument here (NGO can’t serialize LayerMask in RPCs)
        StartBurstServerRpc(origin, aimPoint, burstCount, bulletSpeed, bulletLifetime, bulletDamage, OwnerClientId);

        if (currentAmmoCount <= 0) BeginReload();
    }

    public override void AltFire()
    {
        if (!IsOwner || isReloading) return;
        if (HasLiveSpore()) return;                  // only one active spore from this gun
        if (currentAmmoCount <= 0) { BeginReload(); return; }
        if (Time.time < _nextAlt) return;
        _nextAlt = Time.time + altCooldown;

        // Consume 1 ammo
        currentAmmoCount = Mathf.Max(0, currentAmmoCount - 1);

        LocalFpsKick(altSfxKey);
        ApplyLocalRecoil(altSelfRecoil);

        Vector3 origin = firePoint ? firePoint.position : transform.position;
        Quaternion rot = firePoint ? firePoint.rotation : transform.rotation;

        // NOTE: no LayerMask argument here
        SpawnSporeSeedServerRpc(origin, rot, seedSpeed, seedLifetime, OwnerClientId);

        if (currentAmmoCount <= 0) BeginReload();
    }

    public override void Reload() => BeginReload();

    private void BeginReload()
    {
        if (isReloading) return;
        if (currentAmmoCount >= maxAmmo) return;
        isReloading = true;
        if (fpsAnimator) fpsAnimator.Play("FpsDoubleBarrelReload"); // reuse any reload anim
    }

    public override void AE_ReloadCommit()
    {
        currentAmmoCount = maxAmmo;
    }

    public override void AE_ReloadFinished()
    {
        isReloading = false;
    }

    public override void TryAutoReloadAfterShot()
    {
        if (!isReloading && currentAmmoCount <= 0) BeginReload();
    }

    // ================= Helpers =================

    private void LocalFpsKick(string sfxKey)
    {
        if (fpsAnimator) fpsAnimator.Play("FpsDoubleBarrelFiring");
        if (muzzleFx)
        {
            var pos = fpsMuzzle ? fpsMuzzle.position : (firePoint ? firePoint.position : transform.position);
            var rot = fpsMuzzle ? fpsMuzzle.rotation : (firePoint ? firePoint.rotation : transform.rotation);
            Instantiate(muzzleFx, pos, rot);
        }
        if (!string.IsNullOrEmpty(sfxKey))
        {
            var pos = fpsMuzzle ? fpsMuzzle.position : (firePoint ? firePoint.position : transform.position);
            SoundManager.PlaySfxAt(sfxKey, pos, 1f);
        }
    }

    private void ApplyLocalRecoil(float force)
    {
        var motor = owner ? owner.GetComponent<PlayerMotor>() : null;
        if (motor && firePoint)
            motor.ApplyExternalForce(-firePoint.forward * force);
    }

    private Vector3 ComputeAimPoint()
    {
        // Use the player camera if available; otherwise use muzzle forward
        var cam = Camera.main;
        if (aimOrigin) cam = aimOrigin.GetComponentInChildren<Camera>() ?? cam;

        if (cam)
        {
            Ray ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0));
            if (Physics.Raycast(ray, out var hit, 1000f, ~0, QueryTriggerInteraction.Ignore))
                return hit.point;
            return ray.origin + ray.direction * 100f;
        }

        var origin = firePoint ? firePoint.position : transform.position;
        var dir = firePoint ? firePoint.forward : transform.forward;
        return origin + dir * 50f;
    }

    private bool HasLiveSpore()
    {
        NetworkObject no;
        if (!currentSporeRef.TryGet(out no)) return false;
        return no && no.IsSpawned;
    }

    // ================= Server RPCs =================
    // No LayerMask params in RPCs. We use the serialized masks (bulletHitMask, seedHitMask) on the server.

    [ServerRpc]
    private void StartBurstServerRpc(
        Vector3 origin,
        Vector3 aimPoint,
        int count,
        float speed,
        float lifetime,
        float damage,
        ulong shooterId)
    {
        if (!bulletPrefab) return;
        StartCoroutine(BurstRoutine(origin, aimPoint, count, speed, lifetime, damage, shooterId));
    }

    private IEnumerator BurstRoutine(
        Vector3 origin,
        Vector3 aimPoint,
        int count,
        float speed,
        float lifetime,
        float damage,
        ulong shooterId)
    {
        for (int i = 0; i < count; i++)
        {
            // base dir toward aim point
            Vector3 dir = aimPoint - origin;
            if (dir.sqrMagnitude < 0.0001f) dir = firePoint ? firePoint.forward : Vector3.forward;
            dir.Normalize();

            // small random spread per bullet
            Vector2 r = Random.insideUnitCircle * 0.5f;
            Quaternion spread = Quaternion.Euler(-r.y, r.x, 0f);
            Quaternion rot = Quaternion.LookRotation(spread * dir, Vector3.up);

            var no = Instantiate(bulletPrefab, origin, rot);
            var bullet = no.GetComponent<SporeBulletProjectile>();
            if (bullet)
            {
                // Pass the server-held mask (no RPC serialization)
                bullet.SetParams(speed, lifetime, damage, bulletHitMask, shooterId);
            }
            no.Spawn(true);

            if (burstInterval > 0f)
                yield return new WaitForSeconds(burstInterval);
            else
                yield return null;
        }
    }

    [ServerRpc]
    private void SpawnSporeSeedServerRpc(
        Vector3 origin,
        Quaternion rot,
        float speed,
        float lifetime,
        ulong shooterId)
    {
        if (!sporeSeedPrefab) return;

        // enforce one spore from this gun
        if (HasLiveSpore()) return;

        var no = Instantiate(sporeSeedPrefab, origin, rot);
        var seed = no.GetComponent<SporeSticky>();
        if (seed)
        {
            // configure as flying seed: it will attach on boss hit and become a spore
            seed.ConfigureAsSeed(speed, lifetime, seedHitMask, shooterId);
            // clear our handle when it finishes (detonates / despawns)
            seed.OnSporeFinished += HandleSporeFinished;
        }

        no.Spawn(true);
        currentSporeRef = new NetworkObjectReference(no);
    }

    private void HandleSporeFinished(NetworkObject sporeNo)
    {
        NetworkObject existing;
        if (currentSporeRef.TryGet(out existing) && existing == sporeNo)
            currentSporeRef = default;
    }
}
