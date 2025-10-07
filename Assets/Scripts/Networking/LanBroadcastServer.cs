using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System;
using UnityEngine;
using Unity.Netcode;

public class LanBroadcastServer : MonoBehaviour
{
    public int broadcastPort = 47777;   // UDP broadcast port
    public int gamePort = 7777;         // NGO server port
    public float broadcastInterval = 1f;

    private UdpClient udpServer;
    private IPEndPoint endPoint;
    private CancellationTokenSource cts;

    private void OnEnable()
    {
        if (NetworkManager.Singleton != null)
            NetworkManager.Singleton.OnServerStarted += OnServerStarted;
    }

    private void OnDisable()
    {
        if (NetworkManager.Singleton != null)
            NetworkManager.Singleton.OnServerStarted -= OnServerStarted;
    }

    private void OnServerStarted()
    {
        // If you set the UnityTransport port elsewhere, you can read it here instead of using gamePort.
        // var utp = NetworkManager.Singleton.GetComponent<Unity.Netcode.Transports.UTP.UnityTransport>();
        // if (utp != null) gamePort = utp.ConnectionData.Port;

        StartBroadcast();

        NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
        NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;
    }

    private void OnClientConnected(ulong clientId)
    {
        // Stop advertising when someone joins (except the host itself)
        if (clientId != NetworkManager.Singleton.LocalClientId)
        {
            Debug.Log("[LanBroadcastServer] Client joined  stop broadcast");
            StopBroadcast();
        }
    }

    private void OnClientDisconnected(ulong clientId)
    {
        // If only host remains, advertise again
        if (NetworkManager.Singleton.ConnectedClients.Count <= 1)
        {
            Debug.Log("[LanBroadcastServer] All clients left  restart broadcast");
            StartBroadcast();
        }
    }

    public void StartBroadcast()
    {
        if (udpServer != null) return;

        try
        {
            udpServer = new UdpClient() { EnableBroadcast = true };
            endPoint = new IPEndPoint(IPAddress.Broadcast, broadcastPort);

            cts = new CancellationTokenSource();
            var token = cts.Token;

            ThreadPool.QueueUserWorkItem(async _ =>
            {
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
            });

            Debug.Log($"[LanBroadcastServer] Broadcasting every {broadcastInterval}s on {broadcastPort}, lobby port {gamePort}");
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
    }

    private void OnDestroy()
    {
        StopBroadcast();

        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;
            NetworkManager.Singleton.OnServerStarted -= OnServerStarted;
        }
    }
}
