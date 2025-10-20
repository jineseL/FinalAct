// WeaponPickupUI.cs
using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(Canvas))]
public class WeaponPickupUI : MonoBehaviour
{
    [Header("Wiring")]
    public RectTransform infoPanel;
    public Image infoImage;
    public RectTransform promptPanel;
    public Image promptImage;

    [Header("Adjustable Positions (local offsets)")]
    public Vector3 worldOffset = new Vector3(0, 0.7f, 0); // offset from pickup
    public Vector3 infoOffset = new Vector3(0, 0.2f, 0);
    public Vector3 promptOffset = new Vector3(0, -0.2f, 0);

    [Header("Billboard")]
    public bool yawOnly = true;         // rotate only around Y (keeps panel upright)
    public float faceLerp = 12f;        // smoothing

    Transform _target;                  // pickup transform
    Camera _cam;

    public void Init(Transform target, Sprite infoSprite, Sprite promptSprite, Camera cam = null)
    {
        _target = target;
        _cam = cam != null ? cam : Camera.main;

        if (infoImage) infoImage.sprite = infoSprite;
        if (promptImage) promptImage.sprite = promptSprite;

        // put panels at user-tunable positions
        if (infoPanel) infoPanel.localPosition = infoOffset;
        if (promptPanel) promptPanel.localPosition = promptOffset;
    }

    void LateUpdate()
    {
        if (_target == null) { Destroy(gameObject); return; }
        if (_cam == null) _cam = Camera.main;

        // follow pickup position
        transform.position = _target.position + worldOffset;

        // face the player camera
        if (_cam != null)
        {
            Vector3 toCam = _cam.transform.position - transform.position;
            if (yawOnly) toCam.y = 0f;
            if (toCam.sqrMagnitude > 0.0001f)
            {
                var look = Quaternion.LookRotation(-toCam.normalized, Vector3.up);
                transform.rotation = Quaternion.Slerp(transform.rotation, look, Time.deltaTime * faceLerp);
            }
        }
    }
}
