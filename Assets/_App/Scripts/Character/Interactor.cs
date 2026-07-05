using UnityEngine;

/// <summary>
/// Player-owned interact actor. Bound to the interact key (default G) through the player's
/// <see cref="InputManager"/>, it raycasts from the active camera (Camera.main, which follows
/// whichever body is in control) and, if it hits an <see cref="InteractButton"/>, presses it.
///
/// Like <see cref="GrabScript"/>, there is a single actor on the player root (tag "Main") that
/// acts on behalf of the active body. The clone inherits this component, but its InputManager
/// is disabled on spawn, so only the player's input reaches here -- no double-fire, and no
/// extra disabling is needed.
/// </summary>
public class Interactor : MonoBehaviour
{
    [Tooltip("Max distance the interact ray reaches.")]
    [SerializeField]
    float m_Distance = 3f;

    /// <summary>Raycast from the active camera and press the first <see cref="InteractButton"/>
    /// found on the hit collider or its parents. Wire this to the interact key's OnInteractDown
    /// event on the player's <see cref="InputManager"/>.</summary>
    public void Interact()
    {
        Camera cam = Camera.main;
        if (cam == null)
        {
            return;
        }

        // Ignore triggers so the ray isn't blocked by scanner/pressure-plate volumes in front of
        // a button -- interact buttons use a solid collider you look straight at.
        Ray ray = new Ray(cam.transform.position, cam.transform.forward);
        if (!Physics.Raycast(ray, out RaycastHit hit, m_Distance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        {
            return;
        }

        InteractButton button = hit.collider.GetComponentInParent<InteractButton>();
        if (button != null)
        {
            button.Interact();
        }
    }
}
