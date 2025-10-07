using UnityEngine;
using UnityEngine.Audio;

[DisallowMultipleComponent]
public class LoopingSfxAttachment : MonoBehaviour
{
    [Header("Source")]
    [Tooltip("If set, overrides SoundManager's mixer. Leave null to skip routing.")]
    [SerializeField] private AudioMixerGroup mixerOverride;

    [Header("Clip / Key")]
    [Tooltip("Use an AudioClip directly OR a SoundManager key. If both set, Clip wins.")]
    [SerializeField] private AudioClip clip;
    [SerializeField] private string soundKey;

    [Header("Playback")]
    [SerializeField] private bool playOnEnable = true;
    [SerializeField] private bool stopOnDisable = true;
    [SerializeField, Range(0f, 1f)] private float volume = 1f;
    [SerializeField, Range(-3f, 3f)] private float pitch = 1f;
    [SerializeField] private bool randomizePitch = false;
    [SerializeField] private Vector2 pitchRange = new Vector2(0.95f, 1.05f);

    [Header("3D Settings")]
    [SerializeField, Range(0f, 1f)] private float spatialBlend = 1f; // 1=3D
    [SerializeField] private float minDistance = 2f;
    [SerializeField] private float maxDistance = 25f;
    [SerializeField, Range(0f, 5f)] private float dopplerLevel = 0f;

    [Header("Attach / Follow")]
    [Tooltip("If true, the audio object is parented to this transform (no per-frame follow needed).")]
    [SerializeField] private bool parentToThis = true;
    [SerializeField] private Vector3 localOffset = Vector3.zero;

    [Header("Fades")]
    [SerializeField] private float fadeInSeconds = 0.0f;
    [SerializeField] private float fadeOutSeconds = 0.15f;

    private GameObject sfxGO;
    private AudioSource src;
    private float baseVolume;
    private bool isFading;

    private void OnEnable()
    {
        if (playOnEnable) StartLoop();
    }

    private void OnDisable()
    {
        if (stopOnDisable) StopLoop();
    }

    private void LateUpdate()
    {
        // Only needed if not parented (world-follow)
        if (src != null && !parentToThis && sfxGO != null)
        {
            sfxGO.transform.position = transform.position;
            if (sfxGO.transform.parent != null)
                sfxGO.transform.parent = null; // ensure detached
        }
    }

    public void StartLoop()
    {
        if (src != null && src.isPlaying) return;

        var useClip = clip;
        if (!useClip && !string.IsNullOrEmpty(soundKey))
            useClip = SoundManager.GetClip(soundKey); // helper below

        if (!useClip) return;

        // Create holder
        sfxGO = new GameObject($"LoopSFX_{useClip.name}");
        if (parentToThis)
        {
            sfxGO.transform.SetParent(transform, worldPositionStays: false);
            sfxGO.transform.localPosition = localOffset;
        }
        else
        {
            sfxGO.transform.position = transform.position + transform.TransformVector(localOffset);
        }

        src = sfxGO.AddComponent<AudioSource>();
        src.clip = useClip;
        src.loop = true;
        src.playOnAwake = false;

        // Routing
        if (mixerOverride != null)
            src.outputAudioMixerGroup = mixerOverride;
        else
            src.outputAudioMixerGroup = SoundManager.SfxMixerGroup; // uses your SoundManager's mixer if available

        // 3D config
        src.spatialBlend = spatialBlend;
        src.minDistance = Mathf.Max(0.01f, minDistance);
        src.maxDistance = Mathf.Max(src.minDistance + 0.01f, maxDistance);
        src.rolloffMode = AudioRolloffMode.Linear;
        src.dopplerLevel = dopplerLevel;

        // Volume/Pitch
        baseVolume = Mathf.Clamp01(volume) * SoundManager.SfxVolume; // respect global SFX volume
        src.volume = (fadeInSeconds > 0f) ? 0f : baseVolume;
        src.pitch = randomizePitch ? Random.Range(pitchRange.x, pitchRange.y) : Mathf.Clamp(pitch, -3f, 3f);

        src.Play();

        if (fadeInSeconds > 0f)
            StartCoroutine(FadeTo(baseVolume, fadeInSeconds));
    }

    public void StopLoop()
    {
        if (src == null) return;

        if (fadeOutSeconds > 0f && gameObject.activeInHierarchy)
        {
            StartCoroutine(FadeAndDestroy(fadeOutSeconds));
        }
        else
        {
            DestroyImmediate(sfxGO);
            sfxGO = null;
            src = null;
        }
    }

    public void SetVolume(float v)
    {
        volume = Mathf.Clamp01(v);
        baseVolume = volume * SoundManager.SfxVolume;
        if (src) src.volume = baseVolume;
    }

    public void SetPitch(float p)
    {
        pitch = Mathf.Clamp(p, -3f, 3f);
        if (src) src.pitch = pitch;
    }

    private System.Collections.IEnumerator FadeTo(float target, float time)
    {
        if (src == null) yield break;
        isFading = true;
        float start = src.volume;
        float t = 0f;
        while (t < time && src != null)
        {
            t += Time.deltaTime;
            float k = (time <= 0f) ? 1f : t / time;
            src.volume = Mathf.Lerp(start, target, k);
            yield return null;
        }
        if (src != null) src.volume = target;
        isFading = false;
    }

    private System.Collections.IEnumerator FadeAndDestroy(float time)
    {
        if (src == null) yield break;
        yield return FadeTo(0f, time);
        if (sfxGO) Destroy(sfxGO);
        src = null;
        sfxGO = null;
    }
}
