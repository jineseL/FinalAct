using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

public class ArenaGateInteractable : NetworkBehaviour, IInteractable
{
    [SerializeField] private string prompt = "Enter Arena (requires a weapon equipped)";
    private bool isTransitioning = false;

    public string GetInteractionPrompt() => prompt;

    public void OnHoverEnter() { /* optional VFX */ }
    public void OnHoverExit() { /* optional VFX */  }

    // Called on the SERVER by your TryInteractServerRpc -> interactable.Interact(interactor)
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
        NetworkManager.SceneManager.LoadScene(Loader.Scene.ArenaScene.ToString(), LoadSceneMode.Single);
    }

    [ClientRpc]
    private void ShowMessageClientRpc(string msg, ClientRpcParams p = default)
    {
        // TODO: hook into your UI to show a popup/toast
        Debug.Log($"[ArenaGate] {msg}");
    }
}

