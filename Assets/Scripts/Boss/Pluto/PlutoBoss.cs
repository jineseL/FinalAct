using Unity.Netcode;
using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

public class PlutoBoss : NetworkBehaviour, IDamageable
{
    [Header("References")]
    [SerializeField] private Transform model;        // optional visual root; if null, rotate self
    [SerializeField] public Transform core;         // optional hit point
    [SerializeField] private BossHealth bossHealth;  // assign on prefab

    [Header("Hover")]
    [SerializeField] private bool hoverOnStart = true;
    [SerializeField] private float hoverAmplitude = 0.8f;   // meters
    [SerializeField] private float hoverSpeed = 2.2f;       // radians per second

    [Header("Look At Target")]
    [SerializeField] private float lookTurnSpeed = 90f;     // degrees per second
    //[SerializeField] private bool yawOnly = true;           // face horizontally

    [Header("AI Tick")]
    [SerializeField] private float thinkInterval = 5f;

    [Header("Planets")]
    [SerializeField] private Transform planetsRoot;                 // assign your empty holder GO here
    [SerializeField] private List<OrbActionBase> planetActions = new(); // optional manual list

    [Header("Utility")]
    [SerializeField] private PlutoUtilityAi utilityAi;

    //public bool IsServer => base.IsServer; // convenience for PlutoUtilityAi

    public bool IsAlive => bossHealth != null && bossHealth.IsAlive;

    // Runtime
    private bool aiActive = false;
    private float thinkTimer;

    private Vector3 basePos;
    private float hoverPhase;
    private float currentAmp;
    private float currentOmega;
    private bool hoverEnabled;
    private bool hoverStopping;
    private float stopOmegaRate;
    private float ampFadeRate;

    // Targeting
    public GameObject Target { get; private set; }
    private readonly List<GameObject> players = new();

    private float HealthPct =>
        (bossHealth != null && bossHealth.MaxHP > 0f) ? (bossHealth.CurrentHP / bossHealth.MaxHP) : 0f;

    public override void OnNetworkSpawn()
    {
        if (!IsServer) { enabled = false; return; }

        if (!bossHealth) bossHealth = GetComponent<BossHealth>();
        if (!bossHealth)
        {
            Debug.LogError("[PlutoBoss] BossHealth missing on prefab.");
            enabled = false;
            return;
        }

        basePos = transform.position;
        if (hoverOnStart) StartHover(hoverAmplitude, hoverSpeed); else StopHoverImmediate();

        CachePlayers();
        AutoPickInitialTarget();

        // Collect actions from the planets root and manual list, then init utility
        CollectPlanetActions();
        if (!utilityAi) utilityAi = GetComponent<PlutoUtilityAi>() ?? gameObject.AddComponent<PlutoUtilityAi>();
        utilityAi.Initialize(this, planetActions);

        var refs = ArenaSceneRefs.Instance;
        if (refs != null && refs.runIntro)
            StartCoroutine(StartAI_AfterDelay(refs.cutsceneDuration));
        else
            aiActive = true;
    }

    private void CollectPlanetActions()
    {
        planetActions.RemoveAll(a => a == null);

        if (planetsRoot)
        {
            var found = planetsRoot.GetComponentsInChildren<OrbActionBase>(true);
            foreach (var a in found)
                if (a && !planetActions.Contains(a))
                    planetActions.Add(a);
        }
    }
    private void Update()
    {
        if (!IsServer) return;

        if (bossHealth != null && !bossHealth.IsAlive)
        {
            aiActive = false;
            return;
        }

        UpdateHover();
        UpdateLookAtTarget();

        if (!aiActive) return;

        thinkTimer -= Time.deltaTime;
        if (thinkTimer <= 0f)
        {
            thinkTimer = thinkInterval;
            Think();
        }
    }

    private IEnumerator StartAI_AfterDelay(float delay)
    {
        thinkTimer = 5f; //hard coded to remove later
        yield return new WaitForSeconds(delay);
        if (bossHealth != null && bossHealth.IsAlive)
            aiActive = true;
    }

    // ================= Hover =================

    private void UpdateHover()
    {
        if (!hoverEnabled) return;

        if (hoverStopping)
        {
            if (stopOmegaRate > 0f)
            {
                currentOmega = Mathf.Max(0f, currentOmega - stopOmegaRate * Time.deltaTime);
                if (currentOmega <= 0.0001f)
                {
                    currentOmega = 0f;
                    hoverEnabled = false;
                    hoverStopping = false;
                    return;
                }
            }
            if (ampFadeRate > 0f)
                currentAmp = Mathf.Max(0f, currentAmp - ampFadeRate * Time.deltaTime);
        }

        hoverPhase += currentOmega * Time.deltaTime;
        float yOffset = Mathf.Sin(hoverPhase) * currentAmp;

        Vector3 p = basePos;
        p.y += yOffset;
        transform.position = p;
    }

    public void StartHover(float amplitude, float radiansPerSec)
    {
        hoverEnabled = true;
        hoverStopping = false;
        currentAmp = Mathf.Abs(amplitude);
        currentOmega = Mathf.Max(0f, radiansPerSec);
        basePos = transform.position;
        // Optionally reset hoverPhase = 0f;
    }

    public void StopHover(float slowDownRate, float amplitudeFadePerSec = 0f)
    {
        if (currentOmega <= 0f) { StopHoverImmediate(); return; }
        hoverStopping = true;
        stopOmegaRate = Mathf.Max(0.0001f, slowDownRate);
        ampFadeRate = Mathf.Max(0f, amplitudeFadePerSec);
    }

    public void StopHoverImmediate()
    {
        hoverEnabled = false;
        hoverStopping = false;
        currentOmega = 0f;
    }

    public void SetHoverBaseToCurrent()
    {
        basePos = transform.position;
    }

    // ================= Look At Target =================

    private void UpdateLookAtTarget()
    {
        if (Target == null) return;

        Transform rotRoot = model != null ? model : transform;
        Vector3 to = Target.transform.position - rotRoot.position;
        float sqr = to.sqrMagnitude;
        if (sqr < 0.0001f) return;

        Vector3 dir = to / Mathf.Sqrt(sqr);

        // Pick a safe up so LookRotation doesn't flip when dir ~ parallel to Vector3.up
        Vector3 up = Vector3.up;
        float parallel = Mathf.Abs(Vector3.Dot(dir, up));
        if (parallel > 0.999f)
        {
            // Build an up vector orthogonal to dir using current orientation
            up = Vector3.Cross(rotRoot.right, dir).normalized;
            if (up.sqrMagnitude < 1e-6f)
                up = Vector3.Cross(rotRoot.forward, dir).normalized;
            if (up.sqrMagnitude < 1e-6f)
                up = Vector3.up; // last resort
        }

        Quaternion desired = Quaternion.LookRotation(dir, up);
        rotRoot.rotation = Quaternion.RotateTowards(rotRoot.rotation, desired, lookTurnSpeed * Time.deltaTime);
    }

    // Targeting API

    private void CachePlayers()
    {
        players.Clear();
        foreach (var kv in NetworkManager.Singleton.ConnectedClients)
        {
            var po = kv.Value.PlayerObject;
            if (po != null && po.IsSpawned)
                players.Add(po.gameObject);
        }
        // Ensure deterministic order by clientId
        players.Sort((a, b) =>
        {
            var na = a.GetComponent<NetworkObject>();
            var nb = b.GetComponent<NetworkObject>();
            if (na == null || nb == null) return 0;
            return na.OwnerClientId.CompareTo(nb.OwnerClientId);
        });
    }

    private void AutoPickInitialTarget()
    {
        if (players.Count == 0) { Target = null; return; }

        // Example: choose the closest to the boss core
        GameObject best = null;
        float bestSqr = float.PositiveInfinity;
        Vector3 origin = core ? core.position : transform.position;

        for (int i = 0; i < players.Count; i++)
        {
            var p = players[i];
            if (!p) continue;
            float sq = (p.transform.position - origin).sqrMagnitude;
            if (sq < bestSqr)
            {
                bestSqr = sq;
                best = p;
            }
        }

        Target = best;
    }

    public void SetTarget(GameObject player)
    {
        if (!IsServer) return;
        if (player == null) { Target = null; return; }

        // Optional validation: ensure it is a known player
        if (!players.Contains(player))
        {
            CachePlayers();
            if (!players.Contains(player))
                return;
        }

        Target = player;
    }

    public void SetTargetByIndex(int index)
    {
        if (!IsServer) return;
        if (players.Count == 0) { Target = null; return; }
        index = Mathf.Clamp(index, 0, players.Count - 1);
        Target = players[index];
    }

    public void TargetNextPlayer()
    {
        if (!IsServer) return;
        if (players.Count == 0) { Target = null; return; }
        if (Target == null) { Target = players[0]; return; }

        int idx = players.IndexOf(Target);
        if (idx < 0) { Target = players[0]; return; }
        idx = (idx + 1) % players.Count;
        Target = players[idx];
    }

    // ================= AI stub =================

    private void Think()
    {
        // Build context and select action
        if (utilityAi == null) return;

        var ctx = utilityAi.BuildContext();
        utilityAi.SelectAndExecute(ctx);
    }

    // ================= Damage entry =================
    //not in use currently
    public void ApplyCoreDamage(float amount)
    {
        if (!IsServer || bossHealth == null || !bossHealth.IsAlive) return;
        bossHealth.ApplyDamage(Mathf.Abs(amount));
    }
    public void TakeDamage(float amount)
    {
        if (!IsServer) return;
        if (bossHealth == null || !bossHealth.IsAlive) return;
        Debug.Log("damage boss");
        bossHealth.ApplyDamage(Mathf.Abs(amount));
    }
}


