using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

public class MainMenuUI : MonoBehaviour
{
    //if there is new button, add them to intratable functions
    [SerializeField] Button singlePlayerButton;
    [SerializeField] Button multiPlayerButton;
    [SerializeField] Button settingButton;
    [SerializeField] Button quitButton;
    [SerializeField] GameObject MultiplayerUI;

    private void Awake()
    {
        singlePlayerButton.onClick.AddListener(() =>
        {
            // todo go to rest room game scene
            // play animation?
            // use a fade in fade out, go to loading screen
            GameModeManager.Instance.StartSingleplayer();
            AllButtonNonInteractable();
        });

        multiPlayerButton.onClick.AddListener(() =>
        {
            // todo enable multiplayer UI and disable This UI
            // do it via animation
            AllButtonNonInteractable();
            FadeAwayButtons();
           
        });
        settingButton.onClick.AddListener(() =>
        {
            // todo settings
            // with animation
            AllButtonNonInteractable();
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

    
    public void AllButtonInteractable() 
    {
        singlePlayerButton.interactable = true;
        multiPlayerButton.interactable = true;
        settingButton.interactable = true;
        quitButton.interactable = true;
    }
    public void AllButtonNonInteractable()
    {
        singlePlayerButton.interactable = false;
        multiPlayerButton.interactable = false;
        settingButton.interactable = false;
        quitButton.interactable = false;
    }
    public void FadeAwayButtons()
    {
        GetComponent<Animator>().Play("MainMenuButtonsFade");
    }
    public void setUnactive()
    {
        gameObject.SetActive(false);
    }
    public void SetActiveMultiplayerUI()
    {
        MultiplayerUI.SetActive(true);
    }
}
