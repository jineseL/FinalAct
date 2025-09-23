using UnityEngine;

public interface IInteractable
{
    /// <summary>
    /// Called when something (usually the player) interacts with this object.
    /// </summary>
    /// <param name="interactor">The GameObject or component doing the interaction.</param>
    void Interact(GameObject interactor);

    /// <summary>
    /// Optional: name/description to show in UI prompts.
    /// </summary>
    string GetInteractionPrompt();

    // Hover start/stop for visual feedback
    void OnHoverEnter();
    void OnHoverExit();
}
