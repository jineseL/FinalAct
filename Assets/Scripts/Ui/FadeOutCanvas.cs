using UnityEngine;
using System.Collections;

public class FadeOutCanvas : MonoBehaviour
{
    public IEnumerator FadeAndLoad(Loader.Scene scene)
    {
       /* // 1. Instantiate fade canvas
        GameObject fadeObj = Instantiate(fadeOutCanvas);*/

        // 2. Get Animator
        Animator anim = gameObject.GetComponent<Animator>();
        if (anim == null)
        {
            Debug.LogError("FadeOutCanvas has no Animator!");
            yield break;
        }

        // 3. Wait for the fade animation to finish
        // Assuming your fade animation is the default state on layer 0
        AnimatorStateInfo stateInfo = anim.GetCurrentAnimatorStateInfo(0);
        float animLength = stateInfo.length;
        Debug.Log(animLength);
        yield return new WaitForSeconds(animLength);
        Debug.Log("done");
        // 4. Load target scene after fade
        Loader.Load(scene);
    }
}
