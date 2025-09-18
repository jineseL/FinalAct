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
    private bool listening;

    public Action GameFound;

    // Main-thread handoff
    private volatile bool hostFoundFlag;
    private string foundIp;
    private int foundPort;

    public void JoinLobby()
    {
        if (listening) return;

        udpClient = new UdpClient(broadcastPort) { EnableBroadcast = true };
        listening = true;

        listenThread = new Thread(ListenLoop) { IsBackground = true };
        listenThread.Start();
    }

    private void ListenLoop()
    {
        var endPoint = new IPEndPoint(IPAddress.Any, broadcastPort);

        while (listening)
        {
            try
            {
                byte[] data = udpClient.Receive(ref endPoint); // blocking
                string message = Encoding.UTF8.GetString(data);

                if (message.StartsWith("GameLobby:"))
                {
                    // Capture results for main thread
                    foundPort = int.Parse(message.Split(':')[1]);
                    foundIp = endPoint.Address.ToString();
                    hostFoundFlag = true;

                    // Stop listening
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
        // Main thread: safe to touch Unity/NGO
        if (hostFoundFlag)
        {
            hostFoundFlag = false;
            ConnectToHost(foundIp, foundPort);
            GameFound?.Invoke(); // Safe now (main thread)
            foundIp = null;
        }
    }

    private void ConnectToHost(string ip, int port)
    {
        var nm = NetworkManager.Singleton;
        var transport = nm.GetComponent<UnityTransport>();
        transport.SetConnectionData(ip, (ushort)port);
        nm.StartClient();
    }
    public void StopListening()
    {
        // Signal the thread to exit its while loop
        listening = false;

        // Close & dispose the socket (will unblock Receive)
        try { udpClient?.Close(); } catch { }
        try { udpClient?.Dispose(); } catch { }
        udpClient = null;

        // Wait for the thread to finish instead of Abort() (avoids TLS allocator warnings)
        if (listenThread != null)
        {
            if (listenThread.IsAlive)
            {
                try { listenThread.Join(100); } catch { /* ignore */ }
            }
            listenThread = null;
        }
    }

    private void OnDestroy()
    {
        StopListening();
        // If you subscribed handlers somewhere, also unsubscribe:
        // GameFound = null;  // optional
    }
    /*private void OnDestroy()
    {
        listening = false;

        try { udpClient?.Close(); } catch { }
        udpClient = null;

        if (listenThread != null && listenThread.IsAlive)
        {
            // Graceful end instead of Abort (avoids TLS allocator warnings)
            listenThread.Join();
        }
    }*/
}
