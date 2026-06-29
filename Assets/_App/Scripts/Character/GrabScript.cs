using UnityEngine;

/// <summary>
/// Physics-based grab. Raycasts from the active camera (Camera.main, which
/// follows whichever body is in control) and, if it hits a grabbable Rigidbody,
/// drags that body toward a hold anchor at the hands each physics step.
///
/// The hold anchor is a child Transform named <see cref="HoldPointName"/> under
/// the active camera (positioned at the FPS arm's hands). Move/rotate it to set
/// where and how things are held. If it's missing, the grab falls back to a
/// point <see cref="holdDistance"/> straight in front of the camera.
///
/// Works for any Rigidbody:
/// - puzzle pickups (layer <see cref="PickableLayer"/>) move to the hand and,
///   if <see cref="matchRotation"/> is on, rotate to the anchor so they sit gripped;
/// - a ragdoll corpse (tagged with a <see cref="Ragdoll"/> marker) is grabbed by
///   whichever bone you aimed at and left free to dangle from the hand.
/// </summary>
public class GrabScript : MonoBehaviour
{
    const int PickableLayer = 9;
    const string HoldPointName = "HoldPoint";

    [Tooltip("Max distance the grab ray reaches.")]
    [SerializeField]
    float m_pickUpDistance = 3f;
    [Tooltip("Fallback hold distance in front of the camera if no HoldPoint anchor exists.")]
    [SerializeField]
    float holdDistance = 0.5f;
    [Tooltip("How strongly the held body is pulled toward the hold anchor.")]
    [SerializeField]
    float followSpeed = 12f;
    [Tooltip("Safety clamp on how fast the held body can be moved.")]
    [SerializeField]
    float maxHoldSpeed = 25f;
    [Tooltip("Align rigid pickups to the hold anchor's rotation so they sit gripped. Ragdolls always dangle.")]
    [SerializeField]
    bool matchRotation = true;
    [Tooltip("How quickly a held object turns to the anchor's rotation.")]
    [SerializeField]
    float rotationSpeed = 12f;
    [Tooltip("Layer of the player/clone bodies, so a held object doesn't shove the carrier.")]
    [SerializeField]
    int playerLayer = 10;

    Rigidbody m_HeldBody;
    int m_HeldLayer;
    bool m_HeldUsedGravity;
    bool m_HeldWasKinematic;
    bool m_HeldIsRagdoll;
    bool m_HandOccupied;
    Vector3 m_GrabLocalPoint;

    /// <summary>True while an object/ragdoll is being held. Read by <see cref="GrabIK"/>
    /// to know when to reach the hand out and curl the fingers into a grip.</summary>
    public bool HandOccupied => m_HandOccupied;

    /// <summary>World position of the spot on the held body where it was grabbed, so
    /// <see cref="GrabIK"/> can reach the hand to the object itself (and follow it as it
    /// is pulled in) rather than to a fixed anchor. Only meaningful while held.</summary>
    public Vector3 GrabWorldPosition =>
        m_HeldBody != null ? m_HeldBody.transform.TransformPoint(m_GrabLocalPoint) : transform.position;

    public void PickUpObject()
    {
        if (m_HandOccupied)
        {
            return;
        }

        Camera cam = Camera.main;
        if (cam == null)
        {
            return;
        }

        Ray ray = new Ray(cam.transform.position, cam.transform.forward);
        if (!Physics.Raycast(ray, out RaycastHit hit, m_pickUpDistance))
        {
            return;
        }

        Rigidbody body = hit.collider.attachedRigidbody;
        if (body == null || body.isKinematic)
        {
            return;
        }

        bool isRagdoll = hit.collider.GetComponentInParent<Ragdoll>() != null;
        if (hit.collider.gameObject.layer != PickableLayer && !isRagdoll)
        {
            return;
        }

        m_HeldBody = body;
        m_HeldLayer = body.gameObject.layer;
        m_HeldUsedGravity = body.useGravity;
        m_HeldWasKinematic = body.isKinematic;
        m_HeldIsRagdoll = isRagdoll;
        // Remember where on the body we grabbed it (in the body's local space) so the
        // hand can be driven to that exact spot as the body moves.
        m_GrabLocalPoint = body.transform.InverseTransformPoint(hit.point);

        if (isRagdoll)
        {
            // Pin the grabbed bone to the hand; the rest of the body dangles from
            // it through the ragdoll joints instead of being yanked around.
            body.isKinematic = true;
        }
        else
        {
            body.useGravity = false;
            body.angularVelocity = Vector3.zero;
        }

        // Stop the held object from colliding with (and shoving) the carrier.
        Physics.IgnoreLayerCollision(m_HeldLayer, playerLayer, true);

        m_HandOccupied = true;
    }

    public void ReleaseObject()
    {
        if (!m_HandOccupied)
        {
            return;
        }

        Physics.IgnoreLayerCollision(m_HeldLayer, playerLayer, false);
        if (m_HeldBody != null)
        {
            m_HeldBody.isKinematic = m_HeldWasKinematic;
            m_HeldBody.useGravity = m_HeldUsedGravity;
        }
        m_HeldBody = null;
        m_HandOccupied = false;
    }

    void FixedUpdate()
    {
        if (m_HeldBody == null)
        {
            // The held object went away (e.g. corpse removed) - tidy up.
            if (m_HandOccupied)
            {
                ReleaseObject();
            }
            return;
        }

        Camera cam = Camera.main;
        if (cam == null)
        {
            return;
        }

        // Hold at the hand anchor when present; otherwise just in front of the camera.
        Transform anchor = cam.transform.Find(HoldPointName);
        Vector3 targetPos = anchor != null
            ? anchor.position
            : cam.transform.position + cam.transform.forward * holdDistance;

        if (m_HeldIsRagdoll)
        {
            // The grabbed bone is kinematic: move it straight to the hand and let
            // the rest of the body hang from it. No spin, just dangle.
            m_HeldBody.MovePosition(targetPos);
            return;
        }

        Vector3 toTarget = targetPos - m_HeldBody.worldCenterOfMass;
        m_HeldBody.linearVelocity = Vector3.ClampMagnitude(toTarget * followSpeed, maxHoldSpeed);

        // Rigid pickups turn to the grip orientation.
        if (matchRotation)
        {
            Quaternion targetRot = anchor != null ? anchor.rotation : cam.transform.rotation;
            Quaternion delta = targetRot * Quaternion.Inverse(m_HeldBody.rotation);
            delta.ToAngleAxis(out float angle, out Vector3 axis);
            if (angle > 180f)
            {
                angle -= 360f;
            }
            if (Mathf.Abs(angle) > 0.01f && !float.IsInfinity(axis.x))
            {
                m_HeldBody.angularVelocity = axis.normalized * (angle * Mathf.Deg2Rad * rotationSpeed);
            }
            else
            {
                m_HeldBody.angularVelocity = Vector3.zero;
            }
        }
    }
}
