using UnityEngine;
using Unity.Netcode;

public class PlayerWeaponManager : NetworkBehaviour
{
    [Header("Weapons")]
    public GameObject[] fpsWeapons;
    public Weapons[] weapons;

    [Header("Interaction")]
    public GameObject playerCamera;
    public float interactRange = 4f;

    private int currentWeaponIndex = -1;
    public Weapons currentWeapon { private set; get; }
    private IInteractable currentInteractable;

    public override void OnNetworkSpawn()
    {
         foreach (var w in fpsWeapons) w.SetActive(false);
        
    }

    private void Update()
    {
        if (!IsOwner) return;
        HandleInteractionRaycast();
    }

    // === Called from InputManager ===
    public void TryInteract()
    {
        if (currentInteractable != null)
        {
            var nb = currentInteractable as NetworkBehaviour;
            if (nb != null)
            {
                TryInteractServerRpc(nb);
            }
        }
    }

    // === Hover detection ===
    private void HandleInteractionRaycast()
    {
        Ray ray = new Ray(playerCamera.transform.position, playerCamera.transform.forward);

        if (Physics.Raycast(ray, out RaycastHit hit, interactRange))
        {
            IInteractable interactable = hit.collider.GetComponent<IInteractable>();

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

    // === Networking ===
    [ServerRpc(RequireOwnership = false)]
    private void TryInteractServerRpc(NetworkBehaviourReference targetRef, ServerRpcParams rpcParams = default)
    {
        if (targetRef.TryGet(out NetworkBehaviour target))
        {
            IInteractable interactable = target as IInteractable;
            if (interactable != null)
            {
                var interactor = NetworkManager.Singleton.ConnectedClients[rpcParams.Receive.SenderClientId].PlayerObject.gameObject;
                interactable.Interact(interactor);
            }
        }
    }

    // === Weapons ===
    [ServerRpc] public void EquipWeaponServerRpc(int index) => EquipWeaponClientRpc(index);

    [ClientRpc] private void EquipWeaponClientRpc(int index) => SetWeaponActive(index);

    private void SetWeaponActive(int index)
    {
        if (currentWeaponIndex == index) return;

        foreach (var w in fpsWeapons) w.SetActive(false);

        currentWeaponIndex = index;
        currentWeapon = weapons[index]; //weapons script
        currentWeapon.EquipWeapon(GetComponent<PlayerManager>(), fpsWeapons[index]);
        fpsWeapons[index].SetActive(true);
            
        
    }

    public void TryFire() {
        if(currentWeapon !=null)
        currentWeapon?.Fire();
        
    }
    public void TryReload() {
        if (currentWeapon != null)
            currentWeapon?.Reload();
        
    }
}



