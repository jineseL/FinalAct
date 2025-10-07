using Unity.Netcode;
using UnityEngine;

/// Slow continuous homing toward a chosen target player.
/// Damages + knocks back on touch.
public class HomingCubeHazard : HazardBase
{
    [Header("Homing")]
    [SerializeField] private float moveSpeed = 5f;
    [SerializeField] private float rotateDegPerSec = 90f;

    [Header("Touch Damage")]
    [SerializeField] private int touchDamage = 15;
    [SerializeField] private float knockback = 18f;
    [SerializeField] private float knockUpBias = 0.1f;

    //[Header("Targeting")]
    //[SerializeField] private bool targetClosestOnSpawn = true;

    private NetworkObject targetNo;

    // Optional: called by spawner to set a preferred target up-front
    public void SetTarget(NetworkObject no) => targetNo = no;

    protected override void OnHazardSpawned()
    {
        // randomize initial rotation just for looks
        transform.rotation = Random.rotationUniform;
    }

    protected override void OnChargeComplete()
    {
        // choose closest if nothing assigned
        if (!targetNo || !targetNo.IsSpawned)
        {
            var nm = NetworkManager.Singleton;
            float best = float.PositiveInfinity;
            foreach (var kv in nm.ConnectedClients)
            {
                var po = kv.Value.PlayerObject;
                if (!po || !po.IsSpawned) continue;
                float sq = (po.transform.position - transform.position).sqrMagnitude;
                if (sq < best) { best = sq; targetNo = po; }
            }
        }
    }

    private void Update()
    {
        if (!IsServer || hp <= 0) return;
        if (targetNo && targetNo.IsSpawned)
        {
            Vector3 to = (targetNo.transform.position - transform.position);
            Vector3 desiredDir = (to.sqrMagnitude > 0.0001f) ? to.normalized : transform.forward;
            Quaternion targetRot = Quaternion.LookRotation(desiredDir, Vector3.up);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRot, rotateDegPerSec * Time.deltaTime);
        }

        transform.position += transform.forward * moveSpeed * Time.deltaTime;
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!IsServer || hp <= 0) return;
        if (other.gameObject.layer != PlayerLayer) return;

        var ph = other.GetComponentInParent<PlayerHealth>();
        if (ph)
        {
            ph.ApplyDamageFromHitPoint(touchDamage, other.ClosestPoint(transform.position), knockback, knockUpBias);
        }
    }
}
