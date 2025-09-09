using UnityEngine;
using Unity.Netcode;

public class PlayerLook: NetworkBehaviour
{
    [Header("References")]
    [SerializeField] private Transform cameraPivot;   // Empty object at head height
    [SerializeField] private GameObject playerCamera;     // Child camera

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
    }

    /// <summary>
    /// Called by InputManager.LateUpdate for the local player
    /// </summary>
    public void ProcessLook(Vector2 input)
    {
        if (!IsOwner) return;

        float mouseX = (input.x*Time.deltaTime) * xSensitivity;
        float mouseY = (input.y *Time.deltaTime)* ySensitivity;

        // Pitch (look up/down)
        pitch = Mathf.Clamp(pitch - mouseY, -80f, 80f);
        playerCamera.transform.localRotation = Quaternion.Euler(pitch, 0f, 0f);

        // Yaw (look left/right, rotates the body)
        yaw += mouseX;
        transform.rotation = Quaternion.Euler(0f, yaw, 0f);

        // Sync pitch for other clients
        //syncedPitch.Value = pitch;
    }

    /*private void Update()
    {
        if (!IsOwner)
        {
            // Remote players: update their camera pivot to match their synced pitch
            cameraPivot.rotation = Quaternion.Euler(syncedPitch.Value, 0f, 0f);
        }
    }*/
}
