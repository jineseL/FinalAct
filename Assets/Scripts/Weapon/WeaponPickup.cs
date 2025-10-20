using UnityEngine;
using Unity.Netcode;
public class WeaponPickup : NetworkBehaviour, IInteractable
{
    public int weaponIndex;
    public string weaponName;

    [Header("Idle Motion")]
    [SerializeField] float rotateSpeed = 90f;   // deg/sec
    [SerializeField] float hoverAmplitude = 0.15f;
    [SerializeField] float hoverPeriod = 2.0f;

    Vector3 _baseLocalPos;

    [Header("World UI")]
    [SerializeField] WeaponPickupUI uiPrefab;     // the Canvas prefab
    [SerializeField] Sprite infoCardSprite;       // weapon card image
    [SerializeField] Sprite promptSprite;         // "Press E" image

    WeaponPickupUI _uiInstance;                   // local instance for the viewer

    void Awake() => _baseLocalPos = transform.localPosition;
    public void Interact(GameObject interactor)
    {
        var weaponManager = interactor.GetComponent<PlayerWeaponManager>();
        if (weaponManager != null)
        {
            // server tells all clients to equip weapon
            weaponManager.EquipWeaponServerRpc(weaponIndex);
        }
    }
    void Update()
    {
        transform.Rotate(0f, rotateSpeed * Time.deltaTime, 0f, Space.Self);

        float t = (Time.time % hoverPeriod) / hoverPeriod;    // 0..1
        float y = Mathf.Sin(t * Mathf.PI * 2f) * hoverAmplitude;
        transform.localPosition = _baseLocalPos + Vector3.up * y;
    }
    //for UI 
    public string GetInteractionPrompt()
    {
        return $"Press E to pick up {weaponName}";
    }

    //for hover over to use next time
    public void OnHoverEnter() 
    {
        if (_uiInstance != null || uiPrefab == null) return;
        // spawn the UI locally, facing the local player's camera
        _uiInstance = Instantiate(uiPrefab);
        _uiInstance.Init(transform, infoCardSprite, promptSprite, Camera.main);
    }
    public void OnHoverExit() 
    {
        if (_uiInstance != null)
        {
            Destroy(_uiInstance.gameObject);
            _uiInstance = null;
        }
    }
}

