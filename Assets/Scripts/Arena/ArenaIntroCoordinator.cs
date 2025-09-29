using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

public class ArenaIntroCoordinator : NetworkBehaviour
{
    private bool introRunning = false;
    private float introEndTime = 0f;

    public override void OnNetworkSpawn()
    {
        if (!IsServer) { enabled = false; return; }
        NetworkManager.SceneManager.OnLoadEventCompleted += OnLoadCompleted;
        NetworkManager.OnClientConnectedCallback += OnClientConnected;
    }

    public override void OnNetworkDespawn()
    {
        if (!IsServer || NetworkManager == null) return;
        NetworkManager.SceneManager.OnLoadEventCompleted -= OnLoadCompleted;
        NetworkManager.OnClientConnectedCallback -= OnClientConnected;
    }

    private void OnLoadCompleted(string sceneName, LoadSceneMode mode,
        List<ulong> clientsCompleted, List<ulong> clientsTimedOut)
    {
        if (!IsServer) return;
        if (sceneName != Loader.Scene.ArenaScene.ToString()) return;
        if (introRunning) return;

        var refs = ArenaSceneRefs.Instance;
        if (!refs) { Debug.LogWarning("ArenaSceneRefs missing."); return; }

        // If intro is disabled, just place players and ensure player cam > cutscene cam, then return.
        if (!refs.runIntro)
        {
            PlacePlayersAtSpawns();
            ExitCutsceneClientRpc(); // ensures player cams/HUD/input are on and canvas hidden
            return;
        }

        // Run intro normally
        introRunning = true;
        introEndTime = Time.time + refs.cutsceneDuration;

        EnterCutsceneClientRpc(refs.cutsceneDuration);
        StartCoroutine(EndCutsceneAfter(refs.cutsceneDuration));
    }

    private void OnClientConnected(ulong clientId)
    {
        if (!IsServer) return;

        var refs = ArenaSceneRefs.Instance;
        if (!refs) return;

        // If intro is disabled, make sure the late joiner is placed & not in cutscene
        if (!refs.runIntro)
        {
            // place just the new client (not all)
            var po = NetworkManager.ConnectedClients[clientId].PlayerObject;
            if (po != null && po.IsSpawned)
            {
                Transform spawn = (clientId == NetworkManager.ServerClientId) ? refs.hostSpawn : refs.clientSpawn;
                if (spawn) po.transform.SetPositionAndRotation(spawn.position, spawn.rotation);
            }

            // tell only that client to exit cutscene state (enable input/HUD, set priorities, hide canvas)
            var onlyNew = new ClientRpcParams
            {
                Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } }
            };
            ExitCutsceneClientRpc(onlyNew);
            return;
        }

        // If intro is running, sync remainder to the late joiner
        if (introRunning)
        {
            float remaining = Mathf.Max(0f, introEndTime - Time.time);
            if (remaining > 0.01f)
            {
                var onlyNew = new ClientRpcParams
                {
                    Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } }
                };
                EnterCutsceneClientRpc(remaining, onlyNew);
            }
        }
    }

    private IEnumerator EndCutsceneAfter(float seconds)
    {
        yield return new WaitForSeconds(seconds);
        PlacePlayersAtSpawns();
        introRunning = false;
        ExitCutsceneClientRpc();
    }

    private void PlacePlayersAtSpawns()
    {
        var refs = ArenaSceneRefs.Instance;
        if (!refs) return;

        foreach (var kv in NetworkManager.ConnectedClients)
        {
            var po = kv.Value.PlayerObject;
            if (po == null || !po.IsSpawned) continue;

            Transform spawn = (kv.Key == NetworkManager.ServerClientId) ? refs.hostSpawn : refs.clientSpawn;
            if (spawn) po.transform.SetPositionAndRotation(spawn.position, spawn.rotation);
        }
    }

    // ========= RPCs =========

    [ClientRpc]
    private void EnterCutsceneClientRpc(float holdDuration, ClientRpcParams p = default)
    {
        var refs = ArenaSceneRefs.Instance;
        if (!refs) return;

        if (refs.cutsceneCanvas) refs.cutsceneCanvas.SetActive(true);

        // Priorities: cutscene over player for smooth blend (CM v3)
        if (refs.cutsceneCam) refs.cutsceneCam.Priority.Value = 100;

        var localPlayer = NetworkManager.Singleton.LocalClient?.PlayerObject;
        if (localPlayer)
        {
            var look = localPlayer.GetComponent<PlayerLook>();
            var playerCam = look ? look.PlayerCam : null;
            if (playerCam) playerCam.Priority.Value = 10;

            var input = localPlayer.GetComponentInChildren<InputManager>(true);
            if (input) input.enabled = false;

            var hud = localPlayer.GetComponentInChildren<PlayerHUD>(true);
            if (hud) hud.gameObject.SetActive(false);
        }
    }

    [ClientRpc]
    private void ExitCutsceneClientRpc(ClientRpcParams p = default)
    {
        var refs = ArenaSceneRefs.Instance;
        if (!refs) return;

        // Blend back: player camera over cutscene camera
        var localPlayer = NetworkManager.Singleton.LocalClient?.PlayerObject;
        if (localPlayer)
        {
            var look = localPlayer.GetComponent<PlayerLook>();
            var playerCam = look ? look.PlayerCam : null;
            if (playerCam) playerCam.Priority.Value = 100;
            if (refs.cutsceneCam) refs.cutsceneCam.Priority.Value = 10;

            var input = localPlayer.GetComponentInChildren<InputManager>(true);
            if (input) input.enabled = true;

            var hud = localPlayer.GetComponentInChildren<PlayerHUD>(true);
            if (hud) hud.gameObject.SetActive(true);
        }

        if (refs.cutsceneCanvas) refs.cutsceneCanvas.SetActive(false);
    }
}

