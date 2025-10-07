using System.Collections;
using Unity.Netcode;
using UnityEngine;

public abstract class HazardBase : NetworkBehaviour, IDamageable
{
    [Header("Health")]
    [SerializeField] protected int maxHP = 3;
    protected int hp;

    [Header("Charge-up")]
    [SerializeField] protected float chargeDuration = 1.2f;
    [SerializeField] protected float startScale = 0.1f;
    [SerializeField] protected float targetScale = 1f;

    [Header("Visuals")]
    [SerializeField] protected Renderer[] renderers;
    [ColorUsage(false, true)] [SerializeField] protected Color baseColor = Color.white;
    [ColorUsage(false, true)] [SerializeField] protected Color hitColor = new(1f, 0.2f, 0.2f, 1f);
    [SerializeField] protected float flashIn = 0.05f, flashHold = 0.04f, flashOut = 0.18f;

    protected MaterialPropertyBlock[] mpb;
    protected Color[] cachedBase;
    protected Coroutine flashCo;

    protected static readonly int BaseColorID = Shader.PropertyToID("_BaseColor");
    protected static readonly int ColorID = Shader.PropertyToID("_Color");

    // layers
    protected static int PlayerLayer = -1;

    public bool IsAlive => hp > 0;

    private Transform chargeFollowTarget;

    public void SetChargeFollowTarget(Transform t) => chargeFollowTarget = t;

    public override void OnNetworkSpawn()
    {
        // DO NOT early-return on clients; we still need renderer/MPB set up
        if (PlayerLayer < 0) PlayerLayer = LayerMask.NameToLayer("Player");

        if (renderers == null || renderers.Length == 0)
            renderers = GetComponentsInChildren<Renderer>(true);

        // Init MPBs and base colors on ALL peers (server + clients)
        mpb = new MaterialPropertyBlock[renderers.Length];
        cachedBase = new Color[renderers.Length];

        for (int i = 0; i < renderers.Length; i++)
        {
            mpb[i] = new MaterialPropertyBlock();
            var r = renderers[i];
            var mat = r ? r.sharedMaterial : null;

            var baseCol = baseColor;
            if (mat)
            {
                if (mat.HasProperty("_BaseColor")) baseCol = mat.GetColor("_BaseColor");
                else if (mat.HasProperty("_Color")) baseCol = mat.GetColor("_Color");
            }
            cachedBase[i] = baseCol;
            SetRendererColor(i, baseCol);
        }

        // SERVER-ONLY gameplay init
        if (IsServer)
        {
            hp = maxHP;

            // Start charge scaling on server (requires NetworkTransform Sync Scale)
            transform.localScale = Vector3.one * startScale;
            StartCoroutine(ChargeRoutine());

            OnHazardSpawned();
        }
    }


    // called once on server before charge completes (children may use to cache)
    protected virtual void OnHazardSpawned() { }

    protected void BeginCharge()
    {
        StopAllCoroutines();
        StartCoroutine(ChargeRoutine());
    }
    private IEnumerator ChargeRoutine()
    {
        // set initial scale
        transform.localScale = Vector3.one * Mathf.Max(0.0001f, startScale);

        float t = 0f;
        while (t < chargeDuration)
        {
            float k = (chargeDuration <= 0f) ? 1f : (t / chargeDuration);
            float s = Mathf.Lerp(startScale, targetScale, k);
            transform.localScale = Vector3.one * s;

            // Follow the body point while charging (server drives position, replicated via NetworkTransform)
            if (IsServer && chargeFollowTarget)
            {
                transform.position = chargeFollowTarget.position;
                // If you also want to match rotation during charge, uncomment:
                // transform.rotation = chargeFollowTarget.rotation;
            }

            t += Time.deltaTime;
            yield return null;
        }

        transform.localScale = Vector3.one * targetScale;

        // Stop following; from now on child hazard moves under its own logic
        chargeFollowTarget = null;

        OnChargeComplete();
    }

    protected abstract void OnChargeComplete(); // subclasses start their logic here

    protected void SetRendererColor(int idx, Color c)
    {
        var r = renderers[idx];
        if (!r) return;
        var block = mpb[idx];
        r.GetPropertyBlock(block);
        block.SetColor(BaseColorID, c);
        block.SetColor(ColorID, c);
        r.SetPropertyBlock(block);
    }

    protected void SetAllColors(Color c)
    {
        for (int i = 0; i < renderers.Length; i++) SetRendererColor(i, c);
    }

    protected IEnumerator FlashRoutine()
    {
        // up
        float t = 0f;
        while (t < flashIn)
        {
            float f = flashIn > 0f ? t / flashIn : 1f;
            for (int i = 0; i < renderers.Length; i++)
            {
                SetRendererColor(i, Color.Lerp(cachedBase[i], hitColor, f));
            }
            t += Time.deltaTime;
            yield return null;
        }
        SetAllColors(hitColor);

        if (flashHold > 0f) yield return new WaitForSeconds(flashHold);

        // down
        t = 0f;
        while (t < flashOut)
        {
            float f = flashOut > 0f ? 1f - (t / flashOut) : 0f;
            for (int i = 0; i < renderers.Length; i++)
            {
                SetRendererColor(i, Color.Lerp(cachedBase[i], hitColor, 1f - f));
            }
            t += Time.deltaTime;
            yield return null;
        }
        for (int i = 0; i < renderers.Length; i++) SetRendererColor(i, cachedBase[i]);
        flashCo = null;
    }

    public void TakeDamage(float amount)
    {
        if (!IsServer || hp <= 0) return;
        hp -= Mathf.Max(1, Mathf.RoundToInt(Mathf.Abs(amount)));

        // flash clients
        FlashClientRpc();

        if (hp <= 0)
            Die();
    }

    [ClientRpc]
    private void FlashClientRpc()
    {
        if (!isActiveAndEnabled || renderers == null || renderers.Length == 0) return;
        if (flashCo != null) StopCoroutine(flashCo);
        flashCo = StartCoroutine(FlashRoutine());
    }

    protected virtual void Die()
    {
        if (!IsServer) return;
        if (NetworkObject && NetworkObject.IsSpawned) NetworkObject.Despawn(true);
        else Destroy(gameObject);
    }

    // helpers
    protected static bool InMask(int layer, LayerMask mask) => (mask.value & (1 << layer)) != 0;
}
