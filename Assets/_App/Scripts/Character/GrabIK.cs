using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Procedural first-person grab pose for the rigged <c>Arms</c> mesh. There are no
/// hand-authored grab animations, so this bends the right arm with a two-bone IK
/// solver until the hand reaches the hold anchor, and curls the fingers into a
/// grip. When nothing is held it blends back to the imported rest pose.
///
/// Lives on the body root (next to <see cref="GrabScript"/>) so it propagates from
/// MainCharecter to the CloneNested variant. Each body resolves its own arm bones
/// under its child <c>Camera/Arms</c>. Because <see cref="GrabScript"/> always holds
/// the object at the active camera's HoldPoint (it follows <c>Camera.main</c>), this
/// only drives the arm while its own camera is the active one — so control switches
/// hand the grip pose to whichever body is now in control, and the idle body relaxes.
///
/// All angles/axes are serialized: the bend direction and finger curl depend on the
/// rig's bone orientations, so tune them in the Inspector. Tick
/// <see cref="m_DebugOverrideWeight"/> to scrub the grip pose without playing.
/// </summary>
public class GrabIK : MonoBehaviour
{
    [Header("Arm bones (auto-resolved by name under the body's Camera/Arms if empty)")]
    [SerializeField] Transform m_UpperArm; // upper_arm.R
    [SerializeField] Transform m_ForeArm;  // forearm.R
    [SerializeField] Transform m_Hand;     // hand.R

    [Header("Reach target")]
    [Tooltip("Reach the hand to the actual held object's grab point (and follow it as it's pulled in). If off, reach the HoldPoint anchor instead.")]
    [SerializeField] bool m_ReachHeldObject = true;
    [Tooltip("Hold anchor. Hand orientation always comes from here; hand position too when Reach Held Object is off. Auto-found as 'HoldPoint' under the camera if empty.")]
    [SerializeField] Transform m_HoldPoint;
    [Tooltip("Positional nudge for the palm vs the wrist, applied in the hold anchor's frame.")]
    [SerializeField] Vector3 m_PalmOffset = Vector3.zero;
    [Tooltip("Rotate the hand to the hold anchor so it looks gripped rather than dangling.")]
    [SerializeField] bool m_OrientHandToAnchor = true;
    [Tooltip("Extra local rotation added to the gripping hand. Tune until the palm wraps the object.")]
    [SerializeField] Vector3 m_HandGripEuler = Vector3.zero;

    [Header("Blend")]
    [Tooltip("How fast the arm eases between rest and grip, in weight units per second.")]
    [SerializeField] float m_BlendSpeed = 8f;

    [Header("Finger curl (per phalanx, blended by grip weight)")]
    [SerializeField] float m_FingerCurlAngle = 55f;
    [SerializeField] Vector3 m_FingerCurlAxis = new Vector3(-1f, 0f, 0f);
    [SerializeField] float m_ThumbCurlAngle = 35f;
    [SerializeField] Vector3 m_ThumbCurlAxis = new Vector3(0f, 0f, -1f);

    [Header("Editor tuning")]
    [Tooltip("Ignore the live held-state and drive the grip from the slider below, so you can pose the hand while editing.")]
    [SerializeField] bool m_DebugOverrideWeight = false;
    [SerializeField, Range(0f, 1f)] float m_DebugWeight = 1f;

    Camera m_Cam;
    GrabScript m_HoldSource; // the GrabScript that actually grabs (player root, tag "Main")
    bool m_Resolved;
    bool m_Posed;            // are we currently overriding the bones?
    float m_Weight;
    Vector3 m_LastTargetPos; // last reach target, held through the release blend

    Quaternion m_UpperRest, m_ForeRest, m_HandRest;
    readonly List<Transform> m_FingerBones = new List<Transform>();
    readonly List<Quaternion> m_FingerRest = new List<Quaternion>();
    readonly List<Transform> m_ThumbBones = new List<Transform>();
    readonly List<Quaternion> m_ThumbRest = new List<Quaternion>();

    // IK runs after the Animator has updated, so our bone writes win.
    void LateUpdate()
    {
        if (!m_Resolved)
        {
            m_Resolved = Resolve();
            if (!m_Resolved)
            {
                return;
            }
        }

        bool grip = ShouldGrip();
        float target = grip ? 1f : 0f;
        m_Weight = m_DebugOverrideWeight
            ? m_DebugWeight
            : Mathf.MoveTowards(m_Weight, target, m_BlendSpeed * Time.deltaTime);

        if (m_Weight <= 0.0001f)
        {
            // Restore the rest pose once, then stop touching the bones so any
            // future idle animation on the arms can drive them.
            if (m_Posed)
            {
                ApplyRest();
                m_Posed = false;
            }
            return;
        }
        m_Posed = true;

        // Track the live target while gripping (or scrubbing in the editor); hold the
        // last one through the release blend so the arm retracts from where it was.
        if (grip || m_DebugOverrideWeight)
        {
            m_LastTargetPos = ComputeTargetPosition();
        }

        PoseArm(m_LastTargetPos);
        PoseFingers();
    }

    void PoseArm(Vector3 targetPos)
    {
        // Solve from the rest pose every frame so the result is deterministic and
        // the elbow bends in the rig's natural direction.
        m_UpperArm.localRotation = m_UpperRest;
        m_ForeArm.localRotation = m_ForeRest;
        m_Hand.localRotation = m_HandRest;

        SolveTwoBone(m_UpperArm, m_ForeArm, m_Hand, targetPos);

        // Blend the full grip solve back toward rest by the current weight.
        Quaternion upperSolved = m_UpperArm.localRotation;
        Quaternion foreSolved = m_ForeArm.localRotation;
        m_UpperArm.localRotation = Quaternion.Slerp(m_UpperRest, upperSolved, m_Weight);
        m_ForeArm.localRotation = Quaternion.Slerp(m_ForeRest, foreSolved, m_Weight);

        if (m_OrientHandToAnchor && m_HoldPoint != null && m_Hand.parent != null)
        {
            Quaternion gripWorld = m_HoldPoint.rotation * Quaternion.Euler(m_HandGripEuler);
            Quaternion gripLocal = Quaternion.Inverse(m_Hand.parent.rotation) * gripWorld;
            m_Hand.localRotation = Quaternion.Slerp(m_HandRest, gripLocal, m_Weight);
        }
    }

    void PoseFingers()
    {
        for (int i = 0; i < m_FingerBones.Count; i++)
        {
            Quaternion closed = m_FingerRest[i] * Quaternion.AngleAxis(m_FingerCurlAngle, m_FingerCurlAxis);
            m_FingerBones[i].localRotation = Quaternion.Slerp(m_FingerRest[i], closed, m_Weight);
        }
        for (int i = 0; i < m_ThumbBones.Count; i++)
        {
            Quaternion closed = m_ThumbRest[i] * Quaternion.AngleAxis(m_ThumbCurlAngle, m_ThumbCurlAxis);
            m_ThumbBones[i].localRotation = Quaternion.Slerp(m_ThumbRest[i], closed, m_Weight);
        }
    }

    void ApplyRest()
    {
        m_UpperArm.localRotation = m_UpperRest;
        m_ForeArm.localRotation = m_ForeRest;
        m_Hand.localRotation = m_HandRest;
        for (int i = 0; i < m_FingerBones.Count; i++)
        {
            m_FingerBones[i].localRotation = m_FingerRest[i];
        }
        for (int i = 0; i < m_ThumbBones.Count; i++)
        {
            m_ThumbBones[i].localRotation = m_ThumbRest[i];
        }
    }

    // Grip only when this body's arms are the ones on screen (its camera is the
    // active MainCamera) and the grabbing body is holding something.
    bool ShouldGrip()
    {
        if (m_Cam == null || !m_Cam.CompareTag("MainCamera"))
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
        return m_HoldSource != null && m_HoldSource.HandOccupied;
    }

    Vector3 ComputeTargetPosition()
    {
        // Prefer the actual grab point on the held body so the hand visibly reaches
        // out to meet the object and follows it in; fall back to the hold anchor.
        if (m_ReachHeldObject && m_HoldSource != null && m_HoldSource.HandOccupied)
        {
            Vector3 offset = m_HoldPoint != null ? m_HoldPoint.TransformVector(m_PalmOffset) : m_PalmOffset;
            return m_HoldSource.GrabWorldPosition + offset;
        }
        if (m_HoldPoint != null)
        {
            return m_HoldPoint.TransformPoint(m_PalmOffset);
        }
        return m_Cam.transform.position + m_Cam.transform.forward * 0.5f;
    }

    bool Resolve()
    {
        m_Cam = GetComponentInChildren<Camera>(true);
        if (m_Cam == null)
        {
            return false;
        }

        Transform arms = FindDeep(m_Cam.transform, "Arms");
        Transform searchRoot = arms != null ? arms : transform;
        if (m_UpperArm == null)
        {
            m_UpperArm = FindDeep(searchRoot, "upper_arm.R");
        }
        if (m_ForeArm == null && m_UpperArm != null)
        {
            m_ForeArm = FindDeep(m_UpperArm, "forearm.R");
        }
        if (m_Hand == null && m_ForeArm != null)
        {
            m_Hand = FindDeep(m_ForeArm, "hand.R");
        }
        if (m_UpperArm == null || m_ForeArm == null || m_Hand == null)
        {
            return false;
        }

        if (m_HoldPoint == null)
        {
            m_HoldPoint = FindDeep(m_Cam.transform, "HoldPoint");
        }

        // Capture the imported rest pose to blend against (bones are untouched here).
        m_UpperRest = m_UpperArm.localRotation;
        m_ForeRest = m_ForeArm.localRotation;
        m_HandRest = m_Hand.localRotation;
        CollectFingers(m_Hand);
        return true;
    }

    void CollectFingers(Transform handRoot)
    {
        m_FingerBones.Clear();
        m_FingerRest.Clear();
        m_ThumbBones.Clear();
        m_ThumbRest.Clear();
        foreach (Transform t in handRoot.GetComponentsInChildren<Transform>())
        {
            if (t == handRoot || t.name.EndsWith("_end"))
            {
                continue;
            }
            if (t.name.StartsWith("thumb"))
            {
                m_ThumbBones.Add(t);
                m_ThumbRest.Add(t.localRotation);
            }
            else if (t.name.StartsWith("f_"))
            {
                m_FingerBones.Add(t);
                m_FingerRest.Add(t.localRotation);
            }
        }
    }

    // Analytic two-bone IK (law of cosines). Bends 'a' (upper) and 'b' (fore) so the
    // tip 'c' (hand) reaches 'target', preserving the current bend plane.
    static void SolveTwoBone(Transform a, Transform b, Transform c, Vector3 target)
    {
        Vector3 pa = a.position;
        Vector3 pb = b.position;
        Vector3 pc = c.position;

        Vector3 ab = pb - pa;
        Vector3 cb = pb - pc;
        Vector3 ac = pc - pa;
        Vector3 at = target - pa;

        float lab = ab.magnitude;
        float lcb = cb.magnitude;
        float lat = Mathf.Clamp(at.magnitude, 0.001f, lab + lcb - 0.001f);
        if (lab < 1e-5f || lcb < 1e-5f)
        {
            return;
        }

        // Current interior angles of triangle a-b-c, and the upper->tip vs upper->target spread.
        float curUpper = Mathf.Acos(Mathf.Clamp(Vector3.Dot(ac.normalized, ab.normalized), -1f, 1f));
        float curElbow = Mathf.Acos(Mathf.Clamp(Vector3.Dot((pa - pb).normalized, (pc - pb).normalized), -1f, 1f));

        // Desired interior angles after the tip reaches the target (law of cosines).
        float desUpper = Mathf.Acos(Mathf.Clamp((lcb * lcb - lab * lab - lat * lat) / (-2f * lab * lat), -1f, 1f));
        float desElbow = Mathf.Acos(Mathf.Clamp((lat * lat - lab * lab - lcb * lcb) / (-2f * lab * lcb), -1f, 1f));

        // Bend plane normal; fall back to an arbitrary perpendicular if the arm is straight.
        Vector3 axis = Vector3.Cross(ac, ab);
        if (axis.sqrMagnitude < 1e-8f)
        {
            axis = Vector3.Cross(at, Vector3.up);
            if (axis.sqrMagnitude < 1e-8f)
            {
                axis = Vector3.Cross(at, Vector3.right);
            }
        }
        axis = axis.normalized;

        Vector3 axisUpperLocal = Quaternion.Inverse(a.rotation) * axis;
        Vector3 axisElbowLocal = Quaternion.Inverse(b.rotation) * axis;
        a.localRotation = a.localRotation * Quaternion.AngleAxis((desUpper - curUpper) * Mathf.Rad2Deg, axisUpperLocal);
        b.localRotation = b.localRotation * Quaternion.AngleAxis((desElbow - curElbow) * Mathf.Rad2Deg, axisElbowLocal);

        // Swing the whole arm so the tip lands on the target.
        Vector3 acNew = c.position - a.position;
        Vector3 swingAxis = Vector3.Cross(acNew, at);
        if (swingAxis.sqrMagnitude > 1e-8f)
        {
            float swingAngle = Mathf.Acos(Mathf.Clamp(Vector3.Dot(acNew.normalized, at.normalized), -1f, 1f)) * Mathf.Rad2Deg;
            Vector3 swingAxisLocal = Quaternion.Inverse(a.rotation) * swingAxis.normalized;
            a.localRotation = a.localRotation * Quaternion.AngleAxis(swingAngle, swingAxisLocal);
        }
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
