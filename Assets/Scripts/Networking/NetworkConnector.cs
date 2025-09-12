using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;

public class NetworkConnector : MonoBehaviour
{
    public void ConnectToHost(string ip, int port)
    {
        var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
        transport.SetConnectionData(ip, (ushort)port);
        NetworkManager.Singleton.StartClient();
    }
}

