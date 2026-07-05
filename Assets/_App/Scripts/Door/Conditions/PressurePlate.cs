using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A pressure plate: satisfied while at least one qualifying body is standing on it. Entrants
/// are filtered by layer (set to Player for the player/clone bodies) and, optionally, by tag.
/// Two plates under a <see cref="DoorLock"/> in <see cref="DoorLock.Mode.All"/> make a
/// "two buttons pressed at once" door (player + clone).
///
/// Requires a trigger collider on this GameObject, sized to the plate's footprint.
/// </summary>
public class PressurePlate : DoorCondition
{
    // The player/clone bodies live on this layer (see the tag/layer contract in CLAUDE.md).
    const int PlayerLayer = 10;

    [Tooltip("Layers that can trigger the plate. Defaults to Player (the player/clone bodies).")]
    [SerializeField]
    LayerMask m_Mask = 1 << PlayerLayer;
    [Tooltip("Optional tag a body must also have. Leave empty to accept any body on the mask.")]
    [SerializeField]
    string m_RequiredTag = "";

    // Distinct colliders currently on the plate. Unity fires OnTriggerExit when a collider is
    // disabled or destroyed while overlapping, so this stays accurate as bodies come and go.
    readonly HashSet<Collider> m_Occupants = new HashSet<Collider>();

    void OnTriggerEnter(Collider other)
    {
        if (Qualifies(other) && m_Occupants.Add(other))
        {
            SetMet(m_Occupants.Count > 0);
        }
    }

    void OnTriggerExit(Collider other)
    {
        if (m_Occupants.Remove(other))
        {
            SetMet(m_Occupants.Count > 0);
        }
    }

    bool Qualifies(Collider other)
    {
        if ((m_Mask.value & (1 << other.gameObject.layer)) == 0)
        {
            return false;
        }
        return string.IsNullOrEmpty(m_RequiredTag) || other.CompareTag(m_RequiredTag);
    }
}
