using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections;
using System.Collections.Generic;

public class ArenaGateInteractable : NetworkBehaviour, IInteractable
{
    [SerializeField] private string prompt = "Enter Arena (requires a weapon equipped)";
    [SerializeField] private float idleWaitTimeout = 5f;
    [SerializeField] private Loader.Scene nextScene;
    private bool isTransitioning = false;

    public string GetInteractionPrompt() => prompt;

    public void OnHoverEnter() { /* optional VFX */ }
    public void OnHoverExit() { /* optional VFX */  }

    // Called on the SERVER by your TryInteractServerRpc -> interactable.Interact(interactor)
    private readonly HashSet<ulong> readyClients = new();
    public void Interact(GameObject interactor)
    {
        if (!IsServer) return;
        if (isTransitioning) return;

        var wm = interactor.GetComponent<PlayerWeaponManager>();
        if (wm == null || wm.currentWeapon == null)
        {
            // Tell only that client why it failed (toast/message)
            var nob = interactor.GetComponent<NetworkObject>();
            if (nob != null)
                ShowMessageClientRpc("You must equip a weapon first!", new ClientRpcParams
                {
                    Send = new ClientRpcSendParams { TargetClientIds = new[] { nob.OwnerClientId } }
                });
            return;
        }

        isTransitioning = true;
        // Kick off network scene change (server only)
        readyClients.Clear();
        RequestIdleReadyClientRpc(idleWaitTimeout);

        // Wait for everyone (or timeout) then load nextScene
        //StartCoroutine(WaitAllReadyThenLoad(Loader.Scene.ArenaScene.ToString()));
        StartCoroutine(WaitAllReadyThenLoad(nextScene.ToString()));
    }
    private IEnumerator WaitAllReadyThenLoad(string nextSceneName)
    {
        float tEnd = Time.time + Mathf.Max(0.5f, 5);
        // number of clients to wait for (host counts too)
        int expected = NetworkManager.Singleton.ConnectedClients.Count;

        while (Time.time < tEnd)
        {
            if (readyClients.Count >= expected)
                break;
            yield return null;
        }

        // Proceed regardless after timeout
        NetworkManager.SceneManager.LoadScene(nextSceneName, LoadSceneMode.Single);
    }

    [ClientRpc]
    private void RequestIdleReadyClientRpc(float timeout, ClientRpcParams p = default)
    {
        // Only the local client can accurately check its own FPS animator
        var localPO = NetworkManager.Singleton.LocalClient?.PlayerObject;
        if (!localPO)
        {
            // No player object? still report ready so we don't block
            ReportIdleReadyServerRpc();
            return;
        }

        var pm = localPO.GetComponent<PlayerManager>();
        Animator fps = pm != null ? pm.fpsAnimator : null;

        // Start a local wait
        pm.StartCoroutine(WaitIdleThenReport(fps, timeout));
    }

    private IEnumerator WaitIdleThenReport(Animator fps, float timeout)
    {
        float tEnd = Time.time + Mathf.Max(0.25f, timeout);
        //bool ready = false;

        if (fps == null)
        {
            // If no animator, don't block transition
            //ready = true;
        }
        else
        {
            // If your Idle state has a different name/path, adjust here
            int idleHash = Animator.StringToHash("Idle");

            while (Time.time < tEnd)
            {
                if (!fps.isActiveAndEnabled || fps.runtimeAnimatorController == null || fps.layerCount == 0)
                {
                    // Animator not ready; still wait a bit
                    yield return null;
                    continue;
                }

                var st = fps.GetCurrentAnimatorStateInfo(0);
                if (st.shortNameHash == idleHash || st.IsName("Idle"))
                {
                    //ready = true;
                    break;
                }

                yield return null;
            }
        }

        // Regardless of ready or timeout, report so server can proceed
        ReportIdleReadyServerRpc();
    }

    [ServerRpc(RequireOwnership = false)]
    private void ReportIdleReadyServerRpc(ServerRpcParams rpcParams = default)
    {
        readyClients.Add(rpcParams.Receive.SenderClientId);
    }
    [ClientRpc]
    private void ShowMessageClientRpc(string msg, ClientRpcParams p = default)
    {
        // TODO: hook into your UI to show a popup/toast
        Debug.Log($"[ArenaGate] {msg}");
    }
}

