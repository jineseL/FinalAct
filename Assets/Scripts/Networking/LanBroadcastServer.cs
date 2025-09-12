using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using Unity.Netcode;
using UnityEngine.UI;

public class LanBroadcastServer : MonoBehaviour
{
    public int broadcastPort = 47777;   // UDP broadcast port
    public int gamePort = 7777;         // NGO server port
    public float broadcastInterval = 1f;

    private UdpClient udpServer;
    private IPEndPoint endPoint;
    private CancellationTokenSource cts;

    private void Start()
    {
        if (NetworkManager.Singleton.IsServer)
        {
            StartBroadcast();
            NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;
        }
    }

    private void OnClientConnected(ulong clientId)
    {
        // Ignore host itself (clientId 0)
        if (clientId != NetworkManager.Singleton.LocalClientId)
        {
            Debug.Log("Client joined. Stopping broadcast...");
            StopBroadcast();
        }
    }

    private void OnClientDisconnected(ulong clientId)
    {
        // If we lost the only client, start advertising again
        if (NetworkManager.Singleton.ConnectedClients.Count <= 1) // only host left
        {
            Debug.Log("Client disconnected. Restarting broadcast...");
            StartBroadcast();
        }
    }

    public void StartBroadcast()
    {
        if (udpServer != null) return; // already broadcasting

        udpServer = new UdpClient();
        udpServer.EnableBroadcast = true;
        endPoint = new IPEndPoint(IPAddress.Broadcast, broadcastPort);

        cts = new CancellationTokenSource();
        var token = cts.Token;

        // Run broadcast in background thread
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
                catch { }

                await System.Threading.Tasks.Task.Delay(
                    (int)(broadcastInterval * 1000), token);
            }
        });
    }

    public void StopBroadcast()
    {
        cts?.Cancel();
        udpServer?.Close();
        udpServer?.Dispose();
        udpServer = null;
        cts = null;
    }

    private void OnDestroy()
    {
        StopBroadcast();

        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;
        }
    }
}
