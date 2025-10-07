using Unity.Netcode;
using UnityEngine;

/// Simple continuous-homing missile:
/// - Always moves forward at constant speed
/// - Rotates toward target position with capped turn rate
/// - Explodes on Player/Ground hit or lifetime expiry
/// - 1-hit destroy (IDamageable)
[RequireComponent(typeof(NetworkObject))]
public class SnakeHomingMissile : NetworkBehaviour, IDamageable
{
    public struct Config
    {
        public NetworkObject targetRef;      // player to home on (current position each frame)
        public float speed;                  // constant forward speed
        public float rotateDegPerSec;        // turn speed cap

        public float maxLifetime;            // auto-explode after

        public float explosionRadius;
        public int explosionDamage;
        public float explosionKnockback;
        public float explosionUpBias;
        public LayerMask explodeOnLayers;

        public int hitPoints;                // default 1
    }

    [Header("missiles setting")]
    [SerializeField] private float initialSpeed = 10f;
    [SerializeField] private float speedDecayDuration; //duration for speed tor initial speed to reach target speed
    [SerializeField] private float targetSpeed = 10f;
    [SerializeField] private float speed = 10f;
    [SerializeField] private float rotateDegPerSec = 90f;
    [SerializeField] private float lifeRemaining = 8f;
    [SerializeField] private float forwardRemaining = 8f;
    [SerializeField] private int hp = 1;
    private float speedDecayInitial;
    private float speedDecay;

    private Config cfg;
    private bool configured;

    private NetworkObject targetNO;
    private static int PlayerLayer = -1;
    private Collider col;

    private bool dead = false;

    public bool IsAlive => !dead && hp > 0;

    [SerializeField] private GameObject explosionVfxPrefab; // non-networked VFX prefab
    [SerializeField] private float explosionVfxLifetime = 3f;
    [SerializeField] private AudioClip explosionSfx;
    [SerializeField, Range(0f, 1f)] private float explosionSfxVolume = 0.9f;

    public void ApplyConfig(Config c)
    {
        cfg = c;
        configured = true;

        targetNO = cfg.targetRef;
        speed = cfg.speed;
        rotateDegPerSec = cfg.rotateDegPerSec;
        lifeRemaining = Mathf.Max(0.5f, cfg.maxLifetime);

        hp = Mathf.Max(1, cfg.hitPoints);
    }

    public override void OnNetworkSpawn()
    {
        if (!IsServer) { enabled = false; return; }

        if (col == null) col = GetComponent<Collider>();
        if (col) col.isTrigger = true;

        if (PlayerLayer < 0) PlayerLayer = LayerMask.NameToLayer("Player");

        if (!configured)
        {
            // fallbacks if not set by action
            speed = initialSpeed;
            rotateDegPerSec = 90f;
            lifeRemaining = 8f;
            hp = 1;
            cfg.explosionRadius = 2.5f;
            cfg.explosionDamage = 15;
            cfg.explosionKnockback = 16f;
            cfg.explosionUpBias = 0.1f;
            cfg.explodeOnLayers = ~0;
        }
        speedDecayInitial = speedDecayDuration;
    }

    public void Activate()
    {
        if (!IsServer) return;
        // nothing else; Update drives motion
    }

    private void Update()
    {
        if (!IsServer || dead) return;

        // lifetime
        lifeRemaining -= Time.deltaTime;
        if (lifeRemaining <= 0f) { Explode(); return; }

        if (speedDecayDuration > 0)
        {
            speedDecayDuration -= Time.deltaTime;
            speedDecay = Mathf.Clamp01(speedDecayDuration / speedDecayInitial);
            
        }
        //launch speed
        speed = Mathf.Lerp( targetSpeed, initialSpeed, speedDecay);
        forwardRemaining -= Time.deltaTime;
        if (forwardRemaining <= 0f) 
        {
            //moveforward only
            Vector3 desiredDirF = transform.forward; // default: keep going
            if (targetNO && targetNO.IsSpawned)
            {
                Vector3 to = targetNO.transform.position - transform.position;
                if (to.sqrMagnitude > 0.0001f) desiredDirF = to.normalized;
            }

            // move forward
            transform.position += transform.forward * speed * Time.deltaTime;
            return;
        }

        // homing: compute desired direction toward current target pos
        Vector3 desiredDir = transform.forward; // default: keep going
        if (targetNO && targetNO.IsSpawned)
        {
            Vector3 to = targetNO.transform.position - transform.position;
            if (to.sqrMagnitude > 0.0001f) desiredDir = to.normalized;
        }

        // rotate toward desired with capped turn rate
        Quaternion desiredRot = Quaternion.LookRotation(desiredDir, Vector3.up);
        transform.rotation = Quaternion.RotateTowards(transform.rotation, desiredRot, rotateDegPerSec * Time.deltaTime);

        // move forward
        transform.position += transform.forward * speed * Time.deltaTime;
    }

    private static bool InMask(int layer, LayerMask mask) => (mask.value & (1 << layer)) != 0;

    private void OnTriggerEnter(Collider other)
    {
        if (!IsServer || dead) return;
        if (InMask(other.gameObject.layer, cfg.explodeOnLayers))
            Explode();
    }

    private void Explode()
    {
        if (!IsServer || dead) return;
        dead = true;

        // damage players in radius (unchanged) ...
        var hits = Physics.OverlapSphere(transform.position, cfg.explosionRadius, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < hits.Length; i++)
        {
            var h = hits[i];
            if (h.gameObject.layer == PlayerLayer)
            {
                var ph = h.GetComponentInParent<PlayerHealth>();
                if (ph != null)
                    ph.ApplyDamageFromPoint(cfg.explosionDamage, transform.position, cfg.explosionKnockback, cfg.explosionUpBias);
            }
        }

        // VFX/SFX for everyone (host + clients)
        SpawnExplosionVfxClientRpc(transform.position, Quaternion.identity);
        NetworkSfxRelay.All_PlayAt("MissileExplode", transform.position,0.6f);

        // Despawn missile
        if (NetworkObject && NetworkObject.IsSpawned) NetworkObject.Despawn(true);
        else Destroy(gameObject);
    }


    [ClientRpc]
    private void SpawnExplosionVfxClientRpc(Vector3 pos, Quaternion rot)
    {
        // VFX
        if (explosionVfxPrefab)
        {
            var vfx = Object.Instantiate(explosionVfxPrefab, pos, rot);
            if (explosionVfxLifetime > 0f) Object.Destroy(vfx, explosionVfxLifetime);

            // If your VFX prefab already has an AudioSource with its own clip,
            // you can skip this block. Otherwise play a one-shot here:
            if (explosionSfx)
            {
                // Try to use an AudioSource on the VFX first (so it follows its lifetime)
                var src = vfx.GetComponent<AudioSource>();
                if (src) src.PlayOneShot(explosionSfx, explosionSfxVolume);
                else AudioSource.PlayClipAtPoint(explosionSfx, pos, explosionSfxVolume);
            }
        }
        else if (explosionSfx)
        {
            // SFX only (no VFX prefab assigned)
            AudioSource.PlayClipAtPoint(explosionSfx, pos, explosionSfxVolume);
        }
    }


    // IDamageable
    public void TakeDamage(float amount)
    {
        if (!IsServer || dead) return;
        hp -= Mathf.Max(1, Mathf.RoundToInt(Mathf.Abs(amount)));
        if (hp <= 0) Explode();
    }
}
