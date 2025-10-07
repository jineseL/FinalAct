using System.Collections;
using Unity.Netcode;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

/// Static quit helper you can call from anywhere.
/// Example: AppQuit.Quit();
public static class AppQuit
{
    // Hidden runner to host the coroutine (non-networked, survives shutdown)
    private class Runner : MonoBehaviour { }

    private static Runner runner;
    private static bool isQuitting;

    /// Call this to gracefully shut down NGO and quit the app (or exit play mode in Editor).
    /// framesToWait lets shutdown events process a couple frames.
    public static void Quit(int framesToWaitForShutdown = 2)
    {
        if (isQuitting) return;
        isQuitting = true;

        EnsureRunner();
        runner.StartCoroutine(QuitRoutine(framesToWaitForShutdown));
    }

    private static void EnsureRunner()
    {
        if (runner != null) return;
        var go = new GameObject("~AppQuitRunner");
        Object.DontDestroyOnLoad(go);
        runner = go.AddComponent<Runner>();
    }

    private static IEnumerator QuitRoutine(int framesToWait)
    {
        var nm = NetworkManager.Singleton;
        if (nm != null && nm.IsListening)
        {
            nm.Shutdown();
            // Let shutdown/disconnect events process a few frames
            for (int i = 0; i < Mathf.Max(0, framesToWait); i++)
                yield return null;
        }

#if UNITY_EDITOR
        EditorApplication.ExitPlaymode();
#else
        Application.Quit();
#endif
    }
}
