using UnityEngine;
using Unity.Netcode;
using System.Collections;

public class PlayerHealth : NetworkBehaviour
{
    //todo
    //set up health bar Ui for individual players 
    //

    [SerializeField] private int maxHealth = 100;
    private NetworkVariable<int> currentHealth = new NetworkVariable<int>();

    [SerializeField] private float invulnerableTime = 1f;
    private bool isInvulnerable = false;

    public void initializedPlayerHealth() //call on netwrokspawned in playermanager
    {
        if (IsServer) //is server as i only want server to touch player health
        {
            currentHealth.Value = maxHealth;
        }

        currentHealth.OnValueChanged += OnHealthChanged;
    }

    private void OnDestroy()
    {
        currentHealth.OnValueChanged -= OnHealthChanged;
    }

    private void OnHealthChanged(int oldValue, int newValue)
    {
        Debug.Log($"{gameObject.name} health changed: {oldValue}, {newValue}");
        if (newValue <= 0)
        {
            Die();
        }
    }
    [ServerRpc]
    public void TakeDamageServerRpc(int amount)
    {
        if (isInvulnerable) return;

        currentHealth.Value -= amount;
        currentHealth.Value = Mathf.Clamp(currentHealth.Value, 0, maxHealth);
    }

    [ServerRpc]
    public void MakeInvulnerableServerRpc()
    {
        if (!isInvulnerable)
            StartCoroutine(InvulnerabilityCoroutine());
    }

    private IEnumerator InvulnerabilityCoroutine()
    {
        isInvulnerable = true;
        yield return new WaitForSeconds(invulnerableTime);
        isInvulnerable = false;
    }

    private void Die()
    {
        Debug.Log($"{gameObject.name} has died.");
        // todo Add respawn logic, disable movement
    }
}
