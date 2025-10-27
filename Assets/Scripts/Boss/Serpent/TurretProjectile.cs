using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(Rigidbody), typeof(Collider))]
public class TurretProjectile : NetworkBehaviour
{
    [System.Serializable]
    public struct Init
    {
        public float speed;
        public int damage;
        public float lifetime;
    }

    [Header("Hit VFX (client-side)")]
    [SerializeField] private GameObject vfxOnPlayer;
    [SerializeField] private GameObject vfxOnGround;
    [SerializeField] private float vfxDespawnAfter = 2f;

    [Header("Layers")]
    [SerializeField] private LayerMask groundMask; // set to your Ground layer(s)

    private float _speed;
    private int _damage;
    private float _life;
    private float _age;

    private Rigidbody _rb;
    private Collider _col;
    private bool _armed = false;

    public void InitializeServer(Init p)
    {
        _speed = Mathf.Max(0f, p.speed);
        _damage = Mathf.Max(0, p.damage);
        _life = Mathf.Max(0.1f, p.lifetime);
        _age = 0f;

        // armed after a tiny delay to avoid hitting the shooter
        _armed = false;
    }

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _col = GetComponent<Collider>();
        if (_rb)
        {
            _rb.isKinematic = true; // we'll move manually
            _rb.useGravity = false;
        }
        if (_col) _col.isTrigger = true;
    }

    private void Update()
    {
        if (!IsServer) return;

        // manual straight movement
        transform.position += transform.forward * _speed * Time.deltaTime;

        _age += Time.deltaTime;
        if (_age >= 0.04f) _armed = true; // small arming delay
        if (_age >= _life)
        {
            Despawn();
            return;
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!IsServer || !_armed) return;

        // 1) Hit player?
        PlayerHealth ph = other.GetComponentInParent<PlayerHealth>();
        if (ph && ph.IsAlive)
        {
            ph.ApplyDamage(_damage);
            SpawnVfxClientRpc(transform.position, transform.forward, true);
            Despawn();
            return;
        }

        // 2) Hit ground?
        if (((1 << other.gameObject.layer) & groundMask) != 0)
        {
            SpawnVfxClientRpc(transform.position, transform.forward, false);
            Despawn();
            return;
        }
    }

    [ClientRpc]
    private void SpawnVfxClientRpc(Vector3 pos, Vector3 dir, bool hitPlayer)
    {
        GameObject prefab = hitPlayer ? vfxOnPlayer : vfxOnGround;
        if (!prefab) return;

        var go = Instantiate(prefab, pos, Quaternion.LookRotation(dir, Vector3.up));
        Destroy(go, vfxDespawnAfter);
    }

    private void Despawn()
    {
        if (!IsServer) return;
        var no = GetComponent<NetworkObject>();
        if (no && no.IsSpawned) no.Despawn(true);
        else Destroy(gameObject);
    }
}
