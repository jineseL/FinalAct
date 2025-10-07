using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Netcode;

public static class Loader
{
    //scenes here have to have the exact same spelling as the scene name
    public enum Scene
    {
        RestScene, //rest room
        LobbyScene, //main menu
        LoadingScene, //loading screen
        ArenaScene,
        PresentationArenaScene, //pluto old
    }

    private static Scene targetScene;

    public static void Load(Scene targetscene)
    {
        Loader.targetScene = targetscene;
        SceneManager.LoadScene(Scene.LoadingScene.ToString());
    }
    public static void LoadNetwork(Scene targetScene)
    {
        NetworkManager.Singleton.SceneManager.LoadScene(targetScene.ToString(), LoadSceneMode.Single);
    }
    public static void LoaderCallBack()
    {
        SceneManager.LoadScene(targetScene.ToString());
    }
}
