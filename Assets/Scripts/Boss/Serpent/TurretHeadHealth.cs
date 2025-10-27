using Unity.Netcode;
using UnityEngine;

public class TurretHeadHealth : NetworkBehaviour, IDamageable
{
    [Header("Health")]
    [SerializeField] private float maxHp = 50f;
    private float _hp;
    public bool IsAlive => _hp > 0f;

    [Header("Presentation")]
    [SerializeField] private GameObject explosionVfx;   // spawned on all clients
    [SerializeField] private float vfxLifetime = 2f;
    [SerializeField] private float hideHeadAfter = 0.05f; // small delay so VFX covers the hide

    [SerializeField] private Renderer[] headRenderers; // mesh pieces to hide on death
    [SerializeField] private Collider[] headColliders; // colliders to disable on death

    [Header("Links")]
    [SerializeField] private DeployedTurret parent; // set in prefab; or auto-find

    private bool _notified = false;

    private void Awake()
    {
        _hp = maxHp;
        if (!parent) parent = GetComponentInParent<DeployedTurret>();
        if (headRenderers == null || headRenderers.Length == 0)
            headRenderers = GetComponentsInChildren<Renderer>(true);
        if (headColliders == null || headColliders.Length == 0)
            headColliders = GetComponentsInChildren<Collider>(true);
    }

    public void TakeDamage(float amount)
    {
        if (!IsServer || !IsAlive) return;
        _hp = Mathf.Max(0f, _hp - Mathf.Abs(amount));
        if (_hp <= 0f)
            ServerDie();
        Debug.Log("abc");
    }

    private void ServerDie()
    {
        if (!IsServer) return;
        if (_notified) return;
        _notified = true;

        // Disable collision immediately so it stops receiving hits
        foreach (var c in headColliders) if (c) c.enabled = false;

        // Play explosion for everyone
        SpawnExplosionClientRpc(transform.position);

        // Hide the head meshes shortly after so the VFX masks the pop
        Invoke(nameof(HideHeadMeshes), hideHeadAfter);

        // Tell parent to enter JumpPad state
        if (parent) parent.ServerOnHeadDestroyed();
    }

    private void HideHeadMeshes()
    {
        foreach (var r in headRenderers)
            if (r) r.enabled = false;
    }

    [ClientRpc]
    private void SpawnExplosionClientRpc(Vector3 pos)
    {
        if (!explosionVfx) return;
        var go = Instantiate(explosionVfx, pos, Quaternion.identity);
        Destroy(go, vfxLifetime);
    }
}
