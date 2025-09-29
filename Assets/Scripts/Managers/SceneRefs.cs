using UnityEngine;

public class SceneRefs : MonoBehaviour
{
    public static SceneRefs Instance { get; private set; }

    public Transform hostSpawn;
    public Transform clientSpawn;

    private void Awake()
    {
        Instance = this; // last loaded wins
    }
}
