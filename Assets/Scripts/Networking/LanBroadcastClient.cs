using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using System;

public class LanBroadcastClient : MonoBehaviour
{
    public int broadcastPort = 47777; // must match server
    private UdpClient udpClient;
    private Thread listenThread;
    private volatile bool listening;

    public Action GameFound;

    // main-thread handoff
    private volatile bool hostFoundFlag;
    private string foundIp;
    private int foundPort;

    public void JoinLobby()
    {
        // Don’t listen if we’re the host/server in this instance
        if (NetworkManager.Singleton && (NetworkManager.Singleton.IsServer || NetworkManager.Singleton.IsHost))
        {
            Debug.Log("[LanBroadcastClient] Skipping listen; this instance is host/server.");
            return;
        }

        if (listening) return;

        try
        {
            // Allow multiple processes on same machine to bind this port
            udpClient = new UdpClient(AddressFamily.InterNetwork);
            udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            // On Windows, avoid exclusive bind
            udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ExclusiveAddressUse, false);
#endif
            udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, broadcastPort));
            udpClient.EnableBroadcast = true;

            listening = true;
            listenThread = new Thread(ListenLoop) { IsBackground = true };
            listenThread.Start();
            Debug.Log($"[LanBroadcastClient] Listening for broadcasts on {broadcastPort}");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[LanBroadcastClient] Failed to bind port {broadcastPort}: {ex.Message}");
            StopListening();
        }
    }
    /*public void JoinLobby()
    {
        if (listening) return;

        try
        {
            udpClient = new UdpClient(AddressFamily.InterNetwork);
            udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udpClient.EnableBroadcast = true;
            udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, broadcastPort));
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[LanBroadcastClient] Bind failed: {ex.Message}");
            return;
        }

        listening = true;
        Debug.Log($"[LanBroadcastClient] Listening for broadcasts on {broadcastPort}");
        listenThread = new Thread(ListenLoop) { IsBackground = true };
        listenThread.Start();
    }*/

    private void ListenLoop()
    {
        var endPoint = new IPEndPoint(IPAddress.Any, broadcastPort);

        while (listening)
        {
            try
            {
                byte[] data = udpClient.Receive(ref endPoint); // blocking
                string message = Encoding.UTF8.GetString(data);
                Debug.Log("abc");
                if (message.StartsWith("GameLobby:"))
                {
                    // parse port
                    var parts = message.Split(':');
                    if (parts.Length >= 2 && int.TryParse(parts[1], out var port))
                    {
                        foundPort = port;
                        foundIp = endPoint.Address.ToString();
                        hostFoundFlag = true;
                    }

                    // done listening for now
                    listening = false;
                    udpClient.Close();
                    udpClient = null;
                    return;
                }
            }
            catch
            {
                // swallow errors during shutdown
            }
        }
    }

    private void Update()
    {
        if (hostFoundFlag)
        {
            hostFoundFlag = false;
            ConnectToHost(foundIp, foundPort);
            GameFound?.Invoke();
            foundIp = null;
        }
    }

    private void ConnectToHost(string ip, int port)
    {
        var nm = NetworkManager.Singleton;
        if (!nm) return;
        var transport = nm.GetComponent<UnityTransport>();
        transport.SetConnectionData(ip, (ushort)port);
        Debug.Log($"[LanBroadcastClient] Connecting to {ip}:{port}");
        nm.StartClient();
    }

    public void StopListening()
    {
        listening = false;

        try { udpClient?.Close(); } catch { }
        try { udpClient?.Dispose(); } catch { }
        udpClient = null;

        if (listenThread != null)
        {
            if (listenThread.IsAlive)
            {
                try { listenThread.Join(100); } catch { }
            }
            listenThread = null;
        }
    }

    private void OnDestroy()
    {
        StopListening();
    }
}
