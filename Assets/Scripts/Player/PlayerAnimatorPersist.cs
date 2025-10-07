using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

[DisallowMultipleComponent]
public class PlayerAnimatorPersist : NetworkBehaviour
{
    [SerializeField] private Animator animator;
    [SerializeField] private float snapshotInterval = 0.1f; // seconds between auto-captures

    private AnimatorSnapshot snapshot = new AnimatorSnapshot();
    private float nextCapture;
    private bool subscribed;

    public override void OnNetworkSpawn()
    {
        if (!IsOwner) { enabled = false; return; }

        if (!animator) animator = GetComponentInChildren<Animator>(true);
        if (!animator)
        {
            Debug.LogWarning("[PlayerAnimatorPersist] Animator not found.");
            enabled = false;
            return;
        }

        // Always animate helps across loads
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

        Subscribe();
        // Prime an initial snapshot
        snapshot.Capture(animator);
        nextCapture = Time.time + snapshotInterval;
    }

    public override void OnNetworkDespawn()
    {
        Unsubscribe();
    }

    private void Subscribe()
    {
        if (subscribed || NetworkManager == null) return;

        // Apply after NGO finishes the scene load
        NetworkManager.SceneManager.OnLoadEventCompleted += OnLoadCompleted;

        // Also hook generic scene events if present (captures many cases)
        NetworkManager.SceneManager.OnSceneEvent += OnSceneEvent;

        // As a general fallback (non-NGO), you can also apply after Unity sceneLoaded:
        SceneManager.sceneLoaded += OnUnitySceneLoaded;

        subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!subscribed || NetworkManager == null) return;

        NetworkManager.SceneManager.OnLoadEventCompleted -= OnLoadCompleted;
        NetworkManager.SceneManager.OnSceneEvent -= OnSceneEvent;
        SceneManager.sceneLoaded -= OnUnitySceneLoaded;

        subscribed = false;
    }

    private void Update()
    {
        if (!IsOwner || animator == null) return;

        // Periodically capture so we always have a fresh snapshot,
        // removing the need for a "load started" callback.
        if (Time.time >= nextCapture)
        {
            snapshot.Capture(animator);
            nextCapture = Time.time + snapshotInterval;
        }
    }

    private void OnLoadCompleted(string sceneName, LoadSceneMode mode,
        List<ulong> clientsCompleted, List<ulong> clientsTimedOut)
    {
        if (!IsOwner || animator == null) return;
        StartCoroutine(ApplyNextFrame());
    }

    private void OnUnitySceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (!IsOwner || animator == null) return;
        StartCoroutine(ApplyNextFrame());
    }

    private void OnSceneEvent(SceneEvent e)
    {
        // For completeness: when NGO tells us a load completed for our client, apply
        if (!IsOwner || animator == null) return;
        if (e.SceneEventType == SceneEventType.LoadEventCompleted &&
            e.ClientId == NetworkManager.LocalClientId)
        {
            StartCoroutine(ApplyNextFrame());
        }
    }

    private IEnumerator ApplyNextFrame()
    {
        // Ensure Animator is enabled and controller initialized
        yield return null;
        snapshot.Apply(animator);
        Debug.Log("apply");
    }
}

/// Stores current layer state + parameters so we can restore after scene load
[System.Serializable]
public class AnimatorSnapshot
{
    private struct LayerState
    {
        public int hash;
        public float normalizedTime;
    }

    private List<LayerState> layers = new();
    private Dictionary<string, float> floatParams = new();
    private Dictionary<string, int> intParams = new();
    private Dictionary<string, bool> boolParams = new();

    public void Capture(Animator a)
    {
        layers.Clear();
        floatParams.Clear();
        intParams.Clear();
        boolParams.Clear();

        int lc = a.layerCount;
        for (int i = 0; i < lc; i++)
        {
            var info = a.GetCurrentAnimatorStateInfo(i);
            int hash = info.fullPathHash;
            float norm = info.normalizedTime - Mathf.Floor(info.normalizedTime); // keep fractional progress
            layers.Add(new LayerState { hash = hash, normalizedTime = norm });
        }

        foreach (var p in a.parameters)
        {
            switch (p.type)
            {
                case AnimatorControllerParameterType.Float:
                    floatParams[p.name] = a.GetFloat(p.name);
                    break;
                case AnimatorControllerParameterType.Int:
                    intParams[p.name] = a.GetInteger(p.name);
                    break;
                case AnimatorControllerParameterType.Bool:
                    boolParams[p.name] = a.GetBool(p.name);
                    break;
                case AnimatorControllerParameterType.Trigger:
                    // skip (triggers are edge events)
                    break;
            }
        }
    }

    public void Apply(Animator a)
    {
        foreach (var kv in floatParams) a.SetFloat(kv.Key, kv.Value);
        foreach (var kv in intParams) a.SetInteger(kv.Key, kv.Value);
        foreach (var kv in boolParams) a.SetBool(kv.Key, kv.Value);

        for (int i = 0; i < layers.Count; i++)
        {
            var s = layers[i];
            a.Play(s.hash, i, s.normalizedTime);
        }

        a.Update(0f); // force re-eval without advancing time
    }
}
