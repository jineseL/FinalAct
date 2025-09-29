using UnityEngine;
using Unity.Netcode;
using System.Collections.Generic;
public class BlastAoE : NetworkBehaviour
{
    [Header("Assign in scene/startup")]
    public static GameObject ServerPrefab; // assign somewhere central at runtime

    [Header("Runtime")]
    [SerializeField] private SphereCollider triggerCol;

    // Config from spawner:
    private NetworkObjectReference shooterRef;
    private float radius;
    private float life;
    private float playerForce;
    private float enemyDamage;

    private HashSet<ulong> affected = new(); // prevent double application

    private NetworkObject shooter; // resolved on server in Activate
    private static int PlayerLayer;
    private static int PropLayer;
    private static int EnemyLayer;

    public void Configure(NetworkObject shooterNo, float r, float l, float pForce, float dmg)
    {
        shooterRef = shooterNo;
        radius = r;
        life = l;
        playerForce = pForce;
        enemyDamage = dmg;
    }
    private void Awake()
    {
        // Cache layer index once per prefab instance
        PlayerLayer = LayerMask.NameToLayer("Player");
        PropLayer = LayerMask.NameToLayer("Prop");
        EnemyLayer = LayerMask.NameToLayer("Enemy");
    }

    public void Activate()
    {
        if (!IsServer) return;

        if (triggerCol == null) triggerCol = GetComponent<SphereCollider>();
        triggerCol.isTrigger = true;
        triggerCol.radius = radius;

        // resolve shooter
        shooterRef.TryGet(out shooter);

        // auto-despawn after short life
        Invoke(nameof(DespawnSelf), life);
    }

    private void DespawnSelf()
    {
        if (IsServer && this != null && NetworkObject != null && NetworkObject.IsSpawned)
            NetworkObject.Despawn(true);
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!IsServer) return;

        var nob = other.GetComponentInParent<NetworkObject>();
        if (nob == null) return;

        // ignore shooter
        if (shooter != null && nob.NetworkObjectId == shooter.NetworkObjectId) return;
        if (affected.Contains(nob.NetworkObjectId)) return;
        affected.Add(nob.NetworkObjectId);

        // Players
        if (other.gameObject.layer == PlayerLayer)
        {
            var motor = other.GetComponentInParent<PlayerMotor>();
            if (motor != null)
            {
                Vector3 dir = (other.transform.position - transform.position).normalized;
                Vector3 force = dir * playerForce;

                motor.ApplyExternalForce(force);

                var clientRpcParams = new ClientRpcParams
                {
                    Send = new ClientRpcSendParams { TargetClientIds = new[] { nob.OwnerClientId } }
                };
                ApplyKnockbackClientRpc(force, clientRpcParams);
            }
            return;
        }

        // Props
        if (other.gameObject.layer == PropLayer)
        {
            var prop = other.GetComponent<NetworkedProp>();
            if (prop != null)
            {
                Vector3 dir = (other.ClosestPoint(transform.position) - transform.position).normalized;
                if (dir.sqrMagnitude < 0.001f) dir = (other.transform.position - transform.position).normalized;

                float propImpulse = playerForce * 0.8f; // tweak separate from playerForce if you want
                Vector3 impulse = dir * propImpulse;

                Vector3 hitPoint = other.ClosestPoint(transform.position);
                prop.ApplyImpulse(impulse, hitPoint);
            }
            return;
        }

        // Enemies: damage only (no knockback)
        if (other.gameObject.layer == EnemyLayer)
        {
            
            var dmg = other.GetComponentInParent<IDamageable>();
            if (dmg != null && dmg.IsAlive)
            {
                
                dmg.TakeDamage(enemyDamage);
                return;
            }
        }

        // anything else: ignore or custom behavior
    }

    [ClientRpc]
    private void KnockbackClientRpc(ulong targetClientId)
    {
        // If you want to play local-only feedback for the knocked player
        if (NetworkManager.Singleton.LocalClientId == targetClientId)
        {
            // camera shake / hit effect, etc.
        }
    }
    [ClientRpc]
    private void ApplyKnockbackClientRpc(Vector3 force, ClientRpcParams clientRpcParams = default)
    {
        // Runs ONLY on the target client (because of ClientRpcParams)
        var playerObj = NetworkManager.Singleton.LocalClient?.PlayerObject;
        if (playerObj == null) return;

        var motor = playerObj.GetComponentInChildren<PlayerMotor>();
        if (motor != null)
            motor.ApplyExternalForce(force);
    }

    /*#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (triggerCol == null) triggerCol = GetComponent<SphereCollider>();
            Gizmos.color = new Color(1, 0.4f, 0.1f, 0.6f);
            Gizmos.DrawWireSphere(transform.position, triggerCol != null ? triggerCol.radius : 0.5f);
        }
    #endif*/
}
