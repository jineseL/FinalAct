using System.Collections;
using UnityEngine;
using Unity.Netcode;

public class SnakeHitbox : NetworkBehaviour, IDamageable
{
    [Header("Health Routing")]
    [SerializeField] private BossHealth bossHealth;         // shared health
    [SerializeField] private float damageMultiplier = 1f;

    [Header("Hit Feedback")]
    [SerializeField] private ParticleSystem hitVfxPrefab;   // optional
    [SerializeField] private Renderer[] renderersToFlash;
    [SerializeField] private Color flashColor = Color.white;
    [SerializeField] private float flashTime = 0.08f;

    [Header("Ground Contact VFX")]
    [SerializeField] private LayerMask groundLayers;        // set in Inspector
    [SerializeField] private ParticleSystem groundVfxPrefab;
    [SerializeField] private float groundFxMinInterval = 0.15f; // spam guard

    [Header("Player Contact Damage")]
    [SerializeField] LayerMask playerLayers;
    [SerializeField] int contactDamage = 15;
    [SerializeField] float contactKnockback = 12f;
    [SerializeField] float contactKnockUp = 2f;

    public bool IsAlive => bossHealth != null && bossHealth.IsAlive;

    Collider _col;
    MaterialPropertyBlock _mpb;
    Color[] _baseColors;

    // track “inside ground” so we only spawn once per pass-through
    int _groundContacts;
    float _lastGroundFxTime;

    void Awake()
    {
        if (!bossHealth) bossHealth = GetComponentInParent<BossHealth>();

        _col = GetComponent<Collider>();
        if (_col) _col.isTrigger = true; // weapons use trigger hits

        if (renderersToFlash == null || renderersToFlash.Length == 0)
            renderersToFlash = GetComponentsInChildren<Renderer>();

        _mpb = new MaterialPropertyBlock();
        CacheBaseColors();
    }

    void CacheBaseColors()
    {
        if (renderersToFlash == null) return;
        _baseColors = new Color[renderersToFlash.Length];
        for (int i = 0; i < renderersToFlash.Length; i++)
        {
            var r = renderersToFlash[i];
            if (!r) continue;
            r.GetPropertyBlock(_mpb);
            _baseColors[i] = _mpb.GetColor("_BaseColor");
            if (_baseColors[i] == default && r.sharedMaterial && r.sharedMaterial.HasProperty("_Color"))
                _baseColors[i] = r.sharedMaterial.color;
            if (_baseColors[i] == default) _baseColors[i] = Color.white;
        }
    }

    // ----------------- Damage -----------------
    public void TakeDamage(float amount)
    {
        Vector3 hitPoint = _col ? _col.bounds.center : transform.position;
        TakeDamageAt(amount, hitPoint);
    }

    public void TakeDamageAt(float amount, Vector3 hitPoint)
    {
        if (!IsServer) return;
        if (!IsAlive) return;

        bossHealth.ApplyDamage(Mathf.Abs(amount) * damageMultiplier);
        PlayHitFxClientRpc(hitPoint);
    }

    [ClientRpc]
    void PlayHitFxClientRpc(Vector3 hitPoint)
    {
        if (hitVfxPrefab)
        {
            var vfx = Instantiate(hitVfxPrefab, hitPoint, Quaternion.identity);
            vfx.Play();
            Destroy(vfx.gameObject, 2f);
        }
        if (isActiveAndEnabled && renderersToFlash != null && renderersToFlash.Length > 0)
            StartCoroutine(FlashRoutine());
    }

    IEnumerator FlashRoutine()
    {
        for (int i = 0; i < renderersToFlash.Length; i++)
        {
            var r = renderersToFlash[i];
            if (!r) continue;
            r.GetPropertyBlock(_mpb);
            if (r.sharedMaterial && r.sharedMaterial.HasProperty("_BaseColor"))
                _mpb.SetColor("_BaseColor", flashColor);
            else if (r.sharedMaterial && r.sharedMaterial.HasProperty("_Color"))
                _mpb.SetColor("_Color", flashColor);
            r.SetPropertyBlock(_mpb);
        }
        yield return new WaitForSeconds(flashTime);
        for (int i = 0; i < renderersToFlash.Length; i++)
        {
            var r = renderersToFlash[i];
            if (!r) continue;
            r.GetPropertyBlock(_mpb);
            var baseCol = (i < _baseColors.Length) ? _baseColors[i] : Color.white;
            if (r.sharedMaterial && r.sharedMaterial.HasProperty("_BaseColor"))
                _mpb.SetColor("_BaseColor", baseCol);
            else if (r.sharedMaterial && r.sharedMaterial.HasProperty("_Color"))
                _mpb.SetColor("_Color", baseCol);
            r.SetPropertyBlock(_mpb);
        }
    }

    // ----------------- Ground contact VFX -----------------
    void OnTriggerEnter(Collider other)
    {
        if (!IsServer) return;

        int otherLayer = other.gameObject.layer;

        // ------------ GROUND FX ------------
        if (IsInLayerMask(otherLayer, groundLayers))
        {
            _groundContacts++;
            if (_groundContacts == 1 && Time.time - _lastGroundFxTime > groundFxMinInterval)
            {
                _lastGroundFxTime = Time.time;

                // try to find a nice point on the ground
                Vector3 pos = other.ClosestPoint(_col.bounds.center);
                Vector3 normal = Vector3.up;
                if (Physics.Raycast(_col.bounds.center + Vector3.up * 0.5f, Vector3.down,
                    out RaycastHit hit, 5f, groundLayers, QueryTriggerInteraction.Ignore))
                {
                    pos = hit.point;
                    normal = hit.normal;
                }
                PlayGroundFxClientRpc(pos, normal);
            }
            return;
        }

        // ------------ PLAYER DAMAGE ------------
        if (IsInLayerMask(otherLayer, playerLayers))
        {
            var hp = other.GetComponentInParent<PlayerHealth>();
            if (hp != null && hp.IsAlive) // your PlayerHealth has IsAlive
            {
                // knock player away from the boss segment
                Vector3 fromBossToPlayer = (other.bounds.center - (_col ? _col.bounds.center : transform.position)).normalized;
                Vector3 knock = fromBossToPlayer * contactKnockback + Vector3.up * contactKnockUp;

                // attackerClientId arg is optional; send ours if you want
                ulong attacker = NetworkObject ? NetworkObject.OwnerClientId : ulong.MaxValue;
                hp.ApplyDamage(contactDamage, knock, attacker);
            }
            return;
        }
    }

    void OnTriggerExit(Collider other)
    {
        if (!IsServer) return;
        if (!IsInLayerMask(other.gameObject.layer, groundLayers)) return;

        _groundContacts = Mathf.Max(0, _groundContacts - 1);
    }

    [ClientRpc]
    void PlayGroundFxClientRpc(Vector3 pos, Vector3 normal)
    {
        if (!groundVfxPrefab) return;
        var rot = Quaternion.FromToRotation(Vector3.up, normal); // align to surface
        var vfx = Instantiate(groundVfxPrefab, pos, rot);
        vfx.Play();
        Destroy(vfx.gameObject, 2f);
    }

    static bool IsInLayerMask(int layer, LayerMask mask)
        => (mask.value & (1 << layer)) != 0;
}
