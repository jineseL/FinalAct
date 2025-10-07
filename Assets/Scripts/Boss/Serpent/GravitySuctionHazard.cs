using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// Moves to a destination (between players or toward arena center),
/// then continuously pulls players inward until destroyed.
public class GravitySuctionHazard : HazardBase
{
    [Header("Movement")]
    [SerializeField] private float travelSpeed = 8f;
    [SerializeField] private float arriveDistance = 0.5f;

    [Header("Suction Radius")]
    [SerializeField] private float effectRadius = 12f;

    [Header("Persistent Pull (works with PlayerMotor.SetPersistentExternalVelocity)")]
    [SerializeField] private float pullMinVelocity = 2f;   // desired speed at edge of radius
    [SerializeField] private float pullMaxVelocity = 8f;   // desired speed near center
    [SerializeField] private float pullCurvePower = 2f;    // >1 softens spike near center; 1 = linear
    [SerializeField] private float distanceCapFactor = 2f; // vel <= dist * factor, prevents overshoot

    [Header("Slow")]
    [SerializeField] private float slowFactor = 0.7f;
    [SerializeField] private float slowRefresh = 0.25f;
    [SerializeField] private float slowRpcInterval = 0.25f; // send slow refresh at most 4x/sec

    [Header("Touch Damage/KB")]
    [SerializeField] private int touchDamage = 15;
    [SerializeField] private float touchKnockback = 18f;
    [SerializeField] private float touchUpBias = 0.12f;

    private Vector3 destination;
    private bool pulling = false;

    // Throttle slow RPCs and track who is in/out of range this frame
    private readonly Dictionary<ulong, float> _nextSlowSend = new();
    private readonly HashSet<ulong> _inRangePrev = new();
    private readonly HashSet<ulong> _inRangeNow = new();

    // Optional: spawner can set destination; otherwise we compute midpoint
    public void SetDestination(Vector3 worldPos) => destination = worldPos;

    protected override void OnChargeComplete()
    {
        // If no destination set, compute it
        if (destination == Vector3.zero)
        {
            var nm = NetworkManager.Singleton;
            Vector3? a = null, b = null;
            foreach (var kv in nm.ConnectedClients)
            {
                var po = kv.Value.PlayerObject;
                if (!po || !po.IsSpawned) continue;
                if (a == null) a = po.transform.position;
                else if (b == null) { b = po.transform.position; break; }
            }

            if (a != null && b != null)
                destination = Vector3.Lerp(a.Value, b.Value, 0.5f);
            else if (a != null)
                destination = a.Value; // single player
            else
                destination = transform.position; // fallback
        }
    }

    private void Update()
    {
        if (!IsServer || hp <= 0) return;

        if (!pulling)
        {
            Vector3 to = destination - transform.position;
            if (to.sqrMagnitude > arriveDistance * arriveDistance)
            {
                transform.position += to.normalized * travelSpeed * Time.deltaTime;
            }
            else
            {
                pulling = true;
            }
        }
        else
        {
            PullPlayers();
        }
    }

    private void PullPlayers()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null) return;

        Vector3 center = transform.position;
        float now = Time.time;

        _inRangeNow.Clear();

        foreach (var kv in nm.ConnectedClients)
        {
            var po = kv.Value.PlayerObject;
            if (!po || !po.IsSpawned) continue;

            var motor = po.GetComponent<PlayerMotor>();
            var ctrl = po.GetComponent<CharacterController>();
            if (!motor) continue;

            Vector3 p = ctrl ? ctrl.transform.TransformPoint(ctrl.center) : po.transform.position;
            Vector3 toC = center - p;
            float dist = toC.magnitude;

            if (dist <= effectRadius)
            {
                _inRangeNow.Add(kv.Key);

                // 0 at edge, 1 near center
                float t = 1f - Mathf.Clamp01(dist / Mathf.Max(0.001f, effectRadius));

                // Shape to avoid huge spike near the center
                float shaped = Mathf.Pow(t, Mathf.Max(1f, pullCurvePower));

                // Desired persistent speed, capped by distance
                float velMag = Mathf.Lerp(pullMinVelocity, pullMaxVelocity, shaped);
                velMag = Mathf.Min(velMag, dist * distanceCapFactor);

                Vector3 desiredVel = (dist > 0.001f) ? toC.normalized * velMag : Vector3.zero;

                // Tell that client to SET its persistent velocity this frame (no stacking)
                SetPullVelocityClientRpc(desiredVel, new ClientRpcParams
                {
                    Send = new ClientRpcSendParams { TargetClientIds = new[] { kv.Key } }
                });

                // Throttle slow application
                if (!_nextSlowSend.TryGetValue(kv.Key, out var next) || now >= next)
                {
                    ApplySlowClientRpc(slowFactor, slowRefresh * 1.1f, new ClientRpcParams
                    {
                        Send = new ClientRpcSendParams { TargetClientIds = new[] { kv.Key } }
                    });
                    _nextSlowSend[kv.Key] = now + Mathf.Max(0.05f, slowRpcInterval);
                }
            }
        }

        // Anyone who was in range last frame but not this frame: clear their pull immediately
        foreach (var id in _inRangePrev)
        {
            if (!_inRangeNow.Contains(id))
            {
                ClearPullFor(id);
            }
        }

        // Swap sets: prev = now
        _inRangePrev.Clear();
        foreach (var id in _inRangeNow) _inRangePrev.Add(id);
    }

    private void ClearPullFor(ulong clientId)
    {
        var p = new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } }
        };
        ClearPullClientRpc(p);
        _nextSlowSend.Remove(clientId);
    }

    // Touch damage/knockback on trigger
    private void OnTriggerEnter(Collider other)
    {
        if (!IsServer || hp <= 0) return;
        if (other.gameObject.layer != PlayerLayer) return;

        var ph = other.GetComponentInParent<PlayerHealth>();
        if (!ph) return;

        Vector3 hitPoint = other.ClosestPoint(transform.position);
        ph.ApplyDamageFromHitPoint(touchDamage, hitPoint, touchKnockback, touchUpBias);
    }

    protected override void Die()
    {
        // Clear all tracked pulls on death
        foreach (var id in _inRangePrev) ClearPullFor(id);
        _inRangePrev.Clear();
        _inRangeNow.Clear();

        base.Die();
    }

    public override void OnNetworkDespawn()
    {
        // Clear all tracked pulls on despawn
        foreach (var id in _inRangePrev) ClearPullFor(id);
        _inRangePrev.Clear();
        _inRangeNow.Clear();

        base.OnNetworkDespawn();
    }

    // ===== Client RPCs to control PlayerMotor persistent velocity =====

    [ClientRpc]
    private void SetPullVelocityClientRpc(Vector3 desiredVel, ClientRpcParams p = default)
    {
        var po = NetworkManager.Singleton.LocalClient?.PlayerObject;
        if (!po) return;
        var motor = po.GetComponent<PlayerMotor>();
        if (motor != null) motor.SetPersistentExternalVelocity(desiredVel);
    }

    [ClientRpc]
    private void ClearPullClientRpc(ClientRpcParams p = default)
    {
        var po = NetworkManager.Singleton.LocalClient?.PlayerObject;
        if (!po) return;
        var motor = po.GetComponent<PlayerMotor>();
        if (motor != null) motor.ClearPersistentExternalVelocity();
    }

    [ClientRpc]
    private void ApplySlowClientRpc(float factor, float duration, ClientRpcParams p = default)
    {
        var po = NetworkManager.Singleton.LocalClient?.PlayerObject;
        if (!po) return;
        var motor = po.GetComponent<PlayerMotor>();
        if (motor) motor.ApplySlow(factor, duration);
    }
}
