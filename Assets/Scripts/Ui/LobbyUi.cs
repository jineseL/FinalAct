using UnityEngine;
using UnityEngine.UI;
using Unity.Netcode;
using System.Collections;
public class LobbyUi : MonoBehaviour
{
    [SerializeField] Button createGameButton;
    [SerializeField] Button joinGameButton;
    [SerializeField] Button backButton;
    [SerializeField] GameObject MainMenuUiCanvas;
    [SerializeField] GameObject ServerScript;
    [SerializeField] GameObject ClientScript;
    [SerializeField] GameObject HostUI;

    private void Awake()
    {
        createGameButton.onClick.AddListener(() =>
        {
            NetworkManager.Singleton.StartHost();
            Instantiate(ServerScript);
            AllButtonNonInteractable();
            AllButtonFadeAwayToMLobby();
        });
        joinGameButton.onClick.AddListener(() =>
        {
            Instantiate(ClientScript);
            ClientScript.GetComponent<LanBroadcastClient>().JoinLobby();
        });
        backButton.onClick.AddListener(() =>
        {
            AllButtonNonInteractable();
            AllButtonFadeAway();
            NetworkManager.Singleton.Shutdown();
        });

    }

    //helper functions for non networking
    #region Non NGO Functions
    public void MainMenuUISetActive()
    {
        MainMenuUiCanvas.SetActive(true);
    }
    public void MultiplayerLobbyUISetActive()
    {
        HostUI.SetActive(true);
    }
    public void AllButtonInteractable()
    {
        createGameButton.interactable = true;
        joinGameButton.interactable = true;
        backButton.interactable = true;
    }
    public void AllButtonNonInteractable()
    {
        createGameButton.interactable = false;
        joinGameButton.interactable = false;
        backButton.interactable = false;
    }
    public void AllButtonFadeAway()
    {
        GetComponent<Animator>().Play("MultiplayerButtonFadeAway");
    }
    public void AllButtonFadeAwayToMLobby()
    {
        GetComponent<Animator>().Play("MultiplayerButtonFadeAwayToLobby");
    }
    public void SetUnactive()
    {
        gameObject.SetActive(false);
    }
    #endregion Non NGO Functions

}
