using Unity.Netcode;
using UnityEngine;
using System;
using System.Collections;

/// Scale behavior for the spore as it absorbs damage.
public enum SporeScaleMode { None, Shrink, Grow }

/// Seed flies, sticks to boss, becomes a spore that absorbs damage.
/// On max absorb it explodes: instant AoE damage and spawns a mist (if max reached).
/// If it receives no damage for timeout, it auto-explodes for 1.3x absorbed (no mist).
[RequireComponent(typeof(NetworkObject))]
public class SporeSticky : NetworkBehaviour, IDamageable
{
    [Header("Seed Flight")]
    [SerializeField] private float seedSpeed = 40f;
    [SerializeField] private float seedLifetime = 6f;
    [SerializeField] private LayerMask seedHitMask;

    [Header("Spore Absorb")]
    [SerializeField] private float maxAbsorb = 150f;         // damage required to "full"
    [SerializeField] private float noDamageTimeout = 4f;      // if idle for this, auto-explode (no mist)
    [SerializeField] private SporeScaleMode scaleMode = SporeScaleMode.Shrink;
    [SerializeField] private Vector2 scaleRange = new Vector2(1.2f, 0.4f); // start->end for Shrink, reversed for Grow

    [Header("Explosion")]
    [SerializeField] private float explosionRadius = 6f;
    [SerializeField] private int explosionDamageOnMax = 120;   // when full
    [SerializeField] private float explosionKnockback = 18f;
    [SerializeField] private float explosionUpBias = 0.1f;

    [Header("Auto-Explode (not full)")]
    [SerializeField] private float partialMultiplier = 1.3f;   // damage = absorbed * this
    [SerializeField] private float partialMinDamage = 10f;

    [Header("Mist")]
    [SerializeField] private NetworkObject mistPrefab;          // has SporeMistArea
    [SerializeField] private float mistDuration = 8f;           // seconds alive
    [SerializeField] private bool spawnMistOnlyOnMax = true;

    [Header("Visuals")]
    [SerializeField] private Renderer[] rend;
    [SerializeField] private GameObject spawnVfx;               // optional
    [SerializeField] private GameObject explodeVfx;             // optional

    // runtime
    private bool isSeed = true;
    private float life;
    private float absorbed;
    private float lastDamageTime;
    private Collider col;
    private Rigidbody rb;

    public bool IsAlive => true;            // as a target, it "soaks" until it explodes
    public bool IsActiveSpore => !isSeed;   // used by bullets to decide feeding

    // owner can subscribe: called on server when this spore finishes and despawns
    public event Action<NetworkObject> OnSporeFinished;

    // configure as seed before Spawn
    public void ConfigureAsSeed(float speed, float lifetime, LayerMask hitMask, ulong shooterId)
    {
        seedSpeed = speed;
        seedLifetime = lifetime;
        seedHitMask = hitMask;
        life = lifetime;
        isSeed = true;
    }

    public override void OnNetworkSpawn()
    {
        if (!IsServer) { enabled = false; return; }

        col = GetComponent<Collider>();
        if (!col)
        {
            col = gameObject.AddComponent<SphereCollider>();
            ((SphereCollider)col).radius = 0.35f;
        }
        col.isTrigger = false; // normal collider for seed flight hit

        rb = GetComponent<Rigidbody>();
        if (!rb) rb = gameObject.AddComponent<Rigidbody>();
        rb.useGravity = false;
        rb.isKinematic = false;

        if (isSeed)
        {
            life = seedLifetime;
            if (spawnVfx) Instantiate(spawnVfx, transform.position, transform.rotation);
        }
        else
        {
            BecomeSporeAttached(null, Vector3.zero);
        }
    }

    private void Update()
    {
        if (!IsServer) return;

        if (isSeed)
        {
            float dt = Time.deltaTime;
            life -= dt;
            if (life <= 0f) { ExplodePartial(); return; }
            transform.position += transform.forward * seedSpeed * dt;
        }
        else
        {
            // as spore: check no-damage timeout
            if (Time.time - lastDamageTime >= noDamageTimeout)
            {
                ExplodePartial();
            }
        }
    }

    private void OnCollisionEnter(Collision collision)
    {
        if (!IsServer || !isSeed) return;

        // only stick if we hit one of the seedHitMask layers
        if ((seedHitMask.value & (1 << collision.gameObject.layer)) == 0)
            return;

        // Find a BossHealth to attach to (direct or parent)
        var boss = collision.collider.GetComponentInParent<BossHealth>();
        if (!boss) return; // ignore non-boss for now

        // Attach at contact point
        ContactPoint cp = collision.GetContact(0);
        BecomeSporeAttached(collision.collider.transform, cp.normal);
        transform.position = cp.point + cp.normal * 0.05f; // lift slightly off the surface
    }

    private void BecomeSporeAttached(Transform surface, Vector3 normal)
    {
        isSeed = false;

        // stop physics, become trigger
        if (rb) rb.isKinematic = true;
        if (col) { col.isTrigger = true; }

        // parent to surface so it "sticks"
        if (surface) transform.SetParent(surface, true);

        // init absorb values
        absorbed = 0f;
        lastDamageTime = Time.time;

        // initial scale
        ApplyScale();

        // turn on pooled visuals if off
        SetRenderersEnabled(true);
    }

    private void SetRenderersEnabled(bool on)
    {
        if (rend == null || rend.Length == 0) return;
        for (int i = 0; i < rend.Length; i++)
        {
            if (rend[i]) rend[i].enabled = on;
        }
    }

    // Bullets feed here
    public void AbsorbDamage(float amount)
    {
        if (!IsServer || isSeed) return;

        amount = Mathf.Max(0f, amount);
        absorbed += amount;
        lastDamageTime = Time.time;

        ApplyScale();

        if (absorbed >= maxAbsorb)
        {
            ExplodeFull();
        }
    }

    private void ApplyScale()
    {
        if (scaleMode == SporeScaleMode.None) return;

        float t = Mathf.Clamp01(absorbed / Mathf.Max(0.0001f, maxAbsorb));

        float start = scaleRange.x;
        float end = scaleRange.y;

        float s;
        if (scaleMode == SporeScaleMode.Shrink)
            s = Mathf.Lerp(start, end, t);
        else // Grow
            s = Mathf.Lerp(end, start, t);

        transform.localScale = Vector3.one * s;
    }

    // IDamageable (so other guns can shoot it too)
    public void TakeDamage(float amount)
    {
        AbsorbDamage(Mathf.Abs(amount));
    }

    // ===== Explosions =====

    private void ExplodeFull()
    {
        if (!IsServer) return;

        // instant AoE damage at full
        AoEDamage(explosionDamageOnMax);

        // spawn mist if set
        if (mistPrefab && (!spawnMistOnlyOnMax || absorbed >= maxAbsorb))
        {
            var mistNo = Instantiate(mistPrefab, transform.position, Quaternion.identity);
            var mist = mistNo.GetComponent<SporeMistArea>();
            if (mist) mist.ConfigureLifetime(mistDuration);
            mistNo.Spawn(true);
        }

        if (explodeVfx) Instantiate(explodeVfx, transform.position, Quaternion.identity);

        FinishAndDespawn();
    }

    private void ExplodePartial()
    {
        if (!IsServer) return;

        int dmg = Mathf.Max((int)partialMinDamage, Mathf.RoundToInt(absorbed * partialMultiplier));
        AoEDamage(dmg);

        // no mist for partial
        if (explodeVfx) Instantiate(explodeVfx, transform.position, Quaternion.identity);

        FinishAndDespawn();
    }

    private void AoEDamage(int damage)
    {
        var hits = Physics.OverlapSphere(transform.position, explosionRadius, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < hits.Length; i++)
        {
            var h = hits[i];
            var bh = h.GetComponentInParent<BossHealth>();
            if (bh != null && bh.IsAlive)
            {
                // knockback-like effect is optional; here only instant damage
                bh.ApplyDamage(damage);
            }
        }
    }

    private void FinishAndDespawn()
    {
        OnSporeFinished?.Invoke(NetworkObject);
        if (NetworkObject && NetworkObject.IsSpawned) NetworkObject.Despawn(true);
        else Destroy(gameObject);
    }
}
