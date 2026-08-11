using UnityEngine;

/// <summary>
/// The shared state behind every <see cref="CloneButtonPanel"/>: one pair of slots (Left and
/// Right) that the two bodies claim between them. The panels are only displays -- they all show
/// these same two slots, so a body pressing the left cap at one panel lights the left cap on
/// every panel, and its partner can see what has already been claimed from across the room.
///
/// The puzzle it encodes: the first body to press takes <b>Left</b>, the second takes
/// <b>Right</b>, and (with <see cref="m_RequireDifferentPanels"/>) they must press at
/// <i>different</i> panels -- so left is held at one panel and right at the other. Only then do
/// the middle indicators light and this condition report met, which is what a
/// <see cref="DoorLock"/> turns into an open door.
///
/// A body cannot claim both slots: pressing again while already holding one lets it go. Walking
/// away from the panel it pressed at (or dismissing a clone that was holding a slot) releases the
/// slot too, so both bodies have to stand at their panels at the same time.
///
/// Put this on any always-active GameObject and wire it into the door's <see cref="DoorLock"/>
/// the way the keycard scanners are wired -- by explicit reference, not by parenting.
/// </summary>
public class CloneButtonLink : DoorCondition
{
    // One claimable slot. Identity is the pressing body's CAMERA transform -- the same token
    // GrabScript.IsHolding keys on -- so a slot stays with the body that pressed it after
    // control switches to the other body.
    class Slot
    {
        public Transform Presser;
        public CloneButtonPanel Panel;
        public CloneButtonSide Side;
        public GrabIK ArmIK;
        public BodyGrabIK BodyIK;
    }

    [Tooltip("Panels showing this state. Auto-collected from the scene if left empty.")]
    [SerializeField]
    CloneButtonPanel[] m_Panels;
    [Tooltip("How far the pressing body may drift from its panel before the cap pops back out and its hand lets go. This is ALSO the maximum distance a press can be made from, so the two ranges always agree -- you have to walk up to the panel, and stepping back off it releases.")]
    [SerializeField]
    float m_ReleaseDistance = 1.5f;
    [Tooltip("Require the two slots to be claimed at different panels, so the bodies must split up. Off = both can press the same panel.")]
    [SerializeField]
    bool m_RequireDifferentPanels = true;
    [Tooltip("Once both slots have been held at the same time, keep the middle indicators lit for good -- matching the door's latch, so the room stays visibly 'solved' after the bodies walk away.")]
    [SerializeField]
    bool m_LatchIndicator = true;

    // Index matches CloneButtonSide: 0 = Left, 1 = Right. Left first means the first body to
    // press always takes the left cap.
    readonly Slot[] m_Slots = { new Slot(), new Slot() };
    bool m_IndicatorLatched; // both slots were held together at least once

    void OnEnable()
    {
        if (m_Panels == null || m_Panels.Length == 0)
        {
            m_Panels = FindObjectsByType<CloneButtonPanel>(FindObjectsSortMode.None);
        }
        Commit();
    }

    void OnDisable()
    {
        // Don't leave a body's arm stuck out at a panel we're no longer driving.
        foreach (Slot slot in m_Slots)
        {
            Release(slot);
        }
    }

    /// <summary>Press from the body currently in control, at the given panel. Called by
    /// <see cref="CloneButtonPanel.Interact"/>.</summary>
    public void PressFrom(CloneButtonPanel panel)
    {
        Camera cam = Camera.main;
        if (cam == null || panel == null)
        {
            return;
        }
        Transform bodyCam = cam.transform;

        // This body already holds a slot -- pressing again lets it go. Checking this first is
        // also what stops one body from claiming both slots and solving the puzzle alone.
        foreach (Slot held in m_Slots)
        {
            if (held.Presser == bodyCam)
            {
                Release(held);
                Commit();
                return;
            }
        }

        // Must be standing at the panel. Using the same distance the walk-away check uses means a
        // press can never be made from somewhere that would release it again on the next frame.
        if (Vector3.Distance(bodyCam.position, panel.Anchor) > m_ReleaseDistance)
        {
            return;
        }

        // Otherwise take the first free slot, Left before Right.
        for (int i = 0; i < m_Slots.Length; i++)
        {
            Slot slot = m_Slots[i];
            if (slot.Presser != null)
            {
                continue;
            }
            if (m_RequireDifferentPanels && OtherSlotIsAt(i, panel))
            {
                continue;
            }
            Press(slot, (CloneButtonSide)i, bodyCam, panel);
            Commit();
            return;
        }
    }

    // Is a slot other than 'index' already claimed at this panel?
    bool OtherSlotIsAt(int index, CloneButtonPanel panel)
    {
        for (int i = 0; i < m_Slots.Length; i++)
        {
            if (i != index && m_Slots[i].Presser != null && m_Slots[i].Panel == panel)
            {
                return true;
            }
        }
        return false;
    }

    void Press(Slot slot, CloneButtonSide side, Transform bodyCam, CloneButtonPanel panel)
    {
        slot.Presser = bodyCam;
        slot.Panel = panel;
        slot.Side = side;

        // The body's IK components hang off the body root: GrabIK on the root itself, BodyGrabIK
        // on the humanoid model child. Walk UP from the camera to find GrabIK rather than assuming
        // the camera is a direct child of the root, then search down from there for BodyGrabIK.
        slot.ArmIK = bodyCam.GetComponentInParent<GrabIK>();
        Transform bodyRoot = slot.ArmIK != null
            ? slot.ArmIK.transform
            : (bodyCam.parent != null ? bodyCam.parent : bodyCam);
        slot.BodyIK = bodyRoot.GetComponentInChildren<BodyGrabIK>(true);
        AimHand(slot);
    }

    void Release(Slot slot)
    {
        if (slot.ArmIK != null)
        {
            slot.ArmIK.ClearReachTarget();
        }
        if (slot.BodyIK != null)
        {
            slot.BodyIK.ClearReachTarget();
        }
        slot.Presser = null;
        slot.Panel = null;
        slot.ArmIK = null;
        slot.BodyIK = null;
    }

    // Point the presser's first-person and third-person hands at the cap it is holding down. Only
    // the panel the body actually pressed at gets a hand -- the mirrored caps are display only.
    void AimHand(Slot slot)
    {
        if (slot.Panel == null)
        {
            return;
        }
        Vector3 anchor = slot.Panel.GetCapAnchor(slot.Side);
        if (slot.ArmIK != null)
        {
            slot.ArmIK.SetReachTarget(anchor);
        }
        if (slot.BodyIK != null)
        {
            slot.BodyIK.SetReachTarget(anchor);
        }
    }

    void Update()
    {
        bool changed = false;
        foreach (Slot slot in m_Slots)
        {
            if (slot.Presser == null)
            {
                continue;
            }

            // Presser == null also catches a dismissed clone: Cloning.DestroyClone destroys the
            // clone's Camera GameObject, so the stored transform goes null (the same safety net
            // GrabScript keeps for a hold whose HoldCam vanished).
            if (slot.Panel == null || Vector3.Distance(slot.Presser.position, slot.Panel.Anchor) > m_ReleaseDistance)
            {
                Release(slot);
                changed = true;
                continue;
            }

            // The cap sinks in over m_PressDuration, so re-aim each frame to keep the hand on it.
            AimHand(slot);
        }

        if (changed)
        {
            Commit();
        }
    }

    // Push the current state out to every panel and to the door lock. SetMet only fires
    // OnChanged on a real transition, so the lock re-evaluates exactly once per change.
    void Commit()
    {
        bool left = m_Slots[0].Presser != null;
        bool right = m_Slots[1].Presser != null;
        bool both = left && right;

        // The indicators latch on the first time both slots are held together, so the panels keep
        // reading "solved" after the bodies leave -- the door they opened is latched too.
        if (both && m_LatchIndicator)
        {
            m_IndicatorLatched = true;
        }
        bool indicatorLit = both || m_IndicatorLatched;

        if (m_Panels != null)
        {
            foreach (CloneButtonPanel panel in m_Panels)
            {
                if (panel != null)
                {
                    panel.SetState(left, right, indicatorLit);
                }
            }
        }

        SetMet(both);
    }
}
