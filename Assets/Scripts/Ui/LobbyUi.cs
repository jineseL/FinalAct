using UnityEngine;
using UnityEngine.UI;
using Unity.Netcode;
public class LobbyUi : MonoBehaviour
{
    [SerializeField] Button createGameButton;
    [SerializeField] Button joinGameButton;

    private void Awake()
    {
        createGameButton.onClick.AddListener(() =>
        {
            NetworkManager.Singleton.StartHost();
        });
        joinGameButton.onClick.AddListener(() =>
        {
            NetworkManager.Singleton.StartClient();
        });
        
    }
}
