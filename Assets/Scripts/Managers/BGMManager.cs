using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;

[DisallowMultipleComponent]
public class BGMManager : MonoBehaviour
{
    public static BGMManager I { get; private set; }

    [Header("Mixer (optional)")]
    [SerializeField] private AudioMixerGroup musicMixer;

    [Header("Library (optional, for keyed lookups)")]
    [SerializeField] private BgmEntry[] bgmTable;
    private Dictionary<string, AudioClip> bgmDict;

    [Header("Defaults")]
    [Range(0f, 1f)] public float musicVolume = 1f;
    [SerializeField] private float defaultFade = 0.75f;

    // Two sources for crossfades
    private AudioSource srcA, srcB;
    private bool aIsActive = true;

    private Coroutine fadeCo;
    private string currentKey;
    private AudioClip currentClip;

    [System.Serializable]
    public struct BgmEntry
    {
        public string key;     // e.g., "arena", "boss_phase1"
        public AudioClip clip; // assign in Inspector
    }

    private void Awake()
    {
        // Singleton guard
        if (I != null && I != this) { Destroy(gameObject); return; }
        I = this;
        DontDestroyOnLoad(gameObject);

        // Build dict (optional)
        if (bgmTable != null && bgmTable.Length > 0)
        {
            bgmDict = new Dictionary<string, AudioClip>(bgmTable.Length);
            foreach (var e in bgmTable)
            {
                if (!string.IsNullOrEmpty(e.key) && e.clip && !bgmDict.ContainsKey(e.key))
                    bgmDict.Add(e.key, e.clip);
            }
        }

        // Create AudioSources
        srcA = CreateSource("BGM_A");
        srcB = CreateSource("BGM_B");
    }

    private AudioSource CreateSource(string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var src = go.AddComponent<AudioSource>();
        src.playOnAwake = false;
        src.loop = true;
        src.spatialBlend = 0f; // 2D
        src.outputAudioMixerGroup = musicMixer;
        src.volume = 0f;
        return src;
    }

    private void OnValidate()
    {
        // reflect volume change in editor
        if (srcA) srcA.volume = (IsPlayingA ? musicVolume : srcA.volume);
        if (srcB) srcB.volume = (IsPlayingA ? srcB.volume : musicVolume);
    }

    private bool IsPlayingA => aIsActive;

    // ====================== Static API ======================

    /// Play a keyed BGM (looks up in bgmTable). Crossfades from current if different.
    public static void Play(string key, float fade = -1f, float volume = -1f, bool restartIfSame = false)
    {
        if (I == null || string.IsNullOrEmpty(key)) return;
        if (I.bgmDict == null || !I.bgmDict.TryGetValue(key, out var clip)) return;
        I.PlayClip(clip, fade, volume, restartIfSame);
        I.currentKey = key;
    }

    /// Play directly from an AudioClip. Crossfades if different.
    public static void Play(AudioClip clip, float fade = -1f, float volume = -1f, bool restartIfSame = false)
    {
        if (I == null || clip == null) return;
        I.currentKey = null;
        I.PlayClip(clip, fade, volume, restartIfSame);
    }

    /// Stop the current BGM (optional fade).
    public static void Stop(float fade = -1f)
    {
        if (I == null) return;
        I.StopInternal(fade);
    }

    /// Is the given key currently playing (by logical identity)?
    public static bool IsPlaying(string key)
    {
        return I != null && !string.IsNullOrEmpty(key) && I.currentKey == key;
    }

    /// Change global BGM volume [0..1].
    public static float Volume
    {
        get => I != null ? I.musicVolume : 1f;
        set
        {
            if (I == null) return;
            I.musicVolume = Mathf.Clamp01(value);
            // adjust active source immediately
            if (I.aIsActive) I.srcA.volume = I.musicVolume;
            else I.srcB.volume = I.musicVolume;
        }
    }

    // ====================== Internals ======================

    private void PlayClip(AudioClip clip, float fade, float volume, bool restartIfSame)
    {
        float f = (fade >= 0f) ? fade : defaultFade;
        float v = (volume >= 0f) ? Mathf.Clamp01(volume) : musicVolume;

        // If same clip and not forcing restart: do nothing
        if (!restartIfSame && currentClip == clip && IsAnyPlaying())
        {
            // Make sure active source volume matches requested v
            GetActive().volume = v;
            musicVolume = v;
            return;
        }

        // Swap active to the other source for crossfade
        var from = GetActive();
        var to = GetInactive();

        // Configure new target
        to.clip = clip;
        to.loop = true;
        to.volume = 0f;
        to.Play();

        currentClip = clip;
        musicVolume = v;

        // Crossfade
        if (fadeCo != null) StopCoroutine(fadeCo);
        fadeCo = StartCoroutine(Crossfade(from, to, f, v));

        // Flip active
        aIsActive = !aIsActive;
    }

    private void StopInternal(float fade)
    {
        float f = (fade >= 0f) ? fade : defaultFade;
        if (fadeCo != null) StopCoroutine(fadeCo);
        fadeCo = StartCoroutine(FadeOutBothAndStop(f));
        currentKey = null;
        currentClip = null;
    }

    private bool IsAnyPlaying()
    {
        return (srcA && srcA.isPlaying) || (srcB && srcB.isPlaying);
    }

    private AudioSource GetActive() => aIsActive ? srcA : srcB;
    private AudioSource GetInactive() => aIsActive ? srcB : srcA;

    private IEnumerator Crossfade(AudioSource from, AudioSource to, float duration, float targetVolume)
    {
        if (duration <= 0.0001f)
        {
            if (from) { from.Stop(); from.volume = 0f; }
            if (to) { to.volume = targetVolume; }
            yield break;
        }

        float t = 0f;
        float startFrom = from ? from.volume : 0f;
        float startTo = to ? to.volume : 0f;

        while (t < duration)
        {
            t += Time.unscaledDeltaTime;
            float k = Mathf.Clamp01(t / duration);

            if (from) from.volume = Mathf.Lerp(startFrom, 0f, k);
            if (to) to.volume = Mathf.Lerp(startTo, targetVolume, k);

            yield return null;
        }

        if (from) { from.volume = 0f; from.Stop(); }
        if (to) { to.volume = targetVolume; }
        fadeCo = null;
    }

    private IEnumerator FadeOutBothAndStop(float duration)
    {
        var a = srcA; var b = srcB;

        if (duration <= 0.0001f)
        {
            if (a) { a.Stop(); a.volume = 0f; }
            if (b) { b.Stop(); b.volume = 0f; }
            yield break;
        }

        float t = 0f;
        float a0 = a ? a.volume : 0f;
        float b0 = b ? b.volume : 0f;

        while (t < duration)
        {
            t += Time.unscaledDeltaTime;
            float k = Mathf.Clamp01(t / duration);
            if (a) a.volume = Mathf.Lerp(a0, 0f, k);
            if (b) b.volume = Mathf.Lerp(b0, 0f, k);
            yield return null;
        }

        if (a) { a.Stop(); a.volume = 0f; }
        if (b) { b.Stop(); b.volume = 0f; }
        fadeCo = null;
    }
}
