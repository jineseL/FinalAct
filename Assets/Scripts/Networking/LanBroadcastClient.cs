using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;

public class LanBroadcastClient : MonoBehaviour
{
    public int broadcastPort = 47777; // must match server
    private UdpClient udpClient;
    private Thread listenThread;
    private bool listening;

    public void JoinLobby()
    {
        if (listening) return; // already listening

        udpClient = new UdpClient(broadcastPort);
        udpClient.EnableBroadcast = true;
        listening = true;

        listenThread = new Thread(ListenLoop);
        listenThread.Start();
    }

    private void ListenLoop()
    {
        IPEndPoint endPoint = new IPEndPoint(IPAddress.Any, broadcastPort);

        while (listening)
        {
            try
            {
                byte[] data = udpClient.Receive(ref endPoint);
                string message = Encoding.UTF8.GetString(data);

                if (message.StartsWith("GameLobby:"))
                {
                    int port = int.Parse(message.Split(':')[1]);
                    string hostIp = endPoint.Address.ToString();

                    Debug.Log($"Found host at {hostIp}:{port}, joining now...");

                    // Connect immediately
                    ConnectToHost(hostIp, port);

                    // Stop listening after first host is found
                    listening = false;
                    udpClient.Close();
                    return;
                }
            }
            catch { }
        }
    }

    private void ConnectToHost(string ip, int port)
    {
        UnityTransport transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
        transport.SetConnectionData(ip, (ushort)port);
        NetworkManager.Singleton.StartClient();
    }

    private void OnDestroy()
    {
        listening = false;
        udpClient?.Close();
        listenThread?.Abort();
    }
}
