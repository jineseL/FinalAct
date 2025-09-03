using UnityEngine;
using Unity.Netcode;

public class PlayerLook: NetworkBehaviour
{
    public Camera cam;
    private float xRotation = 0f;

    public float xSensitivity = 30f;
    public float ySensitivity = 30f;

    public override void OnNetworkSpawn()
    {
        if (!IsOwner)
        {
            // Disable remote cameras
            cam.enabled = false;
            cam.GetComponent<AudioListener>().enabled = false;
        }
    }
    public void ProcessLook(Vector2 input)
    {
        float mouseX = input.x;
        float mouseY = input.y;
        //calculate camera rotation for looking up and down
        xRotation -= (mouseY * Time.deltaTime) * ySensitivity;
        xRotation = Mathf.Clamp(xRotation, -80f, 80f); //to control speed of Rotation

        cam.transform.localRotation = Quaternion.Euler(xRotation, 0, 0);
        transform.Rotate(Vector3.up * (mouseX * Time.deltaTime) * xSensitivity);
    }
}
