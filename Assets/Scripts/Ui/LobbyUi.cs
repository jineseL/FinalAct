using UnityEngine;
using UnityEngine.UI;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using System;

public class LobbyUi : MonoBehaviour
{
    [SerializeField] Button createGameButton;
    [SerializeField] Button joinGameButton;
    [SerializeField] Button backButton;
    [SerializeField] GameObject findingHostText;
    [SerializeField] GameObject MainMenuUiCanvas; // first menu (for going back)
    [SerializeField] GameObject ServerScript;      // prefab with LanBroadcastServer
    [SerializeField] GameObject ClientScript;      // prefab with LanBroadcastClient
    [SerializeField] GameObject HostUI;            // Multiplayer lobby UI
    [SerializeField] GameObject lobbyPreviewManagerPrefab; //manager for spawning player preview

    [SerializeField] ushort gamePort = 7777;       // must match LanBroadcastServer.gamePort
    GameObject container;                          // spawned holder (server/client script)
    LanBroadcastServer serverRef;
    LanBroadcastClient clientRef;

    void Awake()
    {
        createGameButton.onClick.AddListener(() =>
        {
            // Configure transport to listen on all interfaces (LAN) before hosting
            var utp = NetworkManager.Singleton.GetComponent<UnityTransport>();
            utp.SetConnectionData("0.0.0.0", gamePort);

            NetworkManager.Singleton.StartHost();

            // Start broadcasting
            container = Instantiate(ServerScript);
            OnCreateLobby();
            AllButtonNonInteractable();
            AllButtonFadeAwayToMLobby();
            MultiplayerLobbyUISetActive();
        });

        joinGameButton.onClick.AddListener(() =>
        {
            // UI state
            NGOButtonNonInteractable();
            findingHostText.SetActive(true);

            // Start listening; auto-connect on first host found
            container = Instantiate(ClientScript);
            var client = container.GetComponent<LanBroadcastClient>();
            client.GameFound += OnHostFound;
            client.JoinLobby();
        });

        backButton.onClick.AddListener(() =>
        {
            AllButtonNonInteractable();
            AllButtonFadeAway();

            // --- CLEANUP CLIENT (your snippet goes here) ---
            if (clientRef != null)
            {
                clientRef.GameFound -= FindingHostTextSetUnactive;
                clientRef.GameFound -= AllButtonNonInteractable;
                clientRef.GameFound -= AllButtonFadeAwayToMLobby;
                clientRef.StopListening();
                clientRef = null;
            }

            // --- CLEANUP SERVER ---
            if (serverRef != null)
            {
                serverRef.StopBroadcast();
                serverRef = null;
            }

            // Destroy the helper container if it exists
            if (container != null)
            {
                Destroy(container);
                container = null;
            }

            // Shutdown NGO (works for host or client)
            if (NetworkManager.Singleton.IsClient || NetworkManager.Singleton.IsServer)
                NetworkManager.Singleton.Shutdown();

            // Return to previous menu
            findingHostText.SetActive(false);
            HostUI.SetActive(false);
            MainMenuUiCanvas.SetActive(true);
            AllButtonInteractable();
        });
    }

    void OnHostFound()
    {
        // This runs on main thread (from client script Update)
        findingHostText.SetActive(false);
        AllButtonNonInteractable();
        AllButtonFadeAwayToMLobby();
        MultiplayerLobbyUISetActive();
    }
    public void OnCreateLobby()
    {
       

        // Spawn the networked controller so it can send RPCs
        var go = Instantiate(lobbyPreviewManagerPrefab);
        go.GetComponent<NetworkObject>().Spawn();
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
        FindingHostTextSetUnactive();
    }
    public void NGOButtonNonInteractable()
    {
        createGameButton.interactable = false;
        joinGameButton.interactable = false;
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

    public void FindingHostTextSetUnactive()
    {
        findingHostText.SetActive(false);
    }
    #endregion Non NGO Functions

}
