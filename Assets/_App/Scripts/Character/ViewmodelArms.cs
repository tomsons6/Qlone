using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Gives the first-person viewmodel <c>Arms</c> some life without any hand-authored arm
/// clips, in two layers:
///
///  1. <b>Retarget</b> — copies the body model's animated arm swing (Idle / Walking) onto
///     the viewmodel arm bones. The body is a Humanoid (Mixamo-named) rig while the
///     viewmodel is a separate Rigify-named rig with different bone names *and* different
///     bone axes, so we can't name-match, share a controller, or Humanoid-retarget (an
///     arms-only rig isn't a valid Humanoid avatar). Instead we read each body arm bone's
///     rotation *relative to a captured neutral pose* and re-express that delta in the
///     viewmodel bone's own local frame via a fixed basis change captured at startup.
///     Result: the viewmodel arms swing in sync with the walk, starting from their own
///     imported hold pose. The body has no finger bones, so fingers are left to
///     <see cref="GrabIK"/> / their rest pose.
///
///  2. <b>Sway</b> — procedural walk bob (scaled by this body's real movement Speed), idle
///     breathing, and look-sway, applied to the whole <c>Arms</c> container so the arms
///     feel alive even standing still.
///
/// Lives on the body root next to <see cref="GrabIK"/> / <see cref="GrabScript"/> so it
/// propagates from MainCharecter to the CloneNested variant; each body drives its own arms
/// from its own body Animator. Runs after GrabIK (execution order) so the grip solve wins
/// on the right arm: while gripping, the right-arm swing fades out by GrabIK's grip weight.
/// If the retarget ever looks off for a rig, drop <see cref="m_SwingScale"/> or untick
/// <see cref="m_Retarget"/> to fall back to pure sway.
/// </summary>
[DefaultExecutionOrder(100)]
public class ViewmodelArms : MonoBehaviour
{
    [Header("Retarget body arm swing -> viewmodel arms")]
    [SerializeField] bool m_Retarget = true;
    [Tooltip("How much of the body's arm swing to copy (0 = none, 1 = full). Viewmodel swings usually read better toned down.")]
    [SerializeField, Range(0f, 1.5f)] float m_SwingScale = 0.7f;

    [Header("Procedural sway (applied to the whole Arms container)")]
    [SerializeField] bool m_Sway = true;
    [Tooltip("Idle breathing: vertical bob amplitude (metres) and speed while standing still.")]
    [SerializeField] float m_BreathAmplitude = 0.004f;
    [SerializeField] float m_BreathSpeed = 1.4f;
    [Tooltip("Walk bob: amplitude (metres) at full speed, cycle speed, and the movement Speed at which the bob is full.")]
    [SerializeField] float m_BobAmplitude = 0.02f;
    [SerializeField] float m_BobSpeed = 9f;
    [SerializeField] float m_BobFullSpeed = 3f;
    [Tooltip("Look-sway: how far (degrees) the arms lag behind mouse look, and how fast they catch up. Active-camera body only.")]
    [SerializeField] float m_LookSwayAngle = 3f;
    [SerializeField] float m_LookSwaySmooth = 10f;

    Animator m_Body;
    Camera m_Cam;
    Transform m_ArmsRoot;   // the Camera/Arms container (parent of both the mesh and the metarig)
    GrabIK m_GrabIK;
    bool m_Resolved;
    bool m_Captured;

    Vector3 m_BasePos;      // Arms container rest offset, captured before any sway
    Quaternion m_BaseRot;
    Vector3 m_LookSway;

    struct BoneMap
    {
        public Transform body;
        public Transform arm;
        public Quaternion bodyRestLocal; // body bone local rotation at the captured neutral pose
        public Quaternion armRestLocal;  // viewmodel bone local rotation at rest
        public Quaternion basis;         // re-expresses a body-local rotation axis in arm-local space
        public bool isRight;             // right arm is also driven by GrabIK -> fade the swing under grip
    }
    readonly List<BoneMap> m_Bones = new List<BoneMap>();

    // Body Humanoid bone -> viewmodel (Rigify) bone name. Shoulder/fingers omitted: the
    // viewmodel arm starts at upper_arm (no shoulder) and the body has no finger bones.
    static readonly (HumanBodyBones body, string arm, bool right)[] k_Map =
    {
        (HumanBodyBones.RightUpperArm, "upper_arm.R", true),
        (HumanBodyBones.RightLowerArm, "forearm.R",   true),
        (HumanBodyBones.RightHand,     "hand.R",      true),
        (HumanBodyBones.LeftUpperArm,  "upper_arm.L", false),
        (HumanBodyBones.LeftLowerArm,  "forearm.L",   false),
        (HumanBodyBones.LeftHand,      "hand.L",      false),
    };

    void Start()
    {
        m_GrabIK = GetComponent<GrabIK>();
        m_Cam = GetComponentInChildren<Camera>(true);
        m_Body = ResolveBodyAnimator();

        Transform metarig = m_Cam != null ? FindDeep(m_Cam.transform, "metarig") : null;
        if (metarig != null && metarig.parent != null)
        {
            m_ArmsRoot = metarig.parent;
            m_BasePos = m_ArmsRoot.localPosition;
            m_BaseRot = m_ArmsRoot.localRotation;
        }
        m_Resolved = m_Cam != null && m_ArmsRoot != null;
    }

    // The viewmodel arms carry no controller, so pick the model whose Animator actually
    // drives locomotion (has a controller assigned) -- the body. Mirrors CharacterAnimator.
    Animator ResolveBodyAnimator()
    {
        Animator fallback = null;
        foreach (Animator a in GetComponentsInChildren<Animator>(true))
        {
            if (a.runtimeAnimatorController != null)
            {
                return a;
            }
            if (fallback == null)
            {
                fallback = a;
            }
        }
        return fallback;
    }

    void Update()
    {
        // Sway runs in Update (before the Animator + all LateUpdates) so GrabIK solves the
        // grip from the already-swayed shoulder position rather than lagging a frame.
        if (!m_Resolved || !m_Sway || m_ArmsRoot == null)
        {
            return;
        }

        float t = Time.time;
        float dt = Time.deltaTime;

        // Idle breathing (always) + walk bob (scaled by this body's own movement speed).
        float breath = Mathf.Sin(t * m_BreathSpeed) * m_BreathAmplitude;
        float speed = m_Body != null ? m_Body.GetFloat("Speed") : 0f;
        float bobT = m_BobFullSpeed > 0.001f ? Mathf.Clamp01(speed / m_BobFullSpeed) : 0f;
        float bobY = Mathf.Sin(t * m_BobSpeed) * m_BobAmplitude * bobT;
        float bobX = Mathf.Cos(t * m_BobSpeed * 0.5f) * m_BobAmplitude * 0.5f * bobT;

        // Look-sway only for the body currently being looked with (its camera is active).
        Vector3 swayTarget = Vector3.zero;
        if (m_Cam.CompareTag("MainCamera"))
        {
            float mx = Input.GetAxis("Mouse X");
            float my = Input.GetAxis("Mouse Y");
            swayTarget = new Vector3(my, -mx, -mx) * m_LookSwayAngle;
        }
        m_LookSway = Vector3.Lerp(m_LookSway, swayTarget, 1f - Mathf.Exp(-m_LookSwaySmooth * dt));

        m_ArmsRoot.localPosition = m_BasePos + new Vector3(bobX, bobY + breath, 0f);
        m_ArmsRoot.localRotation = m_BaseRot * Quaternion.Euler(m_LookSway);
    }

    // Retarget runs after GrabIK (execution order 100 > 0) so our bone writes layer on top
    // of the grip solve, and the right arm can fade between the two by grip weight.
    void LateUpdate()
    {
        if (!m_Resolved || !m_Retarget)
        {
            return;
        }
        if (!m_Captured)
        {
            // Capture the neutral reference for both rigs once, after the body Animator has
            // applied its first (idle) pose but before we've touched the viewmodel bones.
            m_Captured = Capture();
            return; // delta is identity on the capture frame anyway
        }

        float grip = m_GrabIK != null ? Mathf.Clamp01(m_GrabIK.GripWeight) : 0f;

        for (int i = 0; i < m_Bones.Count; i++)
        {
            BoneMap m = m_Bones[i];
            if (m.body == null || m.arm == null)
            {
                continue;
            }

            // Body bone's rotation change since the neutral pose, in the body bone's local
            // frame, re-expressed in the arm bone's local frame via the rest basis.
            Quaternion bodyDelta = Quaternion.Inverse(m.bodyRestLocal) * m.body.localRotation;
            Quaternion armDelta = m.basis * bodyDelta * Quaternion.Inverse(m.basis);
            if (m_SwingScale != 1f)
            {
                armDelta = Quaternion.Slerp(Quaternion.identity, armDelta, m_SwingScale);
            }
            Quaternion swing = m.armRestLocal * armDelta;

            if (m.isRight && grip > 0.0001f)
            {
                // GrabIK already wrote its grip pose this frame; fade our swing out under it.
                Quaternion gripPose = m.arm.localRotation;
                m.arm.localRotation = Quaternion.Slerp(swing, gripPose, grip);
            }
            else
            {
                m.arm.localRotation = swing;
            }
        }
    }

    bool Capture()
    {
        if (m_Body == null || !m_Body.isHuman)
        {
            // Not a Humanoid body -> nothing to retarget from. Stop trying; sway still runs.
            m_Retarget = false;
            return false;
        }
        Transform metarig = FindDeep(m_Cam.transform, "metarig");
        if (metarig == null)
        {
            return false;
        }

        m_Bones.Clear();
        foreach (var e in k_Map)
        {
            Transform body = m_Body.GetBoneTransform(e.body);
            Transform arm = FindDeep(metarig, e.arm);
            if (body == null || arm == null)
            {
                continue;
            }
            m_Bones.Add(new BoneMap
            {
                body = body,
                arm = arm,
                bodyRestLocal = body.localRotation,
                armRestLocal = arm.localRotation,
                basis = Quaternion.Inverse(arm.rotation) * body.rotation,
                isRight = e.right,
            });
        }
        return m_Bones.Count > 0;
    }

    static Transform FindDeep(Transform root, string childName)
    {
        if (root == null)
        {
            return null;
        }
        if (root.name == childName)
        {
            return root;
        }
        foreach (Transform child in root)
        {
            Transform found = FindDeep(child, childName);
            if (found != null)
            {
                return found;
            }
        }
        return null;
    }
}
