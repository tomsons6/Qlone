using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Builds a physics ragdoll at runtime from a Humanoid <see cref="Animator"/>.
///
/// It adds a Rigidbody + Collider to the 11 standard ragdoll bones and links
/// them with CharacterJoints, then switches the body to physics control. Because
/// every collider is sized in the bone's own local space, it stays correct
/// regardless of how the character is scaled in the hierarchy.
///
/// Call <see cref="Create"/> once, at the moment the character should go limp.
/// </summary>
public static class HumanoidRagdoll
{
    /// <summary>
    /// Turns the animated character into an active ragdoll. Returns false (and
    /// changes nothing) if the rig isn't a complete Humanoid, so callers can
    /// fall back to their previous behaviour.
    /// </summary>
    public static bool Create(Animator animator, Vector3 initialVelocity)
    {
        if (animator == null || !animator.isHuman)
        {
            return false;
        }

        Transform pelvis = animator.GetBoneTransform(HumanBodyBones.Hips);
        Transform spine = animator.GetBoneTransform(HumanBodyBones.Spine);
        Transform head = animator.GetBoneTransform(HumanBodyBones.Head);
        Transform lUpLeg = animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
        Transform lLoLeg = animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
        Transform lFoot = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
        Transform rUpLeg = animator.GetBoneTransform(HumanBodyBones.RightUpperLeg);
        Transform rLoLeg = animator.GetBoneTransform(HumanBodyBones.RightLowerLeg);
        Transform rFoot = animator.GetBoneTransform(HumanBodyBones.RightFoot);
        Transform lUpArm = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        Transform lLoArm = animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
        Transform lHand = animator.GetBoneTransform(HumanBodyBones.LeftHand);
        Transform rUpArm = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
        Transform rLoArm = animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
        Transform rHand = animator.GetBoneTransform(HumanBodyBones.RightHand);

        if (pelvis == null || spine == null || head == null ||
            lUpLeg == null || lLoLeg == null || lFoot == null ||
            rUpLeg == null || rLoLeg == null || rFoot == null ||
            lUpArm == null || lLoArm == null || lHand == null ||
            rUpArm == null || rLoArm == null || rHand == null)
        {
            return false;
        }

        // Stop the animation so physics owns the pose from here on.
        animator.enabled = false;

        // --- Bodies + colliders (kinematic until every joint is wired up) ---
        List<Rigidbody> bodies = new List<Rigidbody>();

        AddPelvisCollider(pelvis, spine, lUpLeg, rUpLeg);
        bodies.Add(AddBody(pelvis, 2.5f));

        AddCapsule(spine, head, 0.22f);
        bodies.Add(AddBody(spine, 2.4f));

        AddHeadCollider(head);
        bodies.Add(AddBody(head, 1.0f));

        AddCapsule(lUpLeg, lLoLeg, 0.18f);
        bodies.Add(AddBody(lUpLeg, 1.5f));
        AddCapsule(lLoLeg, lFoot, 0.16f);
        bodies.Add(AddBody(lLoLeg, 1.2f));
        AddCapsule(rUpLeg, rLoLeg, 0.18f);
        bodies.Add(AddBody(rUpLeg, 1.5f));
        AddCapsule(rLoLeg, rFoot, 0.16f);
        bodies.Add(AddBody(rLoLeg, 1.2f));

        AddCapsule(lUpArm, lLoArm, 0.16f);
        bodies.Add(AddBody(lUpArm, 1.0f));
        AddCapsule(lLoArm, lHand, 0.14f);
        bodies.Add(AddBody(lLoArm, 0.8f));
        AddCapsule(rUpArm, rLoArm, 0.16f);
        bodies.Add(AddBody(rUpArm, 1.0f));
        AddCapsule(rLoArm, rHand, 0.14f);
        bodies.Add(AddBody(rLoArm, 0.8f));

        // --- Joints (child bone -> parent body) ---
        AddJoint(spine, pelvis);
        AddJoint(head, spine);
        AddJoint(lUpLeg, pelvis);
        AddJoint(lLoLeg, lUpLeg);
        AddJoint(rUpLeg, pelvis);
        AddJoint(rLoLeg, rUpLeg);
        AddJoint(lUpArm, spine);
        AddJoint(lLoArm, lUpArm);
        AddJoint(rUpArm, spine);
        AddJoint(rLoArm, rUpArm);

        // --- Hand control to physics ---
        foreach (Rigidbody rb in bodies)
        {
            rb.isKinematic = false;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.linearVelocity = initialVelocity;
        }

        return true;
    }

    static Rigidbody AddBody(Transform bone, float mass)
    {
        Rigidbody rb = bone.gameObject.AddComponent<Rigidbody>();
        rb.mass = mass;
        rb.isKinematic = true; // flipped on once all joints exist
        return rb;
    }

    // Capsule running from the bone's pivot to its child along the limb.
    static void AddCapsule(Transform bone, Transform childEnd, float radiusRatio)
    {
        Vector3 local = bone.InverseTransformPoint(childEnd.position);
        float length = local.magnitude;

        CapsuleCollider cap = bone.gameObject.AddComponent<CapsuleCollider>();
        cap.direction = DominantAxis(local);
        cap.height = length;
        cap.radius = length * radiusRatio;
        cap.center = local * 0.5f;
    }

    static void AddHeadCollider(Transform head)
    {
        // Use the head's tip child (Head_end) to estimate size when present.
        Vector3 local = head.childCount > 0
            ? head.InverseTransformPoint(head.GetChild(0).position)
            : Vector3.up * 0.2f;

        SphereCollider sphere = head.gameObject.AddComponent<SphereCollider>();
        sphere.radius = local.magnitude * 0.5f;
        sphere.center = local * 0.5f;
    }

    static void AddPelvisCollider(Transform pelvis, Transform spine, Transform lUpLeg, Transform rUpLeg)
    {
        Vector3 toSpine = pelvis.InverseTransformPoint(spine.position);
        Vector3 hipSpan = pelvis.InverseTransformPoint(lUpLeg.position) - pelvis.InverseTransformPoint(rUpLeg.position);

        int heightAxis = DominantAxis(toSpine);
        int widthAxis = DominantAxis(hipSpan);

        BoxCollider box = pelvis.gameObject.AddComponent<BoxCollider>();
        box.center = toSpine * 0.5f;

        if (widthAxis == heightAxis)
        {
            // Degenerate measurement — fall back to a roughly cubic box.
            float s = toSpine.magnitude;
            box.size = new Vector3(s, s, s);
            return;
        }

        int depthAxis = 3 - heightAxis - widthAxis;
        float width = hipSpan.magnitude;
        Vector3 size = Vector3.zero;
        size[heightAxis] = toSpine.magnitude;
        size[widthAxis] = width;
        size[depthAxis] = width * 0.6f;
        box.size = size;
    }

    static void AddJoint(Transform bone, Transform connectedBone)
    {
        CharacterJoint joint = bone.gameObject.AddComponent<CharacterJoint>();
        joint.connectedBody = connectedBone.GetComponent<Rigidbody>();
        joint.anchor = Vector3.zero;
        joint.enableProjection = true;

        // Twist axis runs along the limb (away from the parent); swing axis is
        // any direction perpendicular to it.
        Vector3 axis = (-bone.InverseTransformPoint(connectedBone.position)).normalized;
        if (axis.sqrMagnitude < 0.0001f)
        {
            axis = Vector3.right;
        }
        Vector3 swing = Vector3.Cross(axis, Vector3.up);
        if (swing.sqrMagnitude < 0.01f)
        {
            swing = Vector3.Cross(axis, Vector3.right);
        }
        joint.axis = axis;
        joint.swingAxis = swing.normalized;

        joint.lowTwistLimit = new SoftJointLimit { limit = -20f };
        joint.highTwistLimit = new SoftJointLimit { limit = 20f };
        joint.swing1Limit = new SoftJointLimit { limit = 35f };
        joint.swing2Limit = new SoftJointLimit { limit = 35f };
    }

    static int DominantAxis(Vector3 v)
    {
        Vector3 a = new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
        if (a.x >= a.y && a.x >= a.z) return 0;
        if (a.y >= a.z) return 1;
        return 2;
    }
}
