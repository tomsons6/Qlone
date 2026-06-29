using UnityEngine;

/// <summary>
/// Drives the character's Idle / Walking animation from how fast the body is
/// actually moving through the world.
///
/// Because it measures real movement (not key input) it works for both bodies
/// in the clone setup: whichever body currently has control moves and plays
/// Walking, while the inactive body stands still and stays in Idle. No coupling
/// to <see cref="Cloning"/> is required.
///
/// Place this on the character root (the object that has <see cref="FPS_Controller"/>);
/// it finds the Animator on the model child automatically. The referenced
/// AnimatorController needs a float parameter (default "Speed") that the
/// Idle &lt;-&gt; Walking transitions test against.
/// </summary>
public class CharacterAnimator : MonoBehaviour
{
    [Tooltip("Float parameter on the Animator Controller that the Idle <-> Walking transitions read.")]
    [SerializeField] string speedParameter = "Speed";

    [Tooltip("Smoothing time (seconds) applied to the Speed parameter so starts/stops aren't instant.")]
    [SerializeField] float speedSmoothTime = 0.1f;

    Animator animator;
    Vector3 lastPosition;
    int speedHash;

    void Awake()
    {
        animator = ResolveBodyAnimator();
        speedHash = Animator.StringToHash(speedParameter);
        lastPosition = transform.position;
    }

    // The first-person Arms model carries its own (controller-less) Animator and
    // lives under the Camera, so a plain GetComponentInChildren<Animator>() would
    // grab it instead of the body model. Pick the model that actually drives
    // locomotion: the one with an AnimatorController assigned.
    Animator ResolveBodyAnimator()
    {
        Animator fallback = null;
        foreach (Animator candidate in GetComponentsInChildren<Animator>(true))
        {
            if (candidate.runtimeAnimatorController != null)
            {
                return candidate;
            }
            if (fallback == null)
            {
                fallback = candidate;
            }
        }
        return fallback;
    }

    void Update()
    {
        if (animator == null || Time.deltaTime <= 0f)
            return;

        // Horizontal speed (units/second) from real world movement this frame.
        Vector3 delta = transform.position - lastPosition;
        lastPosition = transform.position;
        delta.y = 0f;
        float speed = delta.magnitude / Time.deltaTime;

        // Damped so the locomotion eases in/out instead of snapping.
        animator.SetFloat(speedHash, speed, speedSmoothTime, Time.deltaTime);
    }
}
