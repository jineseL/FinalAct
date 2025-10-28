using UnityEngine;
using Unity.Netcode;
using Unity.Cinemachine;
public class PlayerLook: NetworkBehaviour
{
    [Header("References")]
    [SerializeField] private GameObject cameraPivot;   // Empty object at head height
    [SerializeField] private GameObject playerCamera;     // Child camera
    [SerializeField] private CinemachineCamera playerCmCam; // assign PlayerCamera GO
    public CinemachineCamera PlayerCam => playerCmCam;
    [SerializeField] private AudioListener audioListener;

    [Header("Settings")]
    public float xSensitivity = 30f;
    public float ySensitivity = 30f;

    private float pitch = 0f; // up/down
    private float yaw = 0f;   // left/right

    // Network sync for pitch (so other players see your aim direction)
    private NetworkVariable<float> syncedPitch = new(
        0f, NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

    public override void OnNetworkSpawn()
    {
        if (!IsOwner)
        {
            // Disable remote cameras
            if (playerCamera != null)
            {
                playerCamera.SetActive(false);
                var listener = playerCamera.GetComponent<AudioListener>();
                if (listener) listener.enabled = false;
            }
        }
        yaw = transform.eulerAngles.y;
        pitch = cameraPivot.transform.localEulerAngles.x;
        if (pitch > 180f) pitch -= 360f; // map to [-180,180] for clamping
         // Local owner: lock cursor for FPS

         //uncomment if not testing
         // SetFpsCursor(true);

    }
    public override void OnNetworkDespawn()
    {
        if (IsOwner)
            SetFpsCursor(false);
    }
    /// <summary>
    /// Called by InputManager.LateUpdate for the local player
    /// </summary>
    public void ProcessLook(Vector2 input)
    {
        if (!IsOwner) return;

        float mouseX = (input.x) * xSensitivity;
        float mouseY = (input.y )* ySensitivity;

        // Pitch (look up/down)
        pitch = Mathf.Clamp(pitch - mouseY, -80f, 80f);
        cameraPivot.transform.localRotation = Quaternion.Euler(pitch, 0f, 0f);
        //cameraPivot.transform.localRotation = Quaternion.Euler(pitch, 0f, 0f);
        // Yaw (look left/right, rotates the body)
        yaw += mouseX;
        transform.rotation = Quaternion.Euler(0f, yaw, 0f);

        // Sync pitch for other clients
        //syncedPitch.Value = pitch;
    }
    public void SetCameraActive(bool on)
    {
        if (playerCamera) playerCamera.SetActive(on);
        if (playerCmCam) playerCmCam.enabled = on;               // useless but safer
        if (audioListener) audioListener.enabled = on;
    }
    public void SetFpsCursor(bool locked)
    {
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }
}
