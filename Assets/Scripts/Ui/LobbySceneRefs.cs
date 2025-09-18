using UnityEngine;

public class LobbySceneRefs : MonoBehaviour
{
    public static LobbySceneRefs Instance { get; private set; }
    public Transform player1Spawn;
    public Transform player2Spawn;

    private void Awake()
    {
        Instance = this;
    }
}
