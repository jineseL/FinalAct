using UnityEngine;

public class HoverRotate : MonoBehaviour
{
    [SerializeField] float rotateSpeed = 90f;   // deg/sec
    [SerializeField] float hoverAmplitude = 0.15f;
    [SerializeField] float hoverPeriod = 2.0f;

    Vector3 _baseLocalPos;

    void Awake() => _baseLocalPos = transform.localPosition;

    void Update()
    {
        transform.Rotate(0f, rotateSpeed * Time.deltaTime, 0f, Space.Self);

        float t = (Time.time % hoverPeriod) / hoverPeriod;    // 0..1
        float y = Mathf.Sin(t * Mathf.PI * 2f) * hoverAmplitude;
        transform.localPosition = _baseLocalPos + Vector3.up * y;
    }
}
