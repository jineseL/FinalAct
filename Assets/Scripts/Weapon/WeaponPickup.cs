using UnityEngine;
using Unity.Netcode;
public class WeaponPickup : NetworkBehaviour, IInteractable
{
    public int weaponIndex;
    public string weaponName;

    public void Interact(GameObject interactor)
    {
        if (!interactor.TryGetComponent<PlayerWeaponManager>(out var pwm)) return;

        // Request pickup from server
        if (pwm.IsOwner) // only the owning client sends the request
        {
            pwm.EquipWeaponServerRpc(weaponIndex);
        }
    }

    //for UI 
    public string GetInteractionPrompt()
    {
        return $"Press E to pick up {weaponName}";
    }

    //for hover over to use next time
    public void OnHoverEnter() {   }
    public void OnHoverExit() {   }
}

