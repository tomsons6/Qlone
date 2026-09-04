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
/// the hold auto-drops on the next frame.
///
/// The hold anchor is a child Transform named <see cref="HoldPointName"/> under each
/// camera (positioned at that body's FPS arm hands). Move/rotate it to set where and
/// how things are held. If it's missing, the grab falls back to a point
/// <see cref="holdDistance"/> straight in front of the camera.
///
/// Two different carry mechanisms, because the two cases want opposite things:
/// - <b>puzzle pickups</b> (layer <see cref="PickableLayer"/>) are <b>hard-attached</b>: the
///   body goes kinematic and we write its transform straight onto the hold anchor every
///   frame in <see cref="LateUpdate"/>. Physics never gets a say, so there is no lag to
///   float on and no physics-rate stepping to flicker. Asking PhysX to chase the hand
///   instead -- at any gain, with or without velocity feed-forward -- always leaves the
///   object trailing a fast turn, because a controller that has to close a position error
///   cannot be both tight and gentle;
/// - a <b>ragdoll corpse</b> (tagged with a <see cref="Ragdoll"/> marker) is grabbed by
///   whichever bone you aimed at and stays in the simulation, pinned by MovePosition in
///   <see cref="FixedUpdate"/>, so the rest of the body still dangles from it on its joints.
///
/// Runs at execution order -5: after <c>MovingPlatform</c> (-10) has carried the body, and
/// before <c>GrabIK</c> (0) reads the held object's transform to aim the hand at it. Without
/// that ordering the arm poses to where the object was last frame and judders.
///
/// Because a hard-attached pickup is kinematic it gets no collision response of its own, so
/// <see cref="ResolveCarryAnchor"/> sweeps a sphere from the camera to the hold anchor and
/// shortens the reach when level geometry is in the way -- the grip stays rigid, the object
/// just draws in against your chest rather than sinking into the wall.
/// </summary>
[DefaultExecutionOrder(-5)]
public class GrabScript : MonoBehaviour
{
    const int PickableLayer = 9;
    const string HoldPointName = "HoldPoint";
    // Layers the wall sweep must ignore. The carried object itself is on Pickable, and none of
    // the character layers (carrier, other body, corpses) are walls -- sweeping against any of
    // them would have the object shoving itself back out of its own hand.
    const int NonWallLayers = (1 << PickableLayer) | (1 << 10) | (1 << 11) | (1 << 12) | (1 << 13) | (1 << 14);

    [Tooltip("Max distance the grab ray reaches.")]
    [SerializeField]
    float m_pickUpDistance = 3f;
    [Tooltip("Fallback hold distance in front of the camera if no HoldPoint anchor exists.")]
    [SerializeField]
    float holdDistance = 0.5f;
    [Tooltip("Snap rigid pickups to the hold anchor's rotation so they sit in a fixed grip. Off keeps the orientation they had when grabbed. Ragdolls always dangle.")]
    [SerializeField]
    bool matchRotation = true;
    [Tooltip("Smoothing (seconds) on the measured hand velocity. A hard-attached object has no velocity of its own, so this is what it inherits when you let go -- it is why dropping something while running throws it forward instead of straight down. Lower = snappier throws, higher = steadier.")]
    [SerializeField]
    float releaseVelocitySmoothing = 0.05f;

    [Header("Wall avoidance")]
    [Tooltip("Pull a carried pickup in toward the camera when level geometry would swallow it. A hard-attached object is kinematic and gets no collision response of its own, so this is what keeps it out of walls.")]
    [SerializeField]
    bool avoidWalls = true;
    [Tooltip("What counts as blocking geometry. Excludes the pickable and character layers by default -- the object must not sweep against itself, its carrier, the other body or a corpse.")]
    [SerializeField]
    LayerMask wallLayers = ~NonWallLayers;
    [Tooltip("Closest the object may be pulled in, as a fraction of its normal hold distance. Keeps it off the camera's near plane when you press straight into a wall. A fraction rather than metres so it follows the character's scale.")]
    [SerializeField, Range(0.05f, 1f)]
    float minHoldFraction = 0.25f;

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
        public RigidbodyInterpolation WasInterpolation;
        public bool IsRagdoll;
        public Vector3 GrabLocalPoint;   // grab point in the body's local space
        public Quaternion GrabRotOffset; // held body's rotation relative to the hold anchor at grab time
        public Vector3 ComLocal;         // centre of mass in the body's local space, so the hard attach parks it where the old velocity drive did
        public float CastRadius;         // bounding-sphere radius of the grabbed object, for the wall sweep
        public Transform HoldCam;        // camera/body that owns this hold; the item stays with it across control switches
        public Collider Carrier;         // this body's CharacterController collider, for pairwise IgnoreCollision

        // Hold anchor motion, measured per frame purely so the object can inherit the
        // hand's velocity on release (a hard-attached body carries none of its own).
        public Vector3 LastAnchorPos;
        public Quaternion LastAnchorRot;
        public Vector3 AnchorVel;
        public Vector3 AnchorAngVel;
        public bool HasAnchorSample;
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
        // Kinematic means "not ours to move" -- EXCEPT when it is kinematic precisely because
        // we made it so to carry it. Without that exemption the hard attach would silently kill
        // the hand-to-hand transfer in ToggleGrab: the other body's held item is kinematic, so
        // aiming at it would no longer register as a grabbable at all.
        if (body == null || (body.isKinematic && !IsHeldRigidbody(body)))
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
            WasInterpolation = body.interpolation,
            IsRagdoll = isRagdoll,
            // Remember where on the body we grabbed it (in the body's local space) so the
            // hand can be driven to that exact spot as the body moves.
            GrabLocalPoint = body.transform.InverseTransformPoint(hit.point),
            ComLocal = body.centerOfMass,
            // Bounding-sphere radius from the world AABB. A sphere is rotation invariant, so
            // capturing it once at grab time stays correct however the object is turned.
            CastRadius = hit.collider.bounds.extents.magnitude,
            // Bind the hold to the body that grabbed it. We keep driving to this camera's
            // HoldPoint even after control switches, so the item stays with its grabber.
            HoldCam = cam.transform,
            // This body's own collider, so we can suppress card-vs-carrier collisions pairwise
            // (refcounted per hold) instead of globally by layer.
            Carrier = cam.GetComponentInParent<CharacterController>()
        };

        // Both carry modes take the body out of PhysX's hands, so neither should be integrating
        // its own motion while held. Zero the velocities before going kinematic -- a kinematic
        // body ignores velocity writes.
        body.linearVelocity = Vector3.zero;
        body.angularVelocity = Vector3.zero;
        body.isKinematic = true;
        body.useGravity = false;

        // Rotation the object had relative to the hand when grabbed, so a non-matchRotation
        // carry keeps the orientation you picked it up in and still turns with you.
        GetAnchor(h, out _, out Quaternion grabAnchorRot);
        h.GrabRotOffset = Quaternion.Inverse(grabAnchorRot) * body.rotation;

        // Hard attach: we write this transform ourselves every frame, so interpolation has to
        // be OFF -- it would blend the visual pose toward the *physics* pose, which is now a
        // frame stale, and reintroduce exactly the flicker we are removing. A corpse keeps
        // whatever HumanoidRagdoll gave it, because its grabbed bone genuinely is driven at
        // the physics rate (MovePosition, in FixedUpdate).
        if (!isRagdoll)
        {
            body.interpolation = RigidbodyInterpolation.None;
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
            h.Body.interpolation = h.WasInterpolation;

            // Hand the object the velocity of the hand that was carrying it. A hard-attached
            // body accumulates none of its own, so without this it would drop dead-still out
            // of a sprint. Must come after isKinematic goes false -- a kinematic body ignores
            // velocity writes.
            if (!h.IsRagdoll && !h.Body.isKinematic && h.HasAnchorSample)
            {
                h.Body.linearVelocity = h.AnchorVel;
                h.Body.angularVelocity = h.AnchorAngVel;
            }
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

    // Rigid pickups are hard-attached here rather than in FixedUpdate, because "attached" has
    // to mean attached in the frame you SEE, not in the last physics step. The camera is moved
    // in Update (FPS_Controller) and the platform under the body in LateUpdate at order -10, so
    // by the time this runs at -5 the hand is in its final position for the frame: writing the
    // object onto it here is exact, every frame, with nothing left over to float or flicker.
    void LateUpdate()
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

            GetAnchor(h, out Vector3 anchorPos, out Quaternion anchorRot);

            // A corpse is left alone: it is still simulated and collides on its own, and its
            // grabbed bone is pinned in FixedUpdate so the joints can drag the rest of the body
            // along. Shortening its reach here would fight those joints.
            if (!h.IsRagdoll)
            {
                anchorPos = ResolveCarryAnchor(h, anchorPos);
            }

            // Measure the anchor the object actually follows, not the one the hand asked for,
            // so letting go while pinned against a wall throws the object from where it really
            // is instead of firing it into the geometry.
            MeasureAnchorMotion(h, anchorPos, anchorRot, Time.deltaTime);

            // Snapping a corpse here would teleport a joint anchor every frame and tear the
            // ragdoll apart, so the pin stays in FixedUpdate.
            if (h.IsRagdoll)
            {
                continue;
            }

            Transform t = h.Body.transform;
            t.rotation = matchRotation ? anchorRot : anchorRot * h.GrabRotOffset;
            // Park the centre of mass on the anchor, which is where the old velocity drive put
            // it, so the hand/palm offsets tuned against that behaviour still line up.
            t.position = anchorPos - t.TransformVector(h.ComLocal);
        }
    }

    void FixedUpdate()
    {
        for (int i = m_Holds.Count - 1; i >= 0; i--)
        {
            Hold h = m_Holds[i];

            if (h.Body == null || h.HoldCam == null)
            {
                Release(h);
                continue;
            }

            // Only corpses are driven through physics. Rigid pickups are placed directly in
            // LateUpdate and must not be touched here, or the two would fight.
            if (!h.IsRagdoll)
            {
                continue;
            }

            // The grabbed bone is kinematic: pin it to the hand in both position and rotation
            // so the corpse turns with the player (the grabbed side keeps facing you) instead
            // of holding a fixed world orientation. The rest dangles from it via the joints.
            GetAnchor(h, out Vector3 anchorPos, out Quaternion anchorRot);
            h.Body.MovePosition(anchorPos);
            h.Body.MoveRotation(anchorRot * h.GrabRotOffset);
        }
    }

    // Keep a hard-attached pickup out of level geometry. It is kinematic, so PhysX will never
    // stop it at a wall; instead we sweep a sphere the size of the object from the camera out
    // to the hold anchor, and if anything blocks the way we hold the object at the blocking
    // point instead. The attachment stays perfectly rigid -- only the reach shortens, which
    // reads as pulling the item in against your chest as you close on a wall.
    //
    // The handover is continuous, so nothing pops: a sweep first reports a hit exactly when the
    // wall comes within one radius of the object's centre, and at that moment the reported
    // distance still equals the full reach. From there it shortens smoothly.
    //
    // Triggers are ignored deliberately -- a keycard has to be able to sit inside a
    // KeyCardScanner's volume, which is the whole point of that puzzle.
    Vector3 ResolveCarryAnchor(Hold h, Vector3 anchorPos)
    {
        if (!avoidWalls)
        {
            return anchorPos;
        }

        Vector3 origin = h.HoldCam.position;
        Vector3 toAnchor = anchorPos - origin;
        float nominal = toAnchor.magnitude;
        if (nominal < 1e-4f)
        {
            return anchorPos;
        }

        Vector3 dir = toAnchor / nominal;
        float reach = nominal;

        if (Physics.SphereCast(origin, Mathf.Max(h.CastRadius, 1e-3f), dir, out RaycastHit blocked,
                nominal, wallLayers, QueryTriggerInteraction.Ignore))
        {
            // A sweep that begins already overlapping reports distance 0, which would drag the
            // object onto the camera's near plane; the fraction clamp is what prevents that.
            reach = Mathf.Max(blocked.distance, nominal * minHoldFraction);
        }

        return origin + dir * reach;
    }

    // Where this hold should sit: the HoldPoint under the camera that made the grab, NOT the
    // current Camera.main. Switching control changes Camera.main, but a held item must stay
    // with the body that grabbed it instead of jumping to whoever is now in control.
    void GetAnchor(Hold h, out Vector3 anchorPos, out Quaternion anchorRot)
    {
        Transform camT = h.HoldCam;
        Transform anchor = camT.Find(HoldPointName);
        anchorPos = anchor != null ? anchor.position : camT.position + camT.forward * holdDistance;
        anchorRot = anchor != null ? anchor.rotation : camT.rotation;
    }

    // Track how fast the hold anchor is moving and spinning, so a released object can inherit
    // the hand's motion. A hard-attached body is placed by transform writes and therefore
    // accumulates no velocity of its own, so this is the only record of it.
    //
    // The per-frame estimate is noisy (it divides a small displacement by a small, variable
    // dt), and an unfiltered value would make throw strength jump around between frames --
    // hence the one-pole filter. It no longer feeds the carry itself, so its lag costs nothing.
    void MeasureAnchorMotion(Hold h, Vector3 anchorPos, Quaternion anchorRot, float dt)
    {
        if (h.HasAnchorSample && dt > 1e-6f)
        {
            // Frame-rate independent one-pole coefficient; smoothing 0 gives the raw estimate.
            float k = releaseVelocitySmoothing > 1e-4f
                ? 1f - Mathf.Exp(-dt / releaseVelocitySmoothing)
                : 1f;

            h.AnchorVel = Vector3.Lerp(h.AnchorVel, (anchorPos - h.LastAnchorPos) / dt, k);

            Quaternion step = anchorRot * Quaternion.Inverse(h.LastAnchorRot);
            step.ToAngleAxis(out float stepAngle, out Vector3 stepAxis);
            if (stepAngle > 180f)
            {
                stepAngle -= 360f;
            }
            Vector3 rawAngVel = Mathf.Abs(stepAngle) > 0.0001f && !float.IsInfinity(stepAxis.x)
                ? stepAxis.normalized * (stepAngle * Mathf.Deg2Rad / dt)
                : Vector3.zero;
            h.AnchorAngVel = Vector3.Lerp(h.AnchorAngVel, rawAngVel, k);
        }

        h.LastAnchorPos = anchorPos;
        h.LastAnchorRot = anchorRot;
        h.HasAnchorSample = true;
    }
}
