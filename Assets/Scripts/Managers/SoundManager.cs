using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;

public class SoundManager : MonoBehaviour
{
    public static SoundManager I { get; private set; }

    [Header("Routing (optional)")]
    [SerializeField] private AudioMixerGroup sfxMixer;

    [Header("Library (optional, for key lookups)")]
    [SerializeField] private SfxEntry[] sfxTable;
    private Dictionary<string, AudioClip> sfxDict;

    [Header("Defaults")]
    [Range(0f, 1f)] public float sfxVolume = 1f;
    [SerializeField] private int temp3DLayer = 0; // optional layer for spawned 3D one-shots

    // 2D one-shot source
    private AudioSource uiSource2D;

    public static AudioMixerGroup SfxMixerGroup => I != null ? I.sfxMixer : null;


    [System.Serializable]
    public struct SfxEntry
    {
        public string key;     // e.g., "slam_impact"
        public AudioClip clip; // assign in Inspector
    }

    private void Awake()
    {
        if (I != null && I != this) { Destroy(gameObject); return; }
        I = this;
        DontDestroyOnLoad(gameObject);

        // Build key dict
        if (sfxTable != null && sfxTable.Length > 0)
        {
            sfxDict = new Dictionary<string, AudioClip>(sfxTable.Length);
            foreach (var e in sfxTable)
            {
                if (!string.IsNullOrEmpty(e.key) && e.clip && !sfxDict.ContainsKey(e.key))
                    sfxDict.Add(e.key, e.clip);
            }
        }

        // Create 2D source
        uiSource2D = gameObject.AddComponent<AudioSource>();
        uiSource2D.playOnAwake = false;
        uiSource2D.loop = false;
        uiSource2D.spatialBlend = 0f; // 2D
        uiSource2D.outputAudioMixerGroup = sfxMixer;
        uiSource2D.volume = sfxVolume;
    }

    private void OnValidate()
    {
        if (uiSource2D) uiSource2D.volume = sfxVolume;
    }

    // ================= Static API (easy to call from anywhere) =================

    public static void PlaySfx(AudioClip clip, float volume = 1f, float pitch = 1f)
    {
        if (I == null || clip == null) return;
        I.Play2D(clip, volume, pitch);
    }

    public static void PlaySfx(string key, float volume = 1f, float pitch = 1f)
    {
        if (I == null || I.sfxDict == null) return;
        if (I.sfxDict.TryGetValue(key, out var clip))
            I.Play2D(clip, volume, pitch);
    }
    //local only
    public static void PlaySfxAt(AudioClip clip, Vector3 position, float volume = 1f, float pitch = 1f, float spatialBlend = 1f, float minDistance = 1f, float maxDistance = 25f)
    {
        if (I == null || clip == null) return;
        I.Play3D(clip, position, volume, pitch, spatialBlend, minDistance, maxDistance);
    }
    public static void PlaySfxAt(string key, Vector3 position, float volume = 1f, float pitch = 1f, float spatialBlend = 1f, float minDistance = 1f, float maxDistance = 25f)
    {
        if (I == null || I.sfxDict == null) return;
        if (I.sfxDict.TryGetValue(key, out var clip))
            I.Play3D(clip, position, volume, pitch, spatialBlend, minDistance, maxDistance);
    }

    public static float SfxVolume
    {
        get => I != null ? I.sfxVolume : 1f;
        set
        {
            if (I == null) return;
            I.sfxVolume = Mathf.Clamp01(value);
            if (I.uiSource2D) I.uiSource2D.volume = I.sfxVolume;
        }
    }

    // Safe lookup by key for other scripts
    public static AudioClip GetClip(string key)
    {
        if (I == null || I.sfxDict == null || string.IsNullOrEmpty(key)) return null;
        I.sfxDict.TryGetValue(key, out var clip);
        return clip;
    }
    // ================= Internals =================

    private void Play2D(AudioClip clip, float volume, float pitch)
    {
        uiSource2D.pitch = Mathf.Clamp(pitch, -3f, 3f);
        uiSource2D.PlayOneShot(clip, Mathf.Clamp01(volume) * sfxVolume);
    }

    private void Play3D(AudioClip clip, Vector3 position, float volume, float pitch, float spatialBlend, float minDistance, float maxDistance)
    {
        var go = new GameObject($"SFX3D_{clip.name}");
        go.transform.position = position;
        go.layer = temp3DLayer;

        var src = go.AddComponent<AudioSource>();
        src.clip = clip;
        src.playOnAwake = false;
        src.loop = false;
        src.spatialBlend = Mathf.Clamp01(spatialBlend);
        src.minDistance = Mathf.Max(0.01f, minDistance);
        src.maxDistance = Mathf.Max(src.minDistance + 0.01f, maxDistance);
        src.rolloffMode = AudioRolloffMode.Linear;
        src.outputAudioMixerGroup = sfxMixer;
        src.pitch = Mathf.Clamp(pitch, -3f, 3f);
        src.volume = Mathf.Clamp01(volume) * sfxVolume;

        src.Play();
        Destroy(go, clip.length / Mathf.Max(0.01f, Mathf.Abs(src.pitch)));
    }
}
