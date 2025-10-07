using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;

[DisallowMultipleComponent]
public class LanBroadcastServer : MonoBehaviour
{
    [Header("Broadcast Settings")]
    [Tooltip("UDP broadcast port the client listens on.")]
    public int broadcastPort = 47777;

    [Tooltip("NGO server port to advertise. If 0, will be auto-read from UnityTransport on server start.")]
    public int gamePort = 7777;

    [Tooltip("Seconds between broadcast packets.")]
    public float broadcastInterval = 1f;

    [Header("Lifecycle")]
    [Tooltip("Keep this object across scene loads (recommended if NetworkManager is also persistent).")]
    public bool dontDestroyOnLoad = true;

    private UdpClient udpServer;
    private IPEndPoint endPoint;
    private CancellationTokenSource cts;

    private NetworkManager nm;
    private bool serverStartedHandled = false;
    private bool broadcasting = false;

    private void Awake()
    {
        nm = NetworkManager.Singleton;
        if (dontDestroyOnLoad) DontDestroyOnLoad(gameObject);
    }

    private void OnEnable()
    {
        if (nm != null)
        {
            nm.OnServerStarted += OnServerStarted;
            // Late-subscribe fix: if server is already running, handle immediately
            if (nm.IsServer) SafeHandleServerStarted();
        }
    }

    private void OnDisable()
    {
        if (nm != null)
            nm.OnServerStarted -= OnServerStarted;
    }

    private void OnDestroy()
    {
        StopBroadcast();
        if (nm != null)
        {
            nm.OnClientConnectedCallback -= OnClientConnected;
            nm.OnClientDisconnectCallback -= OnClientDisconnected;
            nm.OnServerStarted -= OnServerStarted;
        }
    }

    private void OnValidate()
    {
        broadcastInterval = Mathf.Max(0.05f, broadcastInterval);
        broadcastPort = Mathf.Clamp(broadcastPort, 1024, 65535);
        gamePort = Mathf.Clamp(gamePort, 0, 65535);
    }

    // ========== NGO hooks ==========

    private void OnServerStarted()
    {
        SafeHandleServerStarted();
    }

    private void SafeHandleServerStarted()
    {
        if (serverStartedHandled) return;
        if (nm == null || !nm.IsServer) return;

        // Auto-read UnityTransport port if not set
        if (gamePort == 0)
        {
            var utp = nm.GetComponent<UnityTransport>();
            if (utp != null) gamePort = utp.ConnectionData.Port;
        }

        Debug.Log($"[LanBroadcastServer] Server started. Advertising port {gamePort} on UDP {broadcastPort}.");
        StartBroadcast();

        nm.OnClientConnectedCallback -= OnClientConnected;
        nm.OnClientConnectedCallback += OnClientConnected;

        nm.OnClientDisconnectCallback -= OnClientDisconnected;
        nm.OnClientDisconnectCallback += OnClientDisconnected;

        serverStartedHandled = true;
    }

    private void OnClientConnected(ulong clientId)
    {
        if (nm == null || !nm.IsServer) return;

        // Ignore the host (it is also a connected client)
        if (clientId != nm.LocalClientId)
        {
            Debug.Log("[LanBroadcastServer] Client joined. Stop broadcast.");
            StopBroadcast();
        }
    }

    private void OnClientDisconnected(ulong clientId)
    {
        if (nm == null || !nm.IsServer) return;

        // If only host remains, advertise again
        if (nm.ConnectedClients.Count <= 1)
        {
            Debug.Log("[LanBroadcastServer] All clients left. Restart broadcast.");
            StartBroadcast();
        }
    }

    // ========== Broadcast control ==========

    public void StartBroadcast()
    {
        if (broadcasting) return;
        if (nm == null || !nm.IsServer)
        {
            Debug.Log("[LanBroadcastServer] StartBroadcast ignored (not server).");
            return;
        }

        try
        {
            udpServer = new UdpClient { EnableBroadcast = true };
            endPoint = new IPEndPoint(IPAddress.Broadcast, broadcastPort);

            cts = new CancellationTokenSource();
            var token = cts.Token;

            // Background loop
            ThreadPool.QueueUserWorkItem(async _ =>
            {
                broadcasting = true;
                Debug.Log($"[LanBroadcastServer] Broadcasting every {broadcastInterval:0.###}s on UDP {broadcastPort}, lobby port {gamePort}");

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        string message = $"GameLobby:{gamePort}";
                        byte[] data = Encoding.UTF8.GetBytes(message);
                        await udpServer.SendAsync(data, data.Length, endPoint);
                    }
                    catch { /* ignore send errors */ }

                    try
                    {
                        await System.Threading.Tasks.Task.Delay((int)(broadcastInterval * 1000), token);
                    }
                    catch { /* canceled */ }
                }

                broadcasting = false;
            });
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[LanBroadcastServer] StartBroadcast error: {ex.Message}");
            StopBroadcast();
        }
    }

    public void StopBroadcast()
    {
        try
        {
            cts?.Cancel();
            cts?.Dispose();
            cts = null;

            if (udpServer != null)
            {
                udpServer.Close();
                udpServer.Dispose();
                udpServer = null;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[LanBroadcastServer] StopBroadcast error: {ex.Message}");
        }
        finally
        {
            broadcasting = false;
        }
    }
}
