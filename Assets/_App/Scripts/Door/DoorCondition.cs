using System;
using UnityEngine;

/// <summary>
/// Abstract base for anything that can satisfy part of a door's opening requirement:
/// a keycard in a scanner, a body on a pressure plate, a pressed button/switch. A
/// <see cref="DoorLock"/> aggregates one or more conditions and opens its <see cref="Door"/>
/// when enough of them are met.
///
/// Subclasses decide WHEN they are satisfied and call <see cref="SetMet"/>; the lock only
/// reads <see cref="IsMet"/> and listens to <see cref="OnChanged"/>, so new condition types
/// plug in without touching <see cref="DoorLock"/> or <see cref="Door"/>.
/// </summary>
public abstract class DoorCondition : MonoBehaviour
{
    bool m_Met;

    /// <summary>Whether this condition is currently satisfied.</summary>
    public bool IsMet => m_Met;

    /// <summary>Raised only when <see cref="IsMet"/> actually changes, so a
    /// <see cref="DoorLock"/> can re-evaluate on demand instead of polling every frame.</summary>
    public event Action<DoorCondition> OnChanged;

    /// <summary>Set the met state. Subclasses call this; it fires <see cref="OnChanged"/>
    /// only on an actual transition, so listeners aren't spammed when nothing changed.</summary>
    protected void SetMet(bool met)
    {
        if (m_Met == met)
        {
            return;
        }
        m_Met = met;
        OnChanged?.Invoke(this);
    }
}
