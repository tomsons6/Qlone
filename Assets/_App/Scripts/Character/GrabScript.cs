using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Physics-based grab with one hold slot PER BODY. There is a single grabber
/// (the player root, tag "Main") that acts on behalf of whichever body is active,
/// raycasting from the active camera (Camera.main). Each grab is tracked as its own
/// <see cref="Hold"/>, keyed by the camera that made it, so the player and the clone
/// can each carry a separate object at the same time (the two-keycard puzzle).
///
/// A hold is bound to the camera that grabbed it, not to Camera.main, so a held item
/// stays with the body that grabbed it across control switches instead of jumping to
/// whoever is now in control. If that camera is destroyed (the holder was dismissed),
/// the hold auto-drops next physics step.
///
/// The hold anchor is a child Transform named <see cref="HoldPointName"/> under each
/// camera (positioned at that body's FPS arm hands). Move/rotate it to set where and
/// how things are held. If it's missing, the grab falls back to a point
/// <see cref="holdDistance"/> straight in front of the camera.
///
/// Works for any grabbable Rigidbody:
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

    /// <summary>
    /// One active grab. Everything about a held object -- what it is, the state we
    /// overrode and must restore, and which body/camera owns it -- lives here so two
    /// bodies can each hold something independently.
    /// </summary>
    class Hold
    {
        public Rigidbody Body;
        public Collider Collider;   // the collider the grab ray hit (contact shape for GrabIK)
        public int Layer;
        public bool UsedGravity;
        public bool WasKinematic;
        public bool IsRagdoll;
        public Vector3 GrabLocalPoint;   // grab point in the body's local space
        public Quaternion GrabRotOffset; // held ragdoll bone's rotation relative to the hold anchor at grab time
        public Transform HoldCam;        // camera/body that owns this hold; the item stays with it across control switches
        public Collider Carrier;         // this body's CharacterController collider, for pairwise IgnoreCollision
    }

    // At most one hold per body (player + clone).
    readonly List<Hold> m_Holds = new List<Hold>(2);

    /// <summary>True while the body that owns <paramref name="bodyCam"/> is holding something.
    /// Read by <see cref="GrabIK"/>/<see cref="BodyGrabIK"/> to know when to reach that body's
    /// hand out and grip. Each body is tested independently, so both can grip at once.</summary>
    public bool IsHolding(Transform bodyCam) => FindByCam(bodyCam) != null;

    /// <summary>World position of the spot on the body held by <paramref name="bodyCam"/> where
    /// it was grabbed, so <see cref="GrabIK"/> can reach the hand to the object itself (and
    /// follow it as it is pulled in) rather than to a fixed anchor. Falls back to this grabber's
    /// position when that body holds nothing.</summary>
    public Vector3 GetGrabWorldPosition(Transform bodyCam)
    {
        Hold h = FindByCam(bodyCam);
        return h != null && h.Body != null
            ? h.Body.transform.TransformPoint(h.GrabLocalPoint)
            : transform.position;
    }

    /// <summary>The collider that the grab ray hit for the body held by <paramref name="bodyCam"/>,
    /// so <see cref="GrabIK"/> can curl each finger until it touches this surface and stop. Null
    /// when that body holds nothing.</summary>
    public Collider GetHeldCollider(Transform bodyCam) => FindByCam(bodyCam)?.Collider;

    /// <summary>True if <paramref name="rb"/> is currently held by either body. Used by the
    /// keycard scanner to test whether a card inside it is actually being held.</summary>
    public bool IsHeldRigidbody(Rigidbody rb)
    {
        for (int i = 0; i < m_Holds.Count; i++)
        {
            if (m_Holds[i].Body == rb)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>True if <paramref name="c"/> is the collider currently held by either body.</summary>
    public bool IsHeld(Collider c)
    {
        for (int i = 0; i < m_Holds.Count; i++)
        {
            if (m_Holds[i].Collider == c)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Deterministically release whatever the body that owns <paramref name="bodyCam"/>
    /// is holding. Called by <see cref="Cloning"/> when a clone is dismissed so its card drops
    /// immediately, rather than waiting for the destroyed-camera safety net in
    /// <see cref="FixedUpdate"/>.</summary>
    public void ReleaseForCamera(Transform bodyCam)
    {
        Hold h = FindByCam(bodyCam);
        if (h != null)
        {
            Release(h);
        }
    }

    /// <summary>
    /// Single-key grab action, evaluated from the ACTIVE body's point of view (Camera.main):
    /// <list type="bullet">
    /// <item>If the active body is already holding, drop that item.</item>
    /// <item>Otherwise grab whatever the active body is aiming at -- transferring it out of the
    /// OTHER body's hand first if that body was holding it. Aiming at nothing does nothing.</item>
    /// </list>
    /// A holder press returns immediately, so aiming at your own held item can never re-grab it
    /// (the original drop-then-regrab bug); a non-holder only acts when aiming at a grabbable, so
    /// looking away never drops the other body's item -- but looking straight at it takes it.
    /// </summary>
    public void ToggleGrab()
    {
        Camera cam = Camera.main;
        if (cam == null)
        {
            return;
        }

        // The active body already holds an item -> this press just drops it.
        Hold mine = FindByCam(cam.transform);
        if (mine != null)
        {
            Release(mine);
            return;
        }

        // The active body is not a holder. Only act when it is actually aiming at a grabbable;
        // PickUp transfers the item out of the other body's hand if that body was holding it.
        if (!TryFindGrabTarget(cam, out RaycastHit hit, out Rigidbody body, out bool isRagdoll))
        {
            return;
        }
        PickUp(cam, hit, body, isRagdoll);
    }

    /// <summary>Grab for the active body (Camera.main's slot). Kept param-less so the existing
    /// Inspector input wiring (E) still works.</summary>
    public void PickUpObject()
    {
        Camera cam = Camera.main;
        if (cam == null || FindByCam(cam.transform) != null)
        {
            return;
        }
        if (!TryFindGrabTarget(cam, out RaycastHit hit, out Rigidbody body, out bool isRagdoll))
        {
            return;
        }
        PickUp(cam, hit, body, isRagdoll);
    }

    /// <summary>Release the active body's held item (Camera.main's slot). Kept param-less so the
    /// existing Inspector input wiring (E, R) still works.</summary>
    public void ReleaseObject()
    {
        Camera cam = Camera.main;
        if (cam != null)
        {
            ReleaseForCamera(cam.transform);
        }
    }

    // Raycast from the given camera and report the grabbable it hit, if any. A hit qualifies
    // when it has a non-kinematic Rigidbody and is either on the Pickable layer or part of a
    // Ragdoll corpse. Shared by PickUpObject and ToggleGrab so the "is there something to
    // grab?" test and the actual grab always agree.
    bool TryFindGrabTarget(Camera cam, out RaycastHit hit, out Rigidbody body, out bool isRagdoll)
    {
        hit = default;
        body = null;
        isRagdoll = false;
        if (cam == null)
        {
            return false;
        }

        Ray ray = new Ray(cam.transform.position, cam.transform.forward);
        if (!Physics.Raycast(ray, out hit, m_pickUpDistance))
        {
            return false;
        }

        body = hit.collider.attachedRigidbody;
        if (body == null || body.isKinematic)
        {
            return false;
        }

        isRagdoll = hit.collider.GetComponentInParent<Ragdoll>() != null;
        return hit.collider.gameObject.layer == PickableLayer || isRagdoll;
    }

    // Start a new hold on 'body' for the body that owns 'cam'. At most one hold per body: if the
    // same body already holds something, do nothing. If the OTHER body holds this same object,
    // release that hold first so the item transfers into this body's hand.
    void PickUp(Camera cam, RaycastHit hit, Rigidbody body, bool isRagdoll)
    {
        if (FindByCam(cam.transform) != null)
        {
            return;
        }

        Hold existing = FindByBody(body);
        if (existing != null)
        {
            Release(existing);
        }

        Hold h = new Hold
        {
            Body = body,
            Collider = hit.collider,
            Layer = body.gameObject.layer,
            UsedGravity = body.useGravity,
            WasKinematic = body.isKinematic,
            IsRagdoll = isRagdoll,
            // Remember where on the body we grabbed it (in the body's local space) so the
            // hand can be driven to that exact spot as the body moves.
            GrabLocalPoint = body.transform.InverseTransformPoint(hit.point),
            // Bind the hold to the body that grabbed it. We keep dragging to this camera's
            // HoldPoint even after control switches, so the item stays with its grabber.
            HoldCam = cam.transform,
            // This body's own collider, so we can suppress card-vs-carrier collisions pairwise
            // (refcounted per hold) instead of globally by layer.
            Carrier = cam.GetComponentInParent<CharacterController>()
        };

        if (isRagdoll)
        {
            // Pin the grabbed bone to the hand; the rest of the body dangles from it through
            // the ragdoll joints instead of being yanked around. Capture the bone's rotation
            // relative to the hold anchor so it stays fixed in the hand and turns with the
            // player, rather than keeping a fixed world orientation (which reads as orbiting a
            // stationary corpse when you turn).
            body.isKinematic = true;
            Transform grabAnchor = cam.transform.Find(HoldPointName);
            Quaternion anchorRot = grabAnchor != null ? grabAnchor.rotation : cam.transform.rotation;
            h.GrabRotOffset = Quaternion.Inverse(anchorRot) * body.rotation;
        }
        else
        {
            body.useGravity = false;
            body.angularVelocity = Vector3.zero;
        }

        // Stop the held object from colliding with (and shoving) its own carrier. Pairwise so
        // two carried objects each ignore only their own carrier, and normal collisions between
        // the object and everything else (including the other body) are preserved.
        if (h.Carrier != null)
        {
            Physics.IgnoreCollision(h.Collider, h.Carrier, true);
        }

        m_Holds.Add(h);
    }

    // Undo one hold: restore the collision, kinematic and gravity state we overrode, and drop it
    // from the list.
    void Release(Hold h)
    {
        if (h.Carrier != null && h.Collider != null)
        {
            Physics.IgnoreCollision(h.Collider, h.Carrier, false);
        }
        if (h.Body != null)
        {
            h.Body.isKinematic = h.WasKinematic;
            h.Body.useGravity = h.UsedGravity;
        }
        m_Holds.Remove(h);
    }

    Hold FindByCam(Transform bodyCam)
    {
        if (bodyCam == null)
        {
            return null;
        }
        for (int i = 0; i < m_Holds.Count; i++)
        {
            if (m_Holds[i].HoldCam == bodyCam)
            {
                return m_Holds[i];
            }
        }
        return null;
    }

    Hold FindByBody(Rigidbody body)
    {
        for (int i = 0; i < m_Holds.Count; i++)
        {
            if (m_Holds[i].Body == body)
            {
                return m_Holds[i];
            }
        }
        return null;
    }

    void FixedUpdate()
    {
        // Iterate backwards so a hold removed mid-loop (the object or its holder went away)
        // doesn't shift the indices of holds we haven't visited yet.
        for (int i = m_Holds.Count - 1; i >= 0; i--)
        {
            Hold h = m_Holds[i];

            // The held object went away (e.g. corpse removed) or the holder's camera was
            // destroyed (a dismissed clone) -> tidy up. This is the safety net that drops the
            // clone's card once its camera is torn down.
            if (h.Body == null || h.HoldCam == null)
            {
                Release(h);
                continue;
            }

            // Hold against the camera the object was grabbed with, not the current Camera.main.
            // Switching control changes Camera.main, but a held item must stay with the body
            // that grabbed it instead of jumping to whoever is now in control.
            Transform camT = h.HoldCam;
            Transform anchor = camT.Find(HoldPointName);
            Vector3 targetPos = anchor != null
                ? anchor.position
                : camT.position + camT.forward * holdDistance;

            if (h.IsRagdoll)
            {
                // The grabbed bone is kinematic: pin it to the hand in both position and rotation
                // so the corpse turns with the player (the grabbed side keeps facing you) instead
                // of holding a fixed world orientation. The rest dangles from it via the joints.
                Quaternion anchorRot = anchor != null ? anchor.rotation : camT.rotation;
                h.Body.MovePosition(targetPos);
                h.Body.MoveRotation(anchorRot * h.GrabRotOffset);
                continue;
            }

            Vector3 toTarget = targetPos - h.Body.worldCenterOfMass;
            h.Body.linearVelocity = Vector3.ClampMagnitude(toTarget * followSpeed, maxHoldSpeed);

            // Rigid pickups turn to the grip orientation.
            if (matchRotation)
            {
                Quaternion targetRot = anchor != null ? anchor.rotation : camT.rotation;
                Quaternion delta = targetRot * Quaternion.Inverse(h.Body.rotation);
                delta.ToAngleAxis(out float angle, out Vector3 axis);
                if (angle > 180f)
                {
                    angle -= 360f;
                }
                if (Mathf.Abs(angle) > 0.01f && !float.IsInfinity(axis.x))
                {
                    h.Body.angularVelocity = axis.normalized * (angle * Mathf.Deg2Rad * rotationSpeed);
                }
                else
                {
                    h.Body.angularVelocity = Vector3.zero;
                }
            }
        }
    }
}
