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
    [SerializeField] Transform m_Hand;     // palm.01.R -- IK reach tip (base of the index+thumb)

    [Header("Reach target")]
    [Tooltip("Reach the hand to the actual held object's grab point (and follow it as it's pulled in). If off, reach the HoldPoint anchor instead.")]
    [SerializeField] bool m_ReachHeldObject = true;
    [Tooltip("Hold anchor. Hand orientation always comes from here; hand position too when Reach Held Object is off. Auto-found as 'HoldPoint' under the camera if empty.")]
    [SerializeField] Transform m_HoldPoint;
    [Tooltip("Positional nudge for the palm vs the wrist, applied in the hold anchor's frame.")]
    [SerializeField] Vector3 m_PalmOffset = Vector3.zero;
    [Tooltip("Apply the grip twist below while gripping. It is relative to the hand's rest pose, so zero leaves the natural pose (only affects the index/thumb metacarpal).")]
    [SerializeField] bool m_OrientHandToAnchor = true;
    [Tooltip("Extra local rotation added to the gripping hand. Tune until the palm wraps the object.")]
    [SerializeField] Vector3 m_HandGripEuler = Vector3.zero;

    [Header("Blend")]
    [Tooltip("How fast the arm eases between rest and grip, in weight units per second.")]
    [SerializeField] float m_BlendSpeed = 8f;

    [Header("Finger curl (contact-based: each phalanx stops when it touches the held object)")]
    [Tooltip("Max curl per finger phalanx. Reached only when nothing stops the finger; contact with the held collider stops it earlier so the hand conforms to the object.")]
    [SerializeField] float m_FingerCurlAngle = 55f;
    [SerializeField] Vector3 m_FingerCurlAxis = new Vector3(1f, 0f, 0f);
    [Tooltip("Max curl per thumb phalanx.")]
    [SerializeField] float m_ThumbCurlAngle = 35f;
    [SerializeField] Vector3 m_ThumbCurlAxis = new Vector3(0f, 0f, -1f);
    [Tooltip("Angle step used while curling a phalanx toward contact. Smaller = tighter fit but more checks.")]
    [SerializeField] float m_CurlStep = 6f;
    [Tooltip("Approx finger thickness for the contact test (metres, viewmodel scale). Increase if fingertips sink into objects; decrease if fingers stop short.")]
    [SerializeField] float m_FingerRadius = 0.012f;

    [Header("Editor tuning")]
    [Tooltip("Ignore the live held-state and drive the grip from the slider below, so you can pose the hand while editing.")]
    [SerializeField] bool m_DebugOverrideWeight = false;
    [SerializeField, Range(0f, 1f)] float m_DebugWeight = 1f;

    Camera m_Cam;
    GrabScript m_HoldSource; // the GrabScript that actually grabs (player root, tag "Main")

    /// <summary>
    /// Current grip blend (0 = relaxed, 1 = fully gripping). <see cref="ViewmodelArms"/>
    /// reads this to fade the retargeted right-arm swing out while the grip pose is active.
    /// </summary>
    public float GripWeight => m_Weight;
    bool m_Resolved;
    bool m_Posed;            // are we currently overriding the bones?
    float m_Weight;
    Vector3 m_LastTargetPos; // last reach target, held through the release blend

    Quaternion m_UpperRest, m_ForeRest, m_HandRest;

    // Finger bones grouped into per-finger chains (proximal -> distal) so each finger can be
    // curled from its base and stopped independently the moment it touches the held object.
    class Finger
    {
        public readonly List<Transform> Bones = new List<Transform>();
        public readonly List<Quaternion> Rest = new List<Quaternion>();
        public Quaternion[] Solved;   // last computed grip pose, held through the release blend
        public bool IsThumb;
    }
    readonly List<Finger> m_Fingers = new List<Finger>();
    bool m_HasSolve;                  // is Finger.Solved populated from a gripping frame?

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

        // A grip twist applied ON TOP of the hand's rest pose. The old code snapped the
        // bone's WORLD rotation to the hold anchor, but the arm rig's bones carry a large
        // baked roll while the anchor is near axis-aligned, so that mangled the hand.
        // Adding m_HandGripEuler to the rest pose keeps the natural pose at zero and lets
        // the palm be curled in by eye. NOTE: m_Hand is palm.01.R, which parents only the
        // index and thumb, so this twist rotates that cluster alone — leave it at zero for
        // a neutral grip that matches the middle/ring/pinky fingers.
        if (m_OrientHandToAnchor)
        {
            Quaternion gripLocal = m_HandRest * Quaternion.Euler(m_HandGripEuler);
            m_Hand.localRotation = Quaternion.Slerp(m_HandRest, gripLocal, m_Weight);
        }
    }

    void PoseFingers()
    {
        Collider held = ContactCollider();
        bool solveNow = held != null;

        for (int f = 0; f < m_Fingers.Count; f++)
        {
            Finger finger = m_Fingers[f];
            float maxAngle = finger.IsThumb ? m_ThumbCurlAngle : m_FingerCurlAngle;
            Vector3 axis = finger.IsThumb ? m_ThumbCurlAxis : m_FingerCurlAxis;

            // Start from rest so each phalanx is measured from a clean pose (last frame's
            // solve may have left it curled).
            for (int i = 0; i < finger.Bones.Count; i++)
            {
                finger.Bones[i].localRotation = finger.Rest[i];
            }

            // (Re)compute the grip while an object is held, or once when there's no cached grip
            // to reuse. On release the collider is gone (solveNow false) but Finger.Solved still
            // holds the last gripping pose, so the fingers retract from the object's actual shape
            // instead of snapping to a full fist.
            if (solveNow || !m_HasSolve)
            {
                // Greedy curl: bend each phalanx from the base outward and stop it the instant
                // its segment touches the held object; leave it curled so the next phalanx down
                // measures from the right place. With no collider, curl to the max angle.
                for (int i = 0; i < finger.Bones.Count; i++)
                {
                    Transform bone = finger.Bones[i];
                    Quaternion rest = finger.Rest[i];
                    float best = maxAngle;
                    if (solveNow)
                    {
                        best = 0f;
                        for (float a = m_CurlStep; a <= maxAngle; a += m_CurlStep)
                        {
                            bone.localRotation = rest * Quaternion.AngleAxis(a, axis);
                            if (SegmentContacts(bone, held))
                            {
                                break;
                            }
                            best = a;
                        }
                    }
                    Quaternion solved = rest * Quaternion.AngleAxis(best, axis);
                    bone.localRotation = solved;
                    finger.Solved[i] = solved;
                }
            }

            // Blend the solved grip back toward rest by the current grip weight.
            for (int i = 0; i < finger.Bones.Count; i++)
            {
                finger.Bones[i].localRotation = Quaternion.Slerp(finger.Rest[i], finger.Solved[i], m_Weight);
            }
        }

        if (solveNow)
        {
            m_HasSolve = true;
        }
    }

    // The object the fingers should wrap: the held body's collider, but only when it's a shape
    // Collider.ClosestPoint supports. Otherwise null -> fall back to the fixed max curl rather
    // than misreading contact (ClosestPoint returns the input point for a non-convex mesh, which
    // would read as an instant false contact and stop the fingers dead).
    Collider ContactCollider()
    {
        Collider c = m_HoldSource != null ? m_HoldSource.GetHeldCollider(m_Cam.transform) : null;
        if (c == null || !c.enabled)
        {
            return null;
        }
        if (c is MeshCollider mesh && !mesh.convex)
        {
            return null;
        }
        return c;
    }

    // True once the phalanx's segment is within a finger-radius of the held collider. Samples
    // along the bone toward its tip; skips the pivot end, which barely moves as the bone turns.
    bool SegmentContacts(Transform bone, Collider held)
    {
        Vector3 start = bone.position;
        Vector3 end = bone.childCount > 0
            ? bone.GetChild(0).position
            : start + (bone.parent != null ? (bone.position - bone.parent.position).normalized : bone.forward) * 0.02f;

        const int samples = 2;
        float sqrR = m_FingerRadius * m_FingerRadius;
        for (int s = 1; s <= samples; s++)
        {
            Vector3 p = Vector3.Lerp(start, end, s / (float)samples);
            Vector3 cp = held.ClosestPoint(p);
            if ((cp - p).sqrMagnitude <= sqrR)
            {
                return true;
            }
        }
        return false;
    }

    void ApplyRest()
    {
        m_UpperArm.localRotation = m_UpperRest;
        m_ForeArm.localRotation = m_ForeRest;
        m_Hand.localRotation = m_HandRest;
        for (int f = 0; f < m_Fingers.Count; f++)
        {
            Finger finger = m_Fingers[f];
            for (int i = 0; i < finger.Bones.Count; i++)
            {
                finger.Bones[i].localRotation = finger.Rest[i];
            }
        }
        m_HasSolve = false; // the grip fully released; recompute fresh on the next grab
    }

    // Grip only when THIS body is the one holding something -- i.e. the grab was made with
    // this body's camera. Binding the grip to the holder (not to whoever has the active view)
    // keeps the holder's hand gripped across control switches, while the other body's arms
    // stay relaxed even once it takes over the active camera.
    bool ShouldGrip()
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

    Vector3 ComputeTargetPosition()
    {
        // Prefer the actual grab point on the held body so the hand visibly reaches
        // out to meet the object and follows it in; fall back to the hold anchor.
        if (m_ReachHeldObject && m_HoldSource != null && m_HoldSource.IsHolding(m_Cam.transform))
        {
            Vector3 offset = m_HoldPoint != null ? m_HoldPoint.TransformVector(m_PalmOffset) : m_PalmOffset;
            return m_HoldSource.GetGrabWorldPosition(m_Cam.transform) + offset;
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
            // The IK reach tip is palm.01.R (base of the index+thumb), NOT the wrist hand.R:
            // driving the finger-base to the grab point makes the fingers wrap the object,
            // and rotating the tip is position-safe (its own rotation doesn't move where it
            // lands). Fingers are still collected from the whole wrist below.
            m_Hand = FindDeep(m_ForeArm, "palm.01.R");
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

        // Collect fingers from the true hand bone, NOT the IK tip. The four fingers live under
        // separate palm bones (palm.01..04.R) that are all children of hand.R, so palm.01.R by
        // itself only contains the index and thumb -- collecting from it leaves middle/ring/pinky
        // undriven (they stay splayed while the index curls alone).
        Transform handBone = FindDeep(m_ForeArm, "hand.R");
        CollectFingers(handBone != null ? handBone : m_Hand);
        return true;
    }

    void CollectFingers(Transform handRoot)
    {
        m_Fingers.Clear();
        var byKey = new Dictionary<string, Finger>();
        foreach (Transform t in handRoot.GetComponentsInChildren<Transform>())
        {
            if (t == handRoot || t.name.EndsWith("_end"))
            {
                continue;
            }
            bool isThumb = t.name.StartsWith("thumb");
            bool isFinger = t.name.StartsWith("f_");
            if (!isThumb && !isFinger)
            {
                continue;
            }
            // Group phalanges into per-finger chains by name ("f_index.01.R" -> "f_index").
            // GetComponentsInChildren is depth-first, so bones arrive proximal -> distal.
            string key = t.name.Split('.')[0];
            if (!byKey.TryGetValue(key, out Finger finger))
            {
                finger = new Finger { IsThumb = isThumb };
                byKey[key] = finger;
                m_Fingers.Add(finger);
            }
            finger.Bones.Add(t);
            finger.Rest.Add(t.localRotation);
        }
        foreach (Finger finger in m_Fingers)
        {
            finger.Solved = new Quaternion[finger.Bones.Count];
        }
        m_HasSolve = false;
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
