using UnityEngine;
using UnityEngine.UI;
using Unity.Netcode;
public class UiTestScipt : MonoBehaviour
{
    [SerializeField]
    Button testButton;

    private void Start()
    {
        testButton.onClick.AddListener(() => GameManager1.instance.SpawnPlayerServerRpc(NetworkManager.Singleton.LocalClientId));
    }
}
