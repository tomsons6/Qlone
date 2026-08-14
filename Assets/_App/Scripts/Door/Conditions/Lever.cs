using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// A lever a body throws and then keeps its hand on. Interacting swings the handle over (180
/// degrees about its own X axis by default) and satisfies this condition -- but only while the
/// body that pulled it stays at the lever. That "hold to keep" is the whole point: using the
/// lever costs you a body, so the lever and whatever it drives can only be used together by the
/// player and the clone (one holds it, the other rides the lift).
///
/// Put this on the lever's ROOT -- the object whose children are the handle and the base.
/// <see cref="Interactor"/> raycasts with QueryTriggerInteraction.Ignore and resolves the
/// interactable with GetComponentInParent, so a solid collider on either child reaches it.
///
/// Either body can use it. The holder's identity is its CAMERA transform -- the same token
/// <see cref="GrabScript.IsHolding"/> and <see cref="CloneButtonLink"/> key on -- because
/// exactly one camera is tagged MainCamera at a time (see <see cref="Cloning"/>), so
/// <c>Camera.main</c> is whichever body pressed the key. Binding the hold to that body rather
/// than to "whoever is in control" is what lets the clone keep the lever thrown after control
/// switches back to the player, which is the only reason the puzzle is solvable.
///
/// While held, the holder's first-person arm (<see cref="GrabIK"/>) and its visible third-person
/// arm (<see cref="BodyGrabIK"/>) are both re-aimed at the grip point every frame, so the hand
/// tracks the handle through the swing instead of reaching one fixed spot in the air.
/// </summary>
public class Lever : DoorCondition, IInteractable
{
    [Header("Handle")]
    [Tooltip("The part that swings. Auto-found as the child named Lever or Handle if left empty.")]
    [SerializeField]
    Transform m_Handle;
    [Tooltip("Rotation applied on top of the handle's rest pose once pulled, in the handle's own local space. The default throws it 180 degrees about its X axis. Author the lever in its RESTING pose -- Awake captures that as the starting point.")]
    [SerializeField]
    Vector3 m_PulledLocalEuler = new Vector3(180f, 0f, 0f);
    [Tooltip("Seconds the handle takes to swing over, or back.")]
    [SerializeField]
    float m_PullDuration = 0.4f;

    [Header("Grip")]
    [Tooltip("Where the holder's hand sits on the handle. Make it a CHILD of the handle so it swings with it -- that is what makes the arm IK follow the throw. Falls back to the handle renderer's centre, which also moves with the swing.")]
    [SerializeField]
    Transform m_GripPoint;
    [Tooltip("Collider the fingers curl around while gripping. Auto-found on the handle. It must be convex (a convex MeshCollider or a primitive) for the contact solve; anything else simply curls the fingers to their default angle.")]
    [SerializeField]
    Collider m_GripCollider;
    [Tooltip("How far the holder may drift from the grip point before it lets go and the lever springs back. This is ALSO the maximum range a pull can be made from, so the two always agree -- you have to walk up to the lever, and stepping away from it releases.")]
    [SerializeField]
    float m_ReleaseDistance = 1.75f;

    [Header("Behaviour")]
    [Tooltip("Off (the puzzle default): a body has to keep holding the lever, so it stays thrown only while somebody is on it. On: a plain switch that stays where it is thrown, and interacting again pushes it back.")]
    [SerializeField]
    bool m_Latching;

    [Header("Events")]
    [Tooltip("Fired as the handle starts swinging over, for sound/VFX.")]
    [SerializeField]
    UnityEvent m_OnPulled;
    [Tooltip("Fired as the handle starts swinging back.")]
    [SerializeField]
    UnityEvent m_OnReleased;

    Renderer m_HandleRenderer;
    Quaternion m_RestRotation; // captured at Awake, so the throw works from wherever the lever was authored
    Vector3 m_PullAxis;
    float m_PullAngle;

    Transform m_Holder;      // the gripping body's camera, or null when nobody is on the lever
    GrabIK m_ArmIK;          // that body's first-person viewmodel arms
    BodyGrabIK m_BodyIK;     // that body's visible humanoid model
    float m_LatchGripTimer;  // latching only: how much longer the hand rides the swing before letting go
    bool m_Pulled;           // the pose the handle is heading to
    float m_Blend;           // 0 = resting, 1 = fully pulled

    /// <summary>Whether the handle is thrown (or on its way there). Matches
    /// <see cref="DoorCondition.IsMet"/>; exposed for UI/VFX that shouldn't depend on the lock.</summary>
    public bool IsPulled => m_Pulled;

    /// <summary>World point a hand grips the handle at. It moves as the handle swings, which is
    /// what the arm IK follows.</summary>
    public Vector3 GripAnchor
    {
        get
        {
            if (m_GripPoint != null)
            {
                return m_GripPoint.position;
            }
            // The handle's own transform sits at the hinge, so it barely moves as the lever
            // swings -- the renderer's world bounds centre actually travels with the handle.
            if (m_HandleRenderer != null)
            {
                return m_HandleRenderer.bounds.center;
            }
            return m_Handle != null ? m_Handle.position : transform.position;
        }
    }

    void Awake()
    {
        if (m_Handle == null)
        {
            foreach (Transform child in transform)
            {
                if (child.name == "Lever" || child.name == "Handle")
                {
                    m_Handle = child;
                    break;
                }
            }
        }

        if (m_Handle == null)
        {
            // Still usable as a condition, but nothing would visibly move -- say so rather than
            // leaving a lever that reports met while sitting perfectly still.
            Debug.LogWarning($"{name}: Lever has no Handle assigned and no child named 'Lever'/'Handle', so nothing will swing.", this);
        }
        else
        {
            m_RestRotation = m_Handle.localRotation;
            m_HandleRenderer = m_Handle.GetComponent<Renderer>();
            if (m_GripCollider == null)
            {
                m_GripCollider = m_Handle.GetComponent<Collider>();
            }
        }

        // Split the authored euler into an axis and an angle once, and drive the handle with
        // AngleAxis instead of slerping to the pulled pose: at exactly 180 degrees the shortest
        // arc between two rotations has no defined axis, so a Slerp is free to swing the handle
        // sideways instead of over its hinge. This keeps it on the authored axis at any angle.
        Quaternion pulled = Quaternion.Euler(m_PulledLocalEuler);
        pulled.ToAngleAxis(out m_PullAngle, out m_PullAxis);
        if (float.IsNaN(m_PullAngle) || m_PullAxis.sqrMagnitude < 1e-6f)
        {
            m_PullAxis = Vector3.right;
            m_PullAngle = 0f;
        }
    }

    void OnDisable()
    {
        // Never leave a body's arm stuck out at a lever that is no longer running.
        LetGo();
    }

    /// <summary>Called by <see cref="Interactor"/> when a body looks at the lever and presses the
    /// interact key. Pulls it, or -- if this same body is already on it -- lets go.</summary>
    public void Interact()
    {
        Camera cam = Camera.main;
        if (cam == null)
        {
            return;
        }
        Transform bodyCam = cam.transform;

        // The body already on the lever lets go. This is how you release without walking away,
        // and (for a latching lever) how you push the handle back.
        if (m_Holder == bodyCam)
        {
            if (m_Latching)
            {
                Grip(bodyCam);
                SetPulled(!m_Pulled);
                m_LatchGripTimer = Mathf.Max(m_PullDuration, 0.01f);
            }
            else
            {
                LetGo();
            }
            return;
        }

        // Somebody else is holding it; it stays theirs until they let go or walk off.
        if (m_Holder != null)
        {
            return;
        }

        // You have to be standing at the lever. Reusing the walk-away range here means a pull can
        // never be made from a spot that would release it again on the very next frame.
        if (Vector3.Distance(bodyCam.position, GripAnchor) > m_ReleaseDistance)
        {
            return;
        }

        Grip(bodyCam);
        if (m_Latching)
        {
            // A plain switch: it stays where it is thrown, so the hand only rides the swing.
            SetPulled(!m_Pulled);
            m_LatchGripTimer = Mathf.Max(m_PullDuration, 0.01f);
        }
        else
        {
            SetPulled(true);
        }
    }

    void Update()
    {
        if (m_Holder == null)
        {
            // A dismissed clone is destroyed along with its camera, so a lever it was holding can
            // lose its holder without anyone letting go (the same safety net GrabScript keeps for
            // a hold whose HoldCam vanished). Outside latching mode "pulled" always implies a live
            // holder, so a null one here means that body is gone -- let the lever spring back.
            if (m_LatchGripTimer > 0f || (m_Pulled && !m_Latching))
            {
                LetGo();
            }
        }
        else if (m_LatchGripTimer > 0f)
        {
            m_LatchGripTimer -= Time.deltaTime;
            if (m_LatchGripTimer <= 0f)
            {
                LetGo();
            }
            else
            {
                AimHand();
            }
        }
        else if (Vector3.Distance(m_Holder.position, GripAnchor) > m_ReleaseDistance)
        {
            LetGo();
        }
        else
        {
            // The handle is still swinging (and the holder can look around), so re-aim every
            // frame to keep the hand on the grip point.
            AimHand();
        }

        Animate();
    }

    // Remember which body is on the lever and find the two arm systems to drive. GrabIK sits on
    // the body ROOT and BodyGrabIK on the humanoid model child, so walk UP from the camera to the
    // root rather than assuming the camera is a direct child of it, then search back down.
    void Grip(Transform bodyCam)
    {
        m_Holder = bodyCam;
        m_ArmIK = bodyCam.GetComponentInParent<GrabIK>();
        Transform bodyRoot = m_ArmIK != null
            ? m_ArmIK.transform
            : (bodyCam.parent != null ? bodyCam.parent : bodyCam);
        m_BodyIK = bodyRoot.GetComponentInChildren<BodyGrabIK>(true);
        AimHand();
    }

    // Point the holder's first-person and third-person hands at the grip point. Passing the
    // handle's collider to GrabIK makes the fingers curl around it (a grip) instead of staying
    // flat as they do when pressing a button.
    void AimHand()
    {
        Vector3 anchor = GripAnchor;
        if (m_ArmIK != null)
        {
            m_ArmIK.SetReachTarget(anchor, m_GripCollider);
        }
        if (m_BodyIK != null)
        {
            m_BodyIK.SetReachTarget(anchor);
        }
    }

    // Take the holder's hand off the lever. Outside latching mode this also drops the lever,
    // which is what makes holding it cost a body.
    void LetGo()
    {
        if (m_ArmIK != null)
        {
            m_ArmIK.ClearReachTarget();
        }
        if (m_BodyIK != null)
        {
            m_BodyIK.ClearReachTarget();
        }
        m_ArmIK = null;
        m_BodyIK = null;
        m_Holder = null;
        m_LatchGripTimer = 0f;

        if (!m_Latching)
        {
            SetPulled(false);
        }
    }

    void SetPulled(bool pulled)
    {
        if (m_Pulled == pulled)
        {
            return;
        }
        m_Pulled = pulled;
        SetMet(pulled); // fires OnChanged, so the DoorLock re-evaluates exactly once
        (pulled ? m_OnPulled : m_OnReleased)?.Invoke();
    }

    // Ease the handle between its rest pose and the pulled pose, matching the SmoothStep feel of
    // Door.Animate and CloneButtonPanel.AnimateCap. Reversing mid-swing just walks the blend the
    // other way, so a released lever eases back from wherever it had got to.
    void Animate()
    {
        if (m_Handle == null)
        {
            return;
        }
        float target = m_Pulled ? 1f : 0f;
        float step = m_PullDuration > 0f ? Time.deltaTime / m_PullDuration : 1f;
        m_Blend = Mathf.MoveTowards(m_Blend, target, step);
        float angle = m_PullAngle * Mathf.SmoothStep(0f, 1f, m_Blend);
        m_Handle.localRotation = m_RestRotation * Quaternion.AngleAxis(angle, m_PullAxis);
    }
}
