using UnityEngine;
using Unity.Netcode;
using UnityEngine.InputSystem;

public class InputManager : NetworkBehaviour
{
    //handle all player inputs 
    private PlayerInput playerInput;
    private PlayerInput.OnFootActions onFoot;

    private PlayerMotor motor;
    private PlayerLook look;
    private PlayerWeaponManager weaponManager;
    /*private void Awake()
    {
        //if (!IsOwner) return;
        //spawning should be done via a function in the future as isowner does not work in awake
        playerInput = new PlayerInput();
        onFoot = playerInput.OnFoot;
        motor = GetComponent<PlayerMotor>();
        look = GetComponent<PlayerLook>();
        onFoot.Jump.performed += ctx => motor.Jump();
    }*/
    /*private void Start()
    {
        PlayerInitizalize();
    }*/
    public override void OnNetworkSpawn()
    {
        PlayerInitizalize();
    }
    public void PlayerInitizalize()
    {
        if (!IsOwner) return;
        playerInput = new PlayerInput();
        onFoot = playerInput.OnFoot;
        motor = GetComponent<PlayerMotor>();
        look = GetComponent<PlayerLook>();
        weaponManager = GetComponent<PlayerWeaponManager>();
        onFoot.Jump.performed += ctx => motor.Jump();
        onFoot.Dash.performed += ctx => motor.Dash();
        onFoot.Fire.performed += ctx => weaponManager.TryFire();
        onFoot.Reload.performed += ctx => weaponManager.TryReload();
        onFoot.Interact.performed += ctx => weaponManager.TryInteract();
        onFoot.Enable();
    }
    private void FixedUpdate()
    {
        if (!IsOwner) return;
        //Debug.Log(onFoot.Movement.ReadValue<Vector2>());
        //tell playermotor to move using value from onFoot Vector 2
        motor.ProcessMove(onFoot.Movement.ReadValue<Vector2>());

    }
    private void LateUpdate()
    {
        if (!IsOwner) return;
        look.ProcessLook(onFoot.Look.ReadValue<Vector2>());
    }
    private void OnEnable()
    {
        if (IsOwner)
            onFoot.Enable();
    }
    private void OnDisable()
    {
        if (IsOwner)
            onFoot.Disable();
    }
}
