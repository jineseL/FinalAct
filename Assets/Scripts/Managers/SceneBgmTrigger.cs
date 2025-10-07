using UnityEngine;

public class SceneBgmTrigger : MonoBehaviour
{
    [Header("Pick one")]
    [SerializeField] private string bgmKey;
    [SerializeField] private AudioClip bgmClip;

    [Header("Options")]
    [SerializeField] private float fade = 0.75f;
    [SerializeField] private bool restartIfSame = false;

    private void OnEnable()
    {
        if (bgmClip)
            BGMManager.Play(bgmClip, fade, -1f, restartIfSame);
        else if (!string.IsNullOrEmpty(bgmKey))
            BGMManager.Play(bgmKey, fade, -1f, restartIfSame);
    }
}
