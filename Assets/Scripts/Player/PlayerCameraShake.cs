// PlayerCameraShake.cs
using UnityEngine;
using Unity.Netcode;

public class PlayerCameraShake : NetworkBehaviour
{
    public enum Strength { Small, Medium, Hard }

    [System.Serializable]
    public struct Preset
    {
        public float duration;     // seconds
        public float posAmplitude; // meters
        public float rotAmplitude; // degrees
        public float frequency;    // Hz
    }

    [Header("Target (parent of PlayerCamera)")]
    [SerializeField] Transform cameraRig;  // drag the camera's PARENT here

    [Header("Presets")]
    public Preset small = new Preset { duration = 0.08f, posAmplitude = 0.03f, rotAmplitude = 0.8f, frequency = 22f };
    public Preset medium = new Preset { duration = 0.15f, posAmplitude = 0.05f, rotAmplitude = 1.5f, frequency = 20f };
    public Preset hard = new Preset { duration = 0.26f, posAmplitude = 0.08f, rotAmplitude = 2.2f, frequency = 18f };

    [Header("Damping")]
    public AnimationCurve damping = AnimationCurve.EaseInOut(0, 1, 1, 0);

    // Optional convenience pointer for the local player on this client
    public static PlayerCameraShake Local { get; private set; }
    public static void ShakeLocal(Strength s) => Local?.Shake(s);

    Vector3 _baseLocalPos;
    Quaternion _baseLocalRot;
    float _timeRemaining;
    float _duration, _posAmp, _rotAmp, _freq;
    float _seed;

    void Awake()
    {
        if (!cameraRig) TryAutoFindRig();
        if (cameraRig)
        {
            _baseLocalPos = cameraRig.localPosition;
            _baseLocalRot = cameraRig.localRotation;
        }
        _seed = Random.value * 1000f;
    }

    public override void OnNetworkSpawn()
    {
        // Mark this as the local shaker on this client
        if (IsOwner) Local = this;
    }

    void OnDisable()
    {
        if (Local == this) Local = null;
        ResetRig();
    }

    void LateUpdate()
    {
        if (!cameraRig || _timeRemaining <= 0f) return;

        float t = 1f - (_timeRemaining / _duration);
        float damper = damping.Evaluate(t);
        float time = (Time.time + _seed) * _freq;

        // -1..1 smooth pseudo-noise
        Vector3 nPos = new Vector3(
            Mathf.PerlinNoise(time, 0f),
            Mathf.PerlinNoise(0f, time),
            Mathf.PerlinNoise(time * 0.7f, time * 1.3f)
        ) * 2f - Vector3.one;

        Vector3 nRot = new Vector3(
            Mathf.PerlinNoise(time * 1.1f, time * 0.6f),
            Mathf.PerlinNoise(time * 0.8f, time * 1.2f),
            Mathf.PerlinNoise(time * 0.5f, time * 0.9f)
        ) * 2f - Vector3.one;

        cameraRig.localPosition = _baseLocalPos + nPos * (_posAmp * damper);
        cameraRig.localRotation = _baseLocalRot * Quaternion.Euler(nRot * (_rotAmp * damper));

        _timeRemaining -= Time.deltaTime;
        if (_timeRemaining <= 0f) ResetRig();
    }

    void ResetRig()
    {
        if (!cameraRig) return;
        cameraRig.localPosition = _baseLocalPos;
        cameraRig.localRotation = _baseLocalRot;
    }

    public void Shake(Strength strength)
    {
        var p = strength switch
        {
            Strength.Small => small,
            Strength.Medium => medium,
            _ => hard
        };

        _duration = Mathf.Max(0.0001f, p.duration);
        _posAmp = p.posAmplitude;
        _rotAmp = p.rotAmplitude;
        _freq = Mathf.Max(0.01f, p.frequency);

        _timeRemaining = Mathf.Max(_timeRemaining, _duration); // stack-friendly
        _seed = Random.Range(0f, 1000f);
    }

    void TryAutoFindRig()
    {
        // fall back: take the parent of the first Camera found
        var cam = GetComponentInChildren<Camera>(true);
        if (cam && cam.transform.parent) cameraRig = cam.transform.parent;
    }
}
