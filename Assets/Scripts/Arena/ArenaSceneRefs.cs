using UnityEngine;
using Unity.Cinemachine;

public class ArenaSceneRefs : MonoBehaviour
{
    public static ArenaSceneRefs Instance { get; private set; }

    [Header("Intro Toggle")]
    public bool runIntro = true;           // <-- turn off to skip cutscene

    [Header("Cameras / UI")]
    public CinemachineCamera cutsceneCam;   // v3 camera for top view
    public GameObject cutsceneCanvas;       // Overlay canvas: "Cutscene playing..."

    [Header("Spawns")]
    public Transform hostSpawn;
    public Transform clientSpawn;

    [Header("Timing")]
    public float cutsceneDuration = 3f;

    private void Awake()
    {
        Instance = this;
        if (cutsceneCanvas) cutsceneCanvas.SetActive(false);
        if (cutsceneCam) cutsceneCam.Priority.Value = 10; // default lower than player
    }
}

