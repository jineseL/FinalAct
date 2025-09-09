using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

public class MainMenuUI : MonoBehaviour
{
    [SerializeField] Button singlePlayerButton;
    [SerializeField] Button multiPlayerButton;
    [SerializeField] Button settingButton;
    [SerializeField] Button quitButton;

    private void Awake()
    {
        singlePlayerButton.onClick.AddListener(() =>
        {
            // todo go to rest room game scene
            // play animation?
            // use a fade in fade out, go to loading screen
            GameModeManager.Instance.StartSingleplayer();
        });

        multiPlayerButton.onClick.AddListener(() =>
        {
            // todo enable multiplayer UI and disable This UI
            // do it via animation
        });
        settingButton.onClick.AddListener(() =>
        {
            // todo settings
            // with animation
        });
        quitButton.onClick.AddListener(() =>
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
    Application.Quit();
#endif
        });
    }
}
