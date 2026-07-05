using System.Collections;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// Reusable door actuator. Moves one or more <see cref="DoorPart"/>s between a captured
/// "closed" pose and an open pose defined by per-part local position/rotation offsets, using
/// the same SmoothStep tween as the split-screen camera animation (<see cref="Cloning"/>).
/// There are no Animator clips -- the motion is procedural, so any transform can be a door
/// part just by giving it an offset:
/// <list type="bullet">
/// <item>a sliding double door = two parts with opposite X offsets;</item>
/// <item>a hinged door = one part with a rotation offset.</item>
/// </list>
///
/// Authors place the door in its CLOSED pose in the editor; <see cref="Awake"/> captures that
/// pose so the open pose is always relative to wherever the door was placed. Drive it directly
/// (<see cref="Open"/>/<see cref="Close"/>/<see cref="Toggle"/>) or wire a <see cref="DoorLock"/>.
/// </summary>
public class Door : MonoBehaviour
{
    /// <summary>One moving piece of the door and where it travels to when open.</summary>
    [System.Serializable]
    public class DoorPart
    {
        [Tooltip("The transform that moves. Required.")]
        public Transform Target;
        [Tooltip("Local-space position offset added to the closed pose when open.")]
        public Vector3 LocalPositionOffset;
        [Tooltip("Local-space rotation (euler) offset applied on top of the closed pose when open.")]
        public Vector3 LocalRotationEulerOffset;

        // Closed pose, captured at Awake so the door works from wherever it was authored.
        [HideInInspector] public Vector3 ClosedPosition;
        [HideInInspector] public Quaternion ClosedRotation;
    }

    [Tooltip("The moving pieces. A sliding double door is two parts with opposite X offsets; a hinged door is one part with a rotation offset.")]
    [SerializeField]
    DoorPart[] m_Parts;
    [Tooltip("Seconds the open/close tween takes.")]
    [SerializeField]
    float m_OpenDuration = 0.6f;
    [Tooltip("Start the door already open.")]
    [SerializeField]
    bool m_StartOpen;

    [Header("Events")]
    [Tooltip("Fired once the door has finished opening.")]
    [SerializeField]
    UnityEvent m_OnOpened;
    [Tooltip("Fired once the door has finished closing.")]
    [SerializeField]
    UnityEvent m_OnClosed;

    bool m_IsOpen;
    Coroutine m_Routine;

    /// <summary>True while the door is open or opening (its intended state), not only once
    /// the tween has finished.</summary>
    public bool IsOpen => m_IsOpen;

    void Awake()
    {
        // Capture each part's closed pose, then snap to the requested start state without a tween.
        if (m_Parts != null)
        {
            foreach (DoorPart part in m_Parts)
            {
                if (part.Target == null)
                {
                    continue;
                }
                part.ClosedPosition = part.Target.localPosition;
                part.ClosedRotation = part.Target.localRotation;
            }
        }
        m_IsOpen = m_StartOpen;
        ApplyImmediate(m_StartOpen);
    }

    public void Open() => SetOpen(true);
    public void Close() => SetOpen(false);
    public void Toggle() => SetOpen(!m_IsOpen);

    /// <summary>Open or close the door, tweening each part. Calling it again with the state the
    /// door is already heading to is a no-op (so a lock re-evaluating while the door is already
    /// open won't restart the tween or re-fire events); calling it with the opposite state
    /// reverses smoothly from the current pose.</summary>
    public void SetOpen(bool open)
    {
        if (open == m_IsOpen)
        {
            return;
        }
        m_IsOpen = open;

        if (m_Routine != null)
        {
            StopCoroutine(m_Routine);
            m_Routine = null;
        }

        // A coroutine can't run on an inactive object -- snap and fire the event instead.
        if (!isActiveAndEnabled)
        {
            ApplyImmediate(open);
            (open ? m_OnOpened : m_OnClosed)?.Invoke();
            return;
        }
        m_Routine = StartCoroutine(Animate(open));
    }

    // Mirrors Cloning.LerpRect: snapshot the pose each part starts from and the pose it's headed
    // to, then SmoothStep between the fixed endpoints so a reversed tween eases out of wherever
    // the part currently sits.
    IEnumerator Animate(bool open)
    {
        int n = m_Parts != null ? m_Parts.Length : 0;
        var fromPos = new Vector3[n];
        var fromRot = new Quaternion[n];
        var toPos = new Vector3[n];
        var toRot = new Quaternion[n];
        for (int i = 0; i < n; i++)
        {
            DoorPart p = m_Parts[i];
            if (p.Target == null)
            {
                continue;
            }
            fromPos[i] = p.Target.localPosition;
            fromRot[i] = p.Target.localRotation;
            toPos[i] = open ? p.ClosedPosition + p.LocalPositionOffset : p.ClosedPosition;
            toRot[i] = open ? p.ClosedRotation * Quaternion.Euler(p.LocalRotationEulerOffset) : p.ClosedRotation;
        }

        float elapsed = 0f;
        while (elapsed < m_OpenDuration)
        {
            elapsed += Time.deltaTime;
            float t = m_OpenDuration > 0f ? Mathf.SmoothStep(0f, 1f, elapsed / m_OpenDuration) : 1f;
            for (int i = 0; i < n; i++)
            {
                DoorPart p = m_Parts[i];
                if (p.Target == null)
                {
                    continue;
                }
                p.Target.localPosition = Vector3.Lerp(fromPos[i], toPos[i], t);
                p.Target.localRotation = Quaternion.Slerp(fromRot[i], toRot[i], t);
            }
            yield return null;
        }

        ApplyImmediate(open);
        m_Routine = null;
        (open ? m_OnOpened : m_OnClosed)?.Invoke();
    }

    // Snap every part directly to the closed or open pose.
    void ApplyImmediate(bool open)
    {
        if (m_Parts == null)
        {
            return;
        }
        foreach (DoorPart p in m_Parts)
        {
            if (p.Target == null)
            {
                continue;
            }
            p.Target.localPosition = open ? p.ClosedPosition + p.LocalPositionOffset : p.ClosedPosition;
            p.Target.localRotation = open ? p.ClosedRotation * Quaternion.Euler(p.LocalRotationEulerOffset) : p.ClosedRotation;
        }
    }
}
