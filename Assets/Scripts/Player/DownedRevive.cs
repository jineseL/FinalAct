using Unity.Netcode;
using UnityEngine;

public class DownedRevive : NetworkBehaviour, IInteractable
{
    [SerializeField] private float interactRange = 5f; // sanity range check on server

    private PlayerHealth health;

    private void Awake()
    {
        health = GetComponent<PlayerHealth>();
        if (!health) Debug.LogWarning("[DownedRevive] Missing PlayerHealth.");
    }

    // IInteractable
    public void OnHoverEnter()
    {
        // Optional: show "Hold E to Revive" tooltip on local HUD if this is not your own player
    }

    public void OnHoverExit()
    {
        // Optional: hide tooltip
    }
    public string GetInteractionPrompt()
    {
        //for text
        return null;
    }
    // Your PlayerWeaponManager calls this on the server via TryInteractServerRpc(targetRef)
    public void Interact(GameObject interactor)
    {
        if (!IsServer) return;
        if (health == null || !health.IsDowned) return;
        if (interactor == null) return;
        // range check
        if (Vector3.Distance(interactor.transform.position, transform.position) > interactRange)
            return;
        // begin revive hold
        Debug.Log("interacting");
        health.ServerBeginRevive(interactor);
    }

    // Allow cancel via a direct ServerRpc
    [ServerRpc(RequireOwnership = false)]
    public void CancelReviveServerRpc(ServerRpcParams p = default)
    {
        if (!IsServer) return;
        if (health == null || !health.IsDowned) return;

        // identify the interactor by client id if you want stricter ownership checks
        var clientId = p.Receive.SenderClientId;
        var interactor = NetworkManager.ConnectedClients[clientId].PlayerObject?.gameObject;
        health.ServerCancelRevive(interactor);
    }
}
