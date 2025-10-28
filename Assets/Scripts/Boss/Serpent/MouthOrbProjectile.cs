using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(SphereCollider))]
[RequireComponent(typeof(Rigidbody))]
public class MouthOrbProjectile : NetworkBehaviour
{
    [Header("Collision")]
    [SerializeField] private LayerMask explodeOnLayers; // include Ground + Player layers
    [SerializeField] private int playerLayer = 8;       // set to your Player layer index

    [Header("Explosion")]
    [SerializeField] private float explosionRadius = 4f;
    [SerializeField] private int explosionDamage = 25;
    [SerializeField] private float explosionKnockback = 18f;
    [SerializeField] private float explosionUpBias = 0.2f;
    [SerializeField] private GameObject explosionVfx;   // non-networked VFX prefab

    private Transform followMouth;
    private float chargeUntil;
    private bool charging;
    private bool launched;
    private bool dead;
    private float dieAt;

    private Rigidbody rb;
    private SphereCollider col;

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        col = GetComponent<SphereCollider>();

        // physics for trigger collision
        rb.isKinematic = true;
        col.isTrigger = true;
    }

    public override void OnNetworkSpawn()
    {
        // optional: small initial scale; action will set explicitly
    }

    // Called by action on the server
    public void ServerStartCharge(Transform mouth, float chargeTime, float startScale, float endScale)
    {
        if (!IsServer) return;
        followMouth = mouth;
        chargeUntil = Time.time + Mathf.Max(0.01f, chargeTime);
        charging = true;
        launched = false;
        dead = false;

        // apply start scale immediately
        transform.localScale = Vector3.one * Mathf.Max(0.0001f, startScale);

        // kick off scale tween on all clients
        ChargeParamsClientRpc(NetworkObjectId, mouth ? mouth.GetComponent<NetworkObject>()?.NetworkObjectId ?? 0UL : 0UL,
                              startScale, endScale, chargeTime);
    }

    // Called by action on the server
    public void ServerLaunch(Vector3 dir, float speed, float life)
    {
        if (!IsServer || dead) return;
        charging = false;
        launched = true;

        // ensure we align before launch
        if (dir.sqrMagnitude < 0.0001f) dir = transform.forward;
        transform.rotation = Quaternion.LookRotation(dir, Vector3.up);

        dieAt = Time.time + Mathf.Max(0.2f, life);

        // tell clients to begin moving forward
        LaunchClientRpc(dir.normalized, speed, dieAt);
    }

    private void Update()
    {
        if (!IsServer || dead) return;

        if (charging)
        {
            // Always stick to mouth until launch.
            if (followMouth)
            {
                transform.position = followMouth.position;
                transform.rotation = followMouth.rotation;
            }

            // NOTE: Do NOT auto-clear 'charging' when time passes.
            // We keep following until ServerLaunch() is invoked.
            // if (Time.time >= chargeUntil) charging = false;   <-- REMOVE this line
        }
        else if (launched)
        {
            if (Time.time >= dieAt)
                ExplodeAt(transform.position);
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!IsServer || dead) return;

        // Only explode on specified layers
        if (((1 << other.gameObject.layer) & explodeOnLayers) == 0)
            return;

        // ClosestPoint works for triggers and non-triggers
        Vector3 hitPoint = other.ClosestPoint(transform.position);

        // Fallback if something odd returns same position (rare)
        if ((hitPoint - transform.position).sqrMagnitude < 1e-6f)
            hitPoint = transform.position;

        ExplodeAt(hitPoint);
    }

    private void ExplodeAt(Vector3 pos)
    {
        if (!IsServer || dead) return;
        dead = true;

        // damage players in radius
        var hits = Physics.OverlapSphere(pos, explosionRadius, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < hits.Length; i++)
        {
            var h = hits[i];
            if (h.gameObject.layer == playerLayer)
            {
                var ph = h.GetComponentInParent<PlayerHealth>();
                if (ph != null)
                    ph.ApplyDamageFromPoint(explosionDamage, pos, explosionKnockback, explosionUpBias);
            }
        }

        // VFX for everyone at hit position
        PlayExplosionVfxClientRpc(pos);
        NetworkSfxRelay.All_PlayAt("SerpentBoom", pos);

        // Despawn
        if (NetworkObject && NetworkObject.IsSpawned) NetworkObject.Despawn(true);
        else Destroy(gameObject);
    }

    // ===== Client visual helpers =====

    [ClientRpc]
    private void ChargeParamsClientRpc(ulong selfId, ulong mouthNoId, float startScale, float endScale, float chargeTime)
    {
        // simple local tween. Follow is applied on server through transform sync
        StopAllCoroutines();
        StartCoroutine(ScaleTween(startScale, endScale, chargeTime));
    }

    private System.Collections.IEnumerator ScaleTween(float a, float b, float tTime)
    {
        tTime = Mathf.Max(0.01f, tTime);
        float t = 0f;
        while (t < tTime)
        {
            float k = t / tTime;
            float s = Mathf.Lerp(a, b, k);
            transform.localScale = Vector3.one * s;
            t += Time.deltaTime;
            yield return null;
        }
        transform.localScale = Vector3.one * b;
    }

    [ClientRpc]
    private void LaunchClientRpc(Vector3 dir, float speed, float dieAtServerTime)
    {
        StopAllCoroutines();
        StartCoroutine(FlyForward(dir, speed, dieAtServerTime));
    }

    private System.Collections.IEnumerator FlyForward(Vector3 dir, float speed, float dieAtServerTime)
    {
        // non-physics forward flight on clients for visuals; server drives despawn
        while (NetworkManager && NetworkManager.ServerTime.TimeAsFloat < dieAtServerTime && !dead)
        {
            transform.position += dir * speed * Time.deltaTime;
            yield return null;
        }
    }

    [ClientRpc]
    private void PlayExplosionVfxClientRpc(Vector3 pos)
    {
        if (explosionVfx)
        {
            var v = Instantiate(explosionVfx, pos, Quaternion.identity);
            Destroy(v, 6f);
        }
    }
    // === Gizmo settings ===
    [SerializeField] private bool showExplosionGizmo = true;
    [SerializeField] private Color gizmoWireColor = new Color(1f, 0.35f, 0.05f, 1f);
    [SerializeField] private Color gizmoFillColor = new Color(1f, 0.35f, 0.05f, 0.08f);

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (!showExplosionGizmo) return;

        Vector3 pos = transform.position;

        // Wire outline
        Gizmos.color = gizmoWireColor;
        Gizmos.DrawWireSphere(pos, explosionRadius);

        // Cheap “fill” by drawing a few inner wire spheres
        Gizmos.color = gizmoFillColor;
        Gizmos.DrawWireSphere(pos, explosionRadius * 0.8f);
        Gizmos.DrawWireSphere(pos, explosionRadius * 0.6f);
        Gizmos.DrawWireSphere(pos, explosionRadius * 0.4f);

        // Direction hint (forward)
        Gizmos.color = Color.cyan;
        Gizmos.DrawRay(pos, transform.forward * Mathf.Min(2f, explosionRadius));
    }
#endif

    private void OnValidate()
    {
        // Keep radius sane while editing
        if (explosionRadius < 0f) explosionRadius = 0f;
    }
}
