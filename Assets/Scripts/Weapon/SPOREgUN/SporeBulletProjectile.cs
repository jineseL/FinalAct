using Unity.Netcode;
using UnityEngine;

/// Bullet that damages boss or feeds spore if it hits one.
/// Moves straight forward, dies on hit or lifetime.
[RequireComponent(typeof(NetworkObject))]
public class SporeBulletProjectile : NetworkBehaviour
{
    [SerializeField] private float speed;
    [SerializeField] private float lifetime;
    [SerializeField] private float damage;
    [SerializeField] private LayerMask hitMask;
    [SerializeField] private ulong shooterClientId;

    private Collider col;

    public void SetParams(float speed, float lifetime, float damage, LayerMask mask, ulong shooterId)
    {
        this.speed = speed;
        this.lifetime = lifetime;
        this.damage = damage;
        this.hitMask = mask;
        this.shooterClientId = shooterId;
    }

    public override void OnNetworkSpawn()
    {
        if (!IsServer) { enabled = false; return; }
        if (!TryGetComponent(out col)) col = gameObject.AddComponent<SphereCollider>();
        col.isTrigger = true;
    }

    private void Update()
    {
        if (!IsServer) return;

        float dt = Time.deltaTime;
        lifetime -= dt;
        if (lifetime <= 0f) { Despawn(); return; }

        transform.position += transform.forward * speed * dt;
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!IsServer) return;

        if ((hitMask.value & (1 << other.gameObject.layer)) == 0)
            return;

        // If we hit a SporeSticky, feed it instead of damaging boss
        var spore = other.GetComponentInParent<SporeSticky>();
        if (spore && spore.IsActiveSpore)
        {
            spore.AbsorbDamage(damage);
            Despawn();
            return;
        }

        // Else damage boss if possible
        var boss = other.GetComponentInParent<IDamageable>();
        if (boss != null && boss.IsAlive)
        {
            boss.TakeDamage(damage);
        }

        Despawn();
    }

    private void Despawn()
    {
        if (NetworkObject && NetworkObject.IsSpawned) NetworkObject.Despawn(true);
        else Destroy(gameObject);
    }
}
