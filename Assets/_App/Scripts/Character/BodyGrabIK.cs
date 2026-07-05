using UnityEngine;

/// <summary>
/// Cosmetic third-person grab pose for the visible humanoid body model. When THIS body
/// is the one holding an object, it uses Unity's built-in Humanoid IK to reach the hand
/// out toward the held object, so a clone (or the player seen from the other split-screen
/// view) visibly extends its arm while carrying something.
///
/// This is purely for looks and is independent of <see cref="GrabIK"/>: that poses the
/// *first-person* viewmodel arms, which only the holder's own camera renders. This one
/// poses the third-person <c>FirstCharacter</c> model that the OTHER camera renders.
///
/// Must live on the same GameObject as the humanoid <see cref="Animator"/>
/// (<c>FirstCharacter</c>), because <c>OnAnimatorIK</c> is only dispatched there, and the
/// animator controller's layer needs "IK Pass" enabled. It resolves the single grabber by
/// tag "Main" and reaches only while <see cref="GrabScript.IsHolding"/> reports this body's
/// camera as a holder -- the same holder gating <see cref="GrabIK"/> uses -- so the reach
/// stays with the holder across control switches and the idle body keeps its normal pose.
/// A dismissed corpse has its Animator disabled, so <c>OnAnimatorIK</c> stops firing and
/// no reach is applied to ragdolls.
/// </summary>
[RequireComponent(typeof(Animator))]
public class BodyGrabIK : MonoBehaviour
{
    [Tooltip("Master weight of the reach. 0 disables the effect entirely; 1 drives the hand fully to the held object.")]
    [SerializeField, Range(0f, 1f)] float m_PositionWeight = 1f;
    [Tooltip("How strongly the hand is rotated toward the object. Leave at 0 for a natural reach; raise it if you want the palm to face the object.")]
    [SerializeField, Range(0f, 1f)] float m_RotationWeight = 0f;
    [Tooltip("How fast the arm eases in and out of the reach, in weight units per second.")]
    [SerializeField] float m_BlendSpeed = 8f;
    [Tooltip("Which hand reaches for the object.")]
    [SerializeField] bool m_UseRightHand = true;
    [Tooltip("Nudge for the reach target, in the holder camera's local space (right / up / forward). Tune if the third-person hand sits too high, low, or close.")]
    [SerializeField] Vector3 m_ReachOffset = Vector3.zero;

    Animator m_Animator;
    Camera m_Cam;
    GrabScript m_HoldSource; // the single grabber that actually holds things (player root, tag "Main")
    float m_Weight;          // current eased reach weight
    bool m_Resolved;

    void Awake()
    {
        m_Animator = GetComponent<Animator>();
    }

    // The body's own camera lives under the body root (this model's parent). Asking the grabber
    // whether it holds anything for this camera tells us whether THIS body is carrying the object.
    bool Resolve()
    {
        Transform bodyRoot = transform.parent != null ? transform.parent : transform;
        m_Cam = bodyRoot.GetComponentInChildren<Camera>(true);
        return m_Cam != null;
    }

    bool IsHolder()
    {
        if (m_Cam == null)
        {
            return false;
        }
        if (m_HoldSource == null)
        {
            GameObject main = GameObject.FindGameObjectWithTag("Main");
            if (main != null)
            {
                m_HoldSource = main.GetComponent<GrabScript>();
            }
        }
        return m_HoldSource != null
            && m_HoldSource.IsHolding(m_Cam.transform);
    }

    void OnAnimatorIK(int layerIndex)
    {
        if (m_Animator == null)
        {
            return;
        }
        if (!m_Resolved)
        {
            m_Resolved = Resolve();
        }

        AvatarIKGoal goal = m_UseRightHand ? AvatarIKGoal.RightHand : AvatarIKGoal.LeftHand;

        // Ease the reach in while holding and out when not, so the arm never snaps.
        bool reach = m_Resolved && IsHolder();
        float target = reach ? m_PositionWeight : 0f;
        m_Weight = Mathf.MoveTowards(m_Weight, target, m_BlendSpeed * Time.deltaTime);

        if (m_Weight <= 0.0001f)
        {
            m_Animator.SetIKPositionWeight(goal, 0f);
            m_Animator.SetIKRotationWeight(goal, 0f);
            return;
        }

        Vector3 targetPos = m_HoldSource.GetGrabWorldPosition(m_Cam.transform);
        if (m_ReachOffset != Vector3.zero)
        {
            targetPos += m_Cam.transform.TransformVector(m_ReachOffset);
        }

        m_Animator.SetIKPositionWeight(goal, m_Weight);
        m_Animator.SetIKPosition(goal, targetPos);

        if (m_RotationWeight > 0f)
        {
            HumanBodyBones upperArm = m_UseRightHand ? HumanBodyBones.RightUpperArm : HumanBodyBones.LeftUpperArm;
            Transform shoulder = m_Animator.GetBoneTransform(upperArm);
            Vector3 dir = shoulder != null ? targetPos - shoulder.position : Vector3.zero;
            if (dir.sqrMagnitude > 1e-6f)
            {
                m_Animator.SetIKRotationWeight(goal, m_Weight * m_RotationWeight);
                m_Animator.SetIKRotation(goal, Quaternion.LookRotation(dir));
            }
        }
    }
}
