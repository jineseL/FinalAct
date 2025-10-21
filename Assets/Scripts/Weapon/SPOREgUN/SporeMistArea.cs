using Unity.Netcode;
using UnityEngine;
using System.Collections.Generic;

/// Mist that damages boss periodically, and heals players after a short warmup if they stay inside.
[RequireComponent(typeof(NetworkObject))]
public class SporeMistArea : NetworkBehaviour
{
    [Header("Area")]
    [SerializeField] private float radius = 6f;
    [SerializeField] private LayerMask affectMask; // should include Boss and Player layers
    [SerializeField] private float tickInterval = 0.5f;

    [Header("Boss DoT")]
    [SerializeField] private int bossDamagePerTick = 4;

    [Header("Player Heal")]
    [SerializeField] private float healWarmup = 1.0f; // must stay inside this long before heals start
    [SerializeField] private int healPerTick = 2;

    private float lifetime = 8f;
    private float dieAt;

    // track players inside with enter time
    private readonly Dictionary<NetworkObject, float> insideSince = new();

    public void ConfigureLifetime(float seconds)
    {
        lifetime = Mathf.Max(0.1f, seconds);
    }

    public override void OnNetworkSpawn()
    {
        if (!IsServer) { enabled = false; return; }
        dieAt = Time.time + lifetime;
        StartCoroutine(TickRoutine());
    }

    private System.Collections.IEnumerator TickRoutine()
    {
        var wait = new WaitForSeconds(tickInterval);

        while (IsServer)
        {
            if (Time.time >= dieAt) break;

            // AoE overlap
            var hits = Physics.OverlapSphere(transform.position, radius, ~0, QueryTriggerInteraction.Ignore);

            // mark current players inside
            var seen = new HashSet<NetworkObject>();

            for (int i = 0; i < hits.Length; i++)
            {
                var h = hits[i];

                // Boss damage
                var bh = h.GetComponentInParent<BossHealth>();
                if (bh != null && bh.IsAlive)
                {
                    bh.ApplyDamage(bossDamagePerTick);
                }

                // Player heal after warmup
                var no = h.GetComponentInParent<NetworkObject>();
                var ph = h.GetComponentInParent<PlayerHealth>();
                if (no != null && ph != null && ph.IsAlive)
                {
                    seen.Add(no);
                    if (!insideSince.ContainsKey(no)) insideSince[no] = Time.time;

                    if (Time.time - insideSince[no] >= healWarmup)
                    {
                        ph.Heal(healPerTick);
                    }
                }
            }

            // remove players who left
            var toRemove = new List<NetworkObject>();
            foreach (var kv in insideSince)
                if (!seen.Contains(kv.Key))
                    toRemove.Add(kv.Key);
            for (int i = 0; i < toRemove.Count; i++) insideSince.Remove(toRemove[i]);

            yield return wait;
        }

        if (NetworkObject && NetworkObject.IsSpawned) NetworkObject.Despawn(true);
        else Destroy(gameObject);
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0f, 1f, 0.3f, 0.25f);
        Gizmos.DrawSphere(transform.position, radius);
    }
#endif
}
