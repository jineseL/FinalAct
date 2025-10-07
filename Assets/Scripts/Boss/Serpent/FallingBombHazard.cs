using Unity.Netcode;
using UnityEngine;

/// Drops straight down and explodes on ground contact (or timeout).
public class FallingBombHazard : HazardBase
{
    [Header("Fall")]
    [SerializeField] private float fallSpeed = 6f;
    [SerializeField] private float maxLifetime = 10f;

    [Header("Explosion")]
    [SerializeField] private float explosionRadius = 5f;
    [SerializeField] private int explosionDamage = 20;
    [SerializeField] private float knockback = 22f;
    [SerializeField] private float upBias = 0.15f;
    [SerializeField] private LayerMask explodeOnLayers; // include Ground and Player

    [Header("VFX")]
    [SerializeField] private GameObject explosionVfx;
    [SerializeField] private float vfxLifetime = 3f;

    private float life;

    protected override void OnChargeComplete()
    {
        life = maxLifetime;
    }

    private void Update()
    {
        if (!IsServer || hp <= 0) return;
        // fall
        transform.position += Vector3.down * fallSpeed * Time.deltaTime;

        life -= Time.deltaTime;
        if (life <= 0f)
        {
            Explode();
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!IsServer || hp <= 0) return;
        if (InMask(other.gameObject.layer, explodeOnLayers))
        {
            Explode();
        }
    }

    private void Explode()
    {
        if (!IsServer || hp <= 0) return;
        hp = 0;

        // damage players
        var hits = Physics.OverlapSphere(transform.position, explosionRadius, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < hits.Length; i++)
        {
            var h = hits[i];
            if (h.gameObject.layer == PlayerLayer)
            {
                var ph = h.GetComponentInParent<PlayerHealth>();
                if (ph != null)
                    ph.ApplyDamageFromPoint(explosionDamage, transform.position, knockback, upBias);
            }
        }

        // VFX on clients
        if (explosionVfx) SpawnVfxClientRpc(transform.position);

        Die();
    }

    [ClientRpc]
    private void SpawnVfxClientRpc(Vector3 pos)
    {
        if (!explosionVfx) return;
        var go = Instantiate(explosionVfx, pos, Quaternion.identity);
        if (vfxLifetime > 0f) Destroy(go, vfxLifetime);
    }
}
