using UnityEngine;
using Unity.Netcode;
public class NetworkedProp : NetworkBehaviour
{
    private Rigidbody rb;

    [Header("Tuning")]
    public float maxImpulse = 50f;          // clamp extremes
    public float maxContinuousSpeed = 20f;  // clamp velocity

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
    }

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            rb.isKinematic = false; // server simulates physics
        }
        else
        {
            rb.isKinematic = true;  // clients only receive transforms
        }
    }

    // Server-only impulse (blast, slam, etc.)
    public void ApplyImpulse(Vector3 force, Vector3 point)
    {
        if (!IsServer) return;

        // clamp
        if (force.sqrMagnitude > maxImpulse * maxImpulse)
            force = force.normalized * maxImpulse;

        // apply impulse at contact point (adds linear + angular)
        rb.AddForceAtPosition(force, point, ForceMode.Impulse);


        // guardrails
        if (rb.linearVelocity.magnitude > maxContinuousSpeed)
            rb.linearVelocity = rb.linearVelocity.normalized * maxContinuousSpeed;
    }

    // Client asks server to nudge (for "walking into it" pushes)
    [ServerRpc(RequireOwnership = false)]
    public void RequestNudgeServerRpc(Vector3 push, Vector3 point)
    {
        ApplyImpulse(push, point);
    }
}