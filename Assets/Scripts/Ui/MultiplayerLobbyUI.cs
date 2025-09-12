using UnityEngine;
using UnityEngine.UI;
using Unity.Netcode;

public class MultiplayerLobbyUI : MonoBehaviour
{
    [SerializeField] private Button startGameBtn;
    [SerializeField] private Button backBtn;

    [SerializeField] private GameObject previousCanvas; // reference to your main multiplayer menu
    [SerializeField] private GameObject thisCanvas;     // this lobby UI

    private LanBroadcastServer broadcastServer;

    private void Awake()
    {
        // Start Game only for host
        startGameBtn.onClick.AddListener(() =>
        {
            if (NetworkManager.Singleton.IsServer) // safety check
            {
                Loader.LoadNetwork(Loader.Scene.RestScene);
            }
        });

        backBtn.onClick.AddListener(() =>
        {
            if (NetworkManager.Singleton.IsServer)
            {
                // Host: stop broadcasting + shut down server
                broadcastServer?.StopBroadcast();
                NetworkManager.Singleton.Shutdown();
                Destroy(broadcastServer.gameObject);
                
            }
            else if (NetworkManager.Singleton.IsClient)
            {
                // Client: disconnect
                NetworkManager.Singleton.Shutdown();
            }

            // Switch back to previous menu UI
            thisCanvas.SetActive(false);
            previousCanvas.SetActive(true);
        });
    }

    private void OnEnable()
    {
        // Hide Start Game by default
        startGameBtn.gameObject.SetActive(false);

        // Subscribe to NGO events
        NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
        NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;

        // Grab broadcaster if host
        if (NetworkManager.Singleton.IsServer)
        {
            broadcastServer = FindFirstObjectByType<LanBroadcastServer>();
        }
    }

    private void OnDisable()
    {
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;
        }
    }

    private void OnClientConnected(ulong clientId)
    {
        // Host only cares when a *client* joins (not itself)
        if (NetworkManager.Singleton.IsServer &&
            clientId != NetworkManager.Singleton.LocalClientId)
        {
            Debug.Log("Client joined! Enabling Start Game button.");
            startGameBtn.gameObject.SetActive(true);

            // Stop broadcasting since lobby is full
            broadcastServer?.StopBroadcast();
        }
    }

    private void OnClientDisconnected(ulong clientId)
    {
        // Host: if the only client leaves, hide Start Game and restart broadcast
        if (NetworkManager.Singleton.IsServer &&
            NetworkManager.Singleton.ConnectedClients.Count <= 1) // only host left
        {
            Debug.Log("Client left. Hiding Start Game and broadcasting again.");
            startGameBtn.gameObject.SetActive(false);
            broadcastServer?.StartBroadcast();
        }
    }
}
