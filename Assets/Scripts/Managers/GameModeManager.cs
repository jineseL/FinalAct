using UnityEngine;
using System.Collections;

public enum GameMode { Singleplayer, Multiplayer }
public class GameModeManager : MonoBehaviour
{
    public GameObject fadeOutCanvas;
    public static GameModeManager Instance { get; private set; }

    public GameMode CurrentMode { get; private set; }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    public void SetGameMode(GameMode mode)
    {
        CurrentMode = mode;
    }
    public void StartSingleplayer()
    {
        CurrentMode = GameMode.Singleplayer;
        GameObject fadeObj = Instantiate(fadeOutCanvas);

        fadeObj.GetComponent<FadeOutCanvas>().StartCoroutine(fadeObj.GetComponent<FadeOutCanvas>().FadeAndLoad(Loader.Scene.RestScene));
    }

    /*private IEnumerator FadeAndLoad()
    {
        // 1. Instantiate fade canvas
        GameObject fadeObj = Instantiate(fadeOutCanvas);

        // 2. Get Animator
        Animator anim = fadeObj.GetComponent<Animator>();
        if (anim == null)
        {
            Debug.LogError("FadeOutCanvas has no Animator!");
            yield break;
        }

        // 3. Wait for the fade animation to finish
        // Assuming your fade animation is the default state on layer 0
        AnimatorStateInfo stateInfo = anim.GetCurrentAnimatorStateInfo(0);
        float animLength = stateInfo.length;

        yield return new WaitForSeconds(animLength);

        // 4. Load target scene after fade
        Loader.Load(Loader.Scene.RestScene);
    }*/

}
