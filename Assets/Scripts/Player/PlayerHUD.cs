using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class PlayerHUD : NetworkBehaviour
{
    [Header("UI")]
    [SerializeField] private Canvas hudCanvas;
    [SerializeField] private Image crosshair;
    [SerializeField] private Slider healthBar;
    [SerializeField] private GameObject ammoPanel;
    [SerializeField] private TMP_Text ammoText;
    [SerializeField] private GameObject bossPanel;
    [SerializeField] private Slider bossSlider;

    [Header("Refs")]
    [SerializeField] private PlayerHealth playerHealth;           // assign on prefab or GetComponent
    [SerializeField] private PlayerWeaponManager weaponManager;   // assign on prefab or GetComponent

    // Cache
    private Weapons cachedWeapon;
    private int lastAmmo = -1;

    private void Awake()
    {
        // Safe auto-wiring
        if (!playerHealth) playerHealth = GetComponent<PlayerHealth>();
        if (!weaponManager) weaponManager = GetComponent<PlayerWeaponManager>();

        // Initial UI state
        if (ammoPanel) ammoPanel.SetActive(false);
        if (bossPanel) bossPanel.SetActive(false);
    }

    public override void OnNetworkSpawn()
    {
        // Only the owner sees their HUD
        if (!IsOwner)
        {
            if (hudCanvas) hudCanvas.enabled = false;
            enabled = false;
            return;
        }

        if (hudCanvas) hudCanvas.enabled = true;
    }

    private void Update()
    {
        if (!IsOwner) return;

        UpdateHealth();
        UpdateAmmo();
        UpdateBossBar();
    }

    private void UpdateHealth()
    {
        if (!healthBar || !playerHealth) return;

        float max = Mathf.Max(1f, playerHealth.MaxHealth);
        float val = Mathf.Clamp(playerHealth.CurrentHealth, 0f, max);
        healthBar.value = val / max;
    }

    private void UpdateAmmo()
    {
        if (!weaponManager) return;

        var w = weaponManager.currentWeapon;
        bool hasWeapon = (w != null);

        if (ammoPanel) ammoPanel.SetActive(hasWeapon);

        if (!hasWeapon || ammoText == null) return;

        // Only refresh when changed (cheap)
        if (w != cachedWeapon || w.currentAmmoCount != lastAmmo)
        {
            cachedWeapon = w;
            lastAmmo = w.currentAmmoCount;
            int max = w.maxAmmo;
            ammoText.text = $"{lastAmmo} / {max}";
        }
    }

    // Boss discovery: show a bar when a boss with BossHealth exists in scene
    private BossHealth cachedBoss;

    private void UpdateBossBar()
    {
        // Find on first need; very cheap check
        if (cachedBoss == null)
        {
            // Prefer by tag "Boss" if you set it; otherwise fallback to FindObjectOfType
            var bossGO = GameObject.FindGameObjectWithTag("Boss");
            if (bossGO) cachedBoss = bossGO.GetComponent<BossHealth>();
            if (!cachedBoss) cachedBoss = FindFirstObjectByType<BossHealth>();
        }

        if (!bossPanel || !bossSlider) return;

        if (cachedBoss != null && cachedBoss.IsAlive)
        {
            bossPanel.SetActive(true);
            float max = Mathf.Max(1f, cachedBoss.MaxHP);
            bossSlider.value = Mathf.Clamp01(cachedBoss.CurrentHP / max);
        }
        else
        {
            bossPanel.SetActive(false);
        }
    }
}

