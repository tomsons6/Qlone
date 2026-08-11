using System.Collections;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// A button or switch the player looks at and presses (driven by <see cref="Interactor"/>).
/// As a <see cref="DoorCondition"/> it can feed a <see cref="DoorLock"/>, and it also exposes a
/// UnityEvent for one-off effects (sound, VFX).
///
/// Two behaviours:
/// <list type="bullet">
/// <item><b>Latching</b> (a switch): each interact toggles <see cref="DoorCondition.IsMet"/>.</item>
/// <item><b>Momentary</b> (a button): each interact pulses it met for <see cref="m_MomentaryPulse"/>
/// seconds, then clears it.</item>
/// </list>
/// Combine a momentary button with <see cref="DoorLock"/>'s latch to open a door for good on a
/// single press.
/// </summary>
public class InteractButton : DoorCondition, IInteractable
{
    [Tooltip("Latching = a switch (each press toggles). Off = a momentary button (each press pulses).")]
    [SerializeField]
    bool m_Latching = true;
    [Tooltip("How long a momentary press stays met, in seconds.")]
    [SerializeField]
    float m_MomentaryPulse = 0.1f;
    [Tooltip("Fired on every interact, for one-off effects (sound, VFX).")]
    [SerializeField]
    UnityEvent m_OnPressed;

    Coroutine m_PulseRoutine;

    /// <summary>Called by <see cref="Interactor"/> when the player looks at this and presses the
    /// interact key. Latching flips the met state; momentary pulses it true, then false.</summary>
    public void Interact()
    {
        m_OnPressed?.Invoke();

        if (m_Latching)
        {
            SetMet(!IsMet);
            return;
        }

        if (m_PulseRoutine != null)
        {
            StopCoroutine(m_PulseRoutine);
            m_PulseRoutine = null;
        }
        if (isActiveAndEnabled)
        {
            m_PulseRoutine = StartCoroutine(Pulse());
        }
        else
        {
            // Can't run a coroutine on an inactive object; a latch on the lock still catches this.
            SetMet(true);
            SetMet(false);
        }
    }

    IEnumerator Pulse()
    {
        SetMet(true);
        yield return new WaitForSeconds(m_MomentaryPulse);
        SetMet(false);
        m_PulseRoutine = null;
    }
}
