using UnityEngine;
using Unity.Netcode;
using System.Collections;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine.InputSystem;

public class InputManager : NetworkBehaviour
{
    //handle all player inputs 
    private PlayerInput playerInput;
    private PlayerInput.OnFootActions onFoot;

    private PlayerMotor motor;
    private PlayerLook look;
    private PlayerWeaponManager weaponManager;
    //private bool isQuitting = false;
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
        onFoot.AltFire.performed += ctx => weaponManager.TryAltFire();
        onFoot.Reload.performed += ctx => weaponManager.TryReload();
        onFoot.Interact.started += ctx => weaponManager.TryInteract();
        onFoot.Interact.canceled += ctx => weaponManager.TryInteractCancel();
        onFoot.Esc.performed += ctx => AppQuit.Quit();
        onFoot.Enable();
    }
    private void Update()
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
        if (IsOwner && playerInput != null) onFoot.Enable();
    }
    private void OnDisable()
    {
        if (IsOwner && playerInput != null) onFoot.Disable();
    }
    /*private void QuitGame() //todo fix this shit
    {
        if (!IsOwner || isQuitting) return;
        isQuitting = true;
        StartCoroutine(QuitRoutine());
    }

    private IEnumerator QuitRoutine()
    {
        // Gracefully stop Netcode (both host and client)

        var nm = NetworkManager.Singleton;
        if (nm != null && nm.IsListening)
        {
            nm.Shutdown();
            // give a frame or two for shutdown events to process
            yield return null;
        }

        // In-editor vs build
#if UNITY_EDITOR
        //EditorApplication.isPlaying = false;
        Debug.Log("test");
        UnityEditor.EditorApplication.ExitPlaymode();
#else
        Application.Quit();
#endif
    }*/
}
