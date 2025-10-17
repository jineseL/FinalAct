using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(NetworkObject))]
[DisallowMultipleComponent]
public class ShotgunPelletProjectile : NetworkBehaviour
{
    [Header("Runtime (debug)")]
    [SerializeField] private float speed;          // m/s
    [SerializeField] private float lifetime;       // seconds remaining
    [SerializeField] private float damageNear;     // damage at falloffStart and below
    [SerializeField] private float damageFar;      // damage at falloffEnd and beyond
    [SerializeField] private float falloffStart;   // meters
    [SerializeField] private float falloffEnd;     // meters
    [SerializeField] private LayerMask hitMask;    // layers we consider a hit
    [SerializeField] private ulong shooterClientId;

    [Header("Impact VFX / Decal")]
    [SerializeField] private GameObject groundHitVfx;      // smoke, sparks, etc
    [SerializeField] private float vfxLifetime = 2f;       // seconds to auto-destroy
    [SerializeField] private float vfxOffsetAlongNormal = 0.02f; // lift off surface to avoid z-fighting
    [SerializeField] private GameObject bulletHole;        // optional decal (quad)
    [SerializeField] private float bulletHoleLifetime = 10f;
    [SerializeField] private bool randomizeDecalTwist = true; // spin around normal a bit

    private float traveled;
    private Collider col;
    private Vector3 _lastPos;

    // Call this on the server immediately after Instantiate, before Spawn.
    public void SetParams(
        float speed,
        float lifetime,
        float damageNear,
        float damageFar,
        float falloffStart,
        float falloffEnd,
        LayerMask hitMask,
        ulong shooterClientId)
    {
        this.speed = speed;
        this.lifetime = lifetime;
        this.damageNear = damageNear;
        this.damageFar = damageFar;
        this.falloffStart = Mathf.Min(falloffStart, falloffEnd);
        this.falloffEnd = Mathf.Max(falloffStart, falloffEnd);
        this.hitMask = hitMask;
        this.shooterClientId = shooterClientId;
    }

    public override void OnNetworkSpawn()
    {
        if (!IsServer) { enabled = false; return; }

        if (!TryGetComponent(out col))
            col = gameObject.AddComponent<SphereCollider>(); // tiny default if none

        col.isTrigger = true;
        _lastPos = transform.position;
    }

    private void Update()
    {
        if (!IsServer) return;

        float dt = Time.deltaTime;
        lifetime -= dt;
        if (lifetime <= 0f)
        {
            Despawn();
            return;
        }

        float step = speed * dt;
        transform.position += transform.forward * step;
        traveled += step;

        // record for raycast-based contact in OnTriggerEnter
        _lastPos = transform.position;
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!IsServer) return;

        // Only react to layers we care about
        if ((hitMask.value & (1 << other.gameObject.layer)) == 0)
            return;
        Debug.Log("test");
        // Compute falloff damage based on traveled distance
        float dmg = ComputeFalloffDamage(traveled, damageNear, damageFar, falloffStart, falloffEnd);

        // If we hit an enemy
        IDamageable damageable = other.GetComponentInParent<IDamageable>();
        if (damageable != null && damageable.IsAlive)
        {
            damageable.TakeDamage(dmg);
            // enemy vfx to spawn here
            //Server_SpawnImpactVfx(other);
            Despawn();
            return;
        }

        // Otherwise: world geometry (ground/wall/etc)
        Server_SpawnImpactVfx(other);
        Despawn();
    }

    private static float ComputeFalloffDamage(float dist, float near, float far, float start, float end)
    {
        if (end <= start) return near;
        if (dist <= start) return near;
        if (dist >= end) return far;
        float t = Mathf.InverseLerp(start, end, dist);
        return Mathf.Lerp(near, far, t);
    }

    // Server: find a good impact point and normal, then broadcast VFX spawn to all clients.
    private void Server_SpawnImpactVfx(Collider other)
    {
        Vector3 hitPos;
        Vector3 hitNormal;

        // Cast along the pellet's travel direction for a precise contact
        Vector3 toNow = transform.position - _lastPos;
        float dist = toNow.magnitude;

        if (dist > 0.0001f)
        {
            Vector3 dir = toNow / dist;
            if (Physics.Raycast(_lastPos, dir, out RaycastHit hit, dist * 1.5f, hitMask, QueryTriggerInteraction.Ignore))
            {
                hitPos = hit.point;
                hitNormal = hit.normal;
            }
            else
            {
                // Fallback: closest point and approximate normal
                hitPos = other.ClosestPoint(transform.position);
                hitNormal = (hitPos - other.bounds.center);
                if (hitNormal.sqrMagnitude < 0.0001f) hitNormal = Vector3.up;
                hitNormal.Normalize();
            }
        }
        else
        {
            hitPos = other.ClosestPoint(transform.position);
            hitNormal = Vector3.up;
        }

        hitPos += hitNormal * vfxOffsetAlongNormal;

        SpawnImpactVfxClientRpc(hitPos, hitNormal, randomizeDecalTwist);
    }

    [ClientRpc]
    private void SpawnImpactVfxClientRpc(Vector3 pos, Vector3 normal, bool twist)
    {
        // Oriented smoke/spark: local up aligned to surface normal
        if (groundHitVfx)
        {
            Debug.Log("spawned");
            Quaternion vfxRot = Quaternion.FromToRotation(Vector3.up, normal);
            var vfx = Object.Instantiate(groundHitVfx, pos, vfxRot);
            if (vfxLifetime > 0f) Object.Destroy(vfx, vfxLifetime);
        }

        // Bullet hole decal: face outward along the normal (forward == normal)
        if (bulletHole)
        {
            Quaternion decalRot = Quaternion.LookRotation(normal, Vector3.up);
            if (twist)
                decalRot *= Quaternion.AngleAxis(Random.Range(0f, 360f), normal);

            // Slightly embed to avoid z-fighting
            var hole = Object.Instantiate(bulletHole, pos, decalRot);
            if (bulletHoleLifetime > 0f) Object.Destroy(hole, bulletHoleLifetime);
        }
    }

    private void Despawn()
    {
        if (NetworkObject && NetworkObject.IsSpawned) NetworkObject.Despawn(true);
        else Destroy(gameObject);
    }
}
