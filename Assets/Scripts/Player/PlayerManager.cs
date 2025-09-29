using UnityEngine;
using Unity.Netcode;
using System.Collections.Generic;

public class PlayerManager : NetworkBehaviour
{
    [Header("Gameobject Disable For Owner")]
    [SerializeField] List<GameObject> ToDisableForOwner = new List<GameObject>();
    [SerializeField] List<GameObject> ToDisableForNonOwner = new List<GameObject>();

    PlayerHealth playerHealth;
    

    public override void OnNetworkSpawn()
    {
        DisableForOwner();
        DisableForNonOwner();

        if ((playerHealth = GetComponent<PlayerHealth>()) is not null)
        {
            playerHealth.InitializePlayerHealth();
        }
        else Debug.Log("player health missing");
    }

    #region Helper Functions
    private void DisableForOwner()
    {
        if(IsOwner)
        for(int i =0;i< ToDisableForOwner.Count; i++)
        {
            ToDisableForOwner[i].SetActive(false);
        }
    }
    private void DisableForNonOwner()
    {
        if (!IsOwner)
            for (int i = 0; i < ToDisableForNonOwner.Count; i++)
            {
                ToDisableForNonOwner[i].SetActive(false);
            }
    }
    #endregion Helper Functions
}
