using Unity.Netcode;
using UnityEngine;

public class PlayerWeaponManager : NetworkBehaviour
{
    [Header("Weapons (same objects for owner & others)")]
    [SerializeField] private GameObject[] fpsWeapons; // must exist on the player prefab for ALL clients
    [SerializeField] private Weapons[] weapons;

    [Header("Interaction")]
    [SerializeField] private GameObject playerCamera;
    [SerializeField] private float interactRange = 4f;

    public int currentWeaponIndex = -1;
    public Weapons currentWeapon;
    private IInteractable currentInteractable;

    // Server-owned state that everyone reads
    private NetworkVariable<int> equippedIndex = new NetworkVariable<int>(
        -1,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    public override void OnNetworkSpawn()
    {
        // Ensure all hidden by default on every client
        if (fpsWeapons != null)
            foreach (var w in fpsWeapons) if (w) w.SetActive(false);

        // React to changes from server
        equippedIndex.OnValueChanged += OnEquippedChanged;

        // Handle late join: if server already set something, apply it now
        if (equippedIndex.Value >= 0)
            SetWeaponActive(equippedIndex.Value);
    }

    public override void OnNetworkDespawn()
    {
        equippedIndex.OnValueChanged -= OnEquippedChanged;
    }

    private void Update()
    {
        if (!IsOwner) return;
        HandleInteractionRaycast();
    }

    // === Interaction ===
    public void TryInteract()
    {
        if (currentInteractable is NetworkBehaviour nb)
            TryInteractServerRpc(nb);
    }

    private void HandleInteractionRaycast()
    {
        Ray ray = new Ray(playerCamera.transform.position, playerCamera.transform.forward);
        if (Physics.Raycast(ray, out RaycastHit hit, interactRange))
        {
            //IInteractable interactable = hit.collider.GetComponent<IInteractable>();
            IInteractable interactable = hit.collider.GetComponentInParent<IInteractable>();
            if (interactable != null)
            {
                if (currentInteractable != interactable)
                {
                    ClearHover();
                    currentInteractable = interactable;
                    currentInteractable.OnHoverEnter();
                }
                return;
            }
        }
        ClearHover();
    }

    private void ClearHover()
    {
        if (currentInteractable != null)
        {
            currentInteractable.OnHoverExit();
            currentInteractable = null;
        }
    }

    [ServerRpc(RequireOwnership = false)]
    private void TryInteractServerRpc(NetworkBehaviourReference targetRef, ServerRpcParams rpcParams = default)
    {
        if (targetRef.TryGet(out NetworkBehaviour target) && target is IInteractable interactable)
        {
            GameObject interactor = NetworkManager.Singleton.ConnectedClients[rpcParams.Receive.SenderClientId].PlayerObject.gameObject;
            interactable.Interact(interactor);
        }
    }

    // === Weapons ===

    // Call this from your pickup or input
    public void RequestEquip(int index)
    {
        if (!IsOwner) return;
        EquipWeaponServerRpc(index);
    }

    [ServerRpc(RequireOwnership = false)]
    public void EquipWeaponServerRpc(int index)
    {
        if (weapons == null || index < 0 || index >= weapons.Length) return;
        equippedIndex.Value = index; // Server authoritative; auto-replicates to everyone
    }

    private void OnEquippedChanged(int oldIndex, int newIndex)
    {
        SetWeaponActive(newIndex);
    }
    /*public void ServerForceEquip(int index)
    {
        if (!IsServer) return;
        if (weapons == null || index < 0 || index >= weapons.Length) return;
        equippedIndex.Value = index; // will replicate to all and call SetWeaponActive via OnValueChanged
    }*/

    private void SetWeaponActive(int index)
    {
        if (currentWeaponIndex == index) return;

        // Hide all
        if (fpsWeapons != null)
            foreach (var w in fpsWeapons) if (w) w.SetActive(false);

        currentWeaponIndex = index;
        currentWeapon = weapons != null && index >= 0 && index < weapons.Length ? weapons[index] : null;

        // Equip script side (pass the same object for both owner & others since you chose one model)
        if (currentWeapon != null)
            currentWeapon.EquipWeapon(GetComponent<PlayerManager>(), fpsWeapons[index]);

        // Show the equipped weapon for everyone
        if (fpsWeapons != null && fpsWeapons[index] != null)
            fpsWeapons[index].SetActive(true);
    }

    public void TryFire() { if (currentWeapon != null) currentWeapon.Fire(); }
    public void TryAltFire() { if (currentWeapon != null) currentWeapon.AltFire(); }
    public void TryReload() { if (currentWeapon != null) currentWeapon.Reload(); }
    public void TryInteractCancel()
    {
        if (!IsOwner) return;
        if (currentInteractable is DownedRevive dr)
        {
            // Tell server to cancel the current revive attempt
            dr.CancelReviveServerRpc();
        }
    }
}



