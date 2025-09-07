using UnityEngine;
using Unity.Netcode;
public class GameManager1:NetworkBehaviour
{
    //for testing
    //[SerializeField] GameObject cameraGO;


    public static GameManager1 instance { get; private set; }
    [SerializeField] GameObject playerPrefab;
    [SerializeField] Transform hostPositionSpawn;
    [SerializeField] Transform clientPositionSpawn;
    /*GameObject player1;
    GameObject player2;*/
    private void Awake()
    {
        if(instance != null)
        {
            Debug.LogError("More than one Gamemanger instance!");

        }
        instance = this;
    }
    private void Start()
    {
        //testing
        //cameraGO.SetActive(false);
    }

    //[ServerRpc(RequireOwnership = false)]
    [Rpc(SendTo.Server)]
    public void SpawnPlayerServerRpc(ulong requestingClientId)
    {
        Debug.Log("test");
        // Decide spawn point
        Vector3 spawnPos = (requestingClientId == 0)
            ? hostPositionSpawn.position
            : clientPositionSpawn.position;

        // Spawn and give ownership
        var playerObj = Instantiate(playerPrefab, spawnPos, Quaternion.identity);
        playerObj.GetComponent<NetworkObject>().SpawnAsPlayerObject(requestingClientId);
    }
}
