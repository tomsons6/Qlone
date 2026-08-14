using UnityEngine;

/// <summary>
/// Carries the character bodies standing on this surface when it moves. Neither body rides a
/// mover on its own:
/// <list type="bullet">
/// <item>the body in control has a <c>CharacterController</c>, and Unity's controller does not
/// get pushed by moving colliders -- a rising platform slides straight through the capsule;</item>
/// <item>the idle body has its controller <b>disabled</b> (see <see cref="Cloning"/>'s
/// SwitchClone), so it has no collider at all and physics cannot touch it.</item>
/// </list>
/// Carrying that idle body is the point rather than a nicety: the lift puzzle has one body
/// standing at the lever holding it while the OTHER rides up, so the passenger is by definition
/// not the body in control.
///
/// Actuation belongs to something else -- put a <see cref="Door"/> on the lift and drive it from
/// a <see cref="DoorLock"/> (the same procedural actuator the doors use: it tweens a part by a
/// local offset, so a lift is just a door that travels upward). This component only moves riders,
/// so it works with any actuator.
///
/// It runs in LateUpdate, after the Door's tween coroutine has already moved the platform this
/// frame, so riders are never a frame behind; and at a negative execution order so a rider has
/// finished moving before <see cref="GrabIK"/>/<see cref="ViewmodelArms"/> pose its arms in their
/// own LateUpdate.
/// </summary>
[DefaultExecutionOrder(-10)]
public class MovingPlatform : MonoBehaviour
{
    [Tooltip("The surface bodies stand on. Defaults to this object's own collider. Riders are found from its top face, so it should be the collider that actually holds them up.")]
    [SerializeField]
    Collider m_Surface;
    [Tooltip("How far a body's feet may be from the top face and still count as standing on it, in metres. It has to cover one frame of platform travel, so raise it if a fast lift leaves a rider behind.")]
    [SerializeField]
    float m_StandTolerance = 0.4f;

    // The two bodies, resolved by tag. The clone is created and destroyed at runtime, so the
    // cached reference is re-checked (see Resolve) rather than looked up once.
    CharacterController m_Player;
    CharacterController m_Clone;

    Vector3 m_LastPosition;

    void Awake()
    {
        if (m_Surface == null)
        {
            m_Surface = GetComponent<Collider>();
        }
        if (m_Surface == null)
        {
            Debug.LogWarning($"{name}: MovingPlatform has no collider to use as its surface, so nothing will be carried.", this);
        }
    }

    // Re-seed on enable AND on Start: OnEnable alone would miss a Door that snaps the platform to
    // a start-open pose in its own Awake (which would read as one huge delta), and Start alone
    // would miss a platform that gets disabled and re-enabled later.
    void OnEnable() => m_LastPosition = transform.position;

    void Start() => m_LastPosition = transform.position;

    void LateUpdate()
    {
        Vector3 now = transform.position;
        Vector3 delta = now - m_LastPosition;
        m_LastPosition = now;

        if (m_Surface == null || delta.sqrMagnitude < 1e-10f)
        {
            return;
        }

        Bounds surface = m_Surface.bounds;
        m_Player = Resolve(m_Player, "Main");
        m_Clone = Resolve(m_Clone, "Clone");
        Carry(m_Player, surface, delta);
        Carry(m_Clone, surface, delta);
    }

    // Find a body by tag, re-resolving whenever the cached one no longer carries that tag. That
    // tag re-check is what drops a dismissed clone: Cloning retags the corpse Untagged (so a new
    // clone can spawn), and its ragdoll is driven by physics from then on, not by us.
    static CharacterController Resolve(CharacterController current, string tag)
    {
        if (current != null && current.CompareTag(tag))
        {
            return current;
        }
        GameObject body = GameObject.FindGameObjectWithTag(tag);
        return body != null ? body.GetComponent<CharacterController>() : null;
    }

    void Carry(CharacterController body, Bounds surface, Vector3 delta)
    {
        if (body == null || !IsStandingOn(body, surface))
        {
            return;
        }

        if (body.enabled)
        {
            // The body in control: move the controller so it still collides on the way up, and so
            // its own gravity move next frame starts from the new spot.
            body.Move(delta);
        }
        else
        {
            // The idle body has no active collider to move, so place it directly. Its Rigidbody is
            // kinematic while idle, which makes the transform authoritative.
            body.transform.position += delta;
        }
    }

    // Is this body's capsule standing on the surface's top face? Measuring the feet (rather than
    // overlapping the whole capsule) is what keeps a body on the floor NEXT to a lift from being
    // dragged along with it, and it works for the idle body too, whose disabled controller is
    // invisible to every physics query.
    bool IsStandingOn(CharacterController body, Bounds surface)
    {
        Transform t = body.transform;
        Vector3 scale = t.lossyScale;
        float height = body.height * Mathf.Abs(scale.y);
        float radius = body.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
        Vector3 feet = t.TransformPoint(body.center) - Vector3.up * (height * 0.5f);

        // Below the top face means the body is beside or under the platform; well above it means
        // it is in the air, or standing on something else entirely.
        if (feet.y < surface.max.y - m_StandTolerance || feet.y > surface.max.y + m_StandTolerance)
        {
            return false;
        }

        // And inside the footprint, allowing the capsule to overhang by its own radius.
        return feet.x >= surface.min.x - radius && feet.x <= surface.max.x + radius
            && feet.z >= surface.min.z - radius && feet.z <= surface.max.z + radius;
    }
}
