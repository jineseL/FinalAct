using UnityEngine;
using Unity.Netcode;
using UnityEngine.SceneManagement;
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

        var nm = NetworkManager.Singleton;
        if (nm == null)
        {
            Debug.LogError("[GameModeManager] NetworkManager not found in scene.");
            return;
        }

        // Start host if not already
        if (!nm.IsServer && !nm.IsClient)
        {
            if (!nm.StartHost())
            {
                Debug.LogError("[GameModeManager] Failed to StartHost().");
                return;
            }
        }

        // Do a fade, then network-load the target scene as the server
        StartCoroutine(FadeThenNetworkLoad(Loader.Scene.RestScene));
    }

    private IEnumerator FadeThenNetworkLoad(Loader.Scene targetScene)
    {
        // Spawn fade canvas
        GameObject fadeObj = null;
        Animator anim = null;

        if (fadeOutCanvas != null)
        {
            fadeObj = Instantiate(fadeOutCanvas);
            anim = fadeObj.GetComponent<Animator>();
        }

        // Wait for the fade anim length if present, else a small fallback delay
        float wait = 0.35f;
        if (anim != null)
        {
            // If your fade uses a specific state, you can query that state’s length
            var st = anim.GetCurrentAnimatorStateInfo(0);
            if (st.length > 0.01f) wait = st.length;
        }
        yield return new WaitForSeconds(wait);

        // Server/host performs the network scene load so all netobjs spawn correctly
        var nm = NetworkManager.Singleton;
        if (nm != null && nm.IsServer)
        {
            nm.SceneManager.LoadScene(targetScene.ToString(), LoadSceneMode.Single);
        }
        else
        {
            Debug.LogWarning("[GameModeManager] Not server; cannot network-load scene.");
            // Fallback (not recommended for NGO): SceneManager.LoadScene(targetScene.ToString());
        }

        // Optional: destroy fade after a moment
        // if (fadeObj) Destroy(fadeObj, 1f);
    }
}
