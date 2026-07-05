using System.Collections;
using UnityEngine;

/// <summary>
/// Aggregates one or more <see cref="DoorCondition"/>s and drives a <see cref="Door"/> when
/// enough of them are met. This is the reusable "what opens this door" glue:
/// <list type="bullet">
/// <item>two keycard scanners under an <see cref="Mode.All"/> lock = the two-card puzzle
/// (one card held by the player, one by the clone, at the same time);</item>
/// <item>two pressure plates under an <see cref="Mode.All"/> lock = "two buttons at once";</item>
/// <item>a single button/switch condition = a simple interact door.</item>
/// </list>
///
/// The lock subscribes to each condition's <see cref="DoorCondition.OnChanged"/> event and
/// re-evaluates only on a change, so there's no per-frame polling.
/// </summary>
public class DoorLock : MonoBehaviour
{
    /// <summary>How many conditions must be met for the lock to open the door.</summary>
    public enum Mode
    {
        All,      // every condition must be met
        Any,      // at least one
        AtLeast,  // at least m_AtLeast
    }

    [Tooltip("Door to drive. Auto-resolved from this object's parents if left empty.")]
    [SerializeField]
    Door m_Door;
    [Tooltip("Conditions to aggregate. Auto-collected from this object's children if left empty.")]
    [SerializeField]
    DoorCondition[] m_Conditions;
    [Tooltip("How many conditions must be met to open the door.")]
    [SerializeField]
    Mode m_Mode = Mode.All;
    [Tooltip("Threshold used when Mode is AtLeast.")]
    [SerializeField]
    int m_AtLeast = 1;
    [Tooltip("Once opened, stay open even if the conditions later stop being met.")]
    [SerializeField]
    bool m_Latch;
    [Tooltip("When not latched and the conditions drop below the threshold, close the door.")]
    [SerializeField]
    bool m_AutoClose = true;
    [Tooltip("Seconds to wait before an auto-close (0 = immediate).")]
    [SerializeField]
    float m_CloseDelay;

    bool m_Latched;
    Coroutine m_CloseRoutine;

    void OnEnable()
    {
        if (m_Door == null)
        {
            m_Door = GetComponentInParent<Door>();
        }
        if (m_Conditions == null || m_Conditions.Length == 0)
        {
            m_Conditions = GetComponentsInChildren<DoorCondition>();
        }

        foreach (DoorCondition c in m_Conditions)
        {
            if (c != null)
            {
                c.OnChanged += OnConditionChanged;
            }
        }
        Evaluate();
    }

    void OnDisable()
    {
        if (m_Conditions == null)
        {
            return;
        }
        foreach (DoorCondition c in m_Conditions)
        {
            if (c != null)
            {
                c.OnChanged -= OnConditionChanged;
            }
        }
        CancelPendingClose();
    }

    void OnConditionChanged(DoorCondition _) => Evaluate();

    void Evaluate()
    {
        if (m_Door == null)
        {
            return;
        }

        if (IsSatisfied())
        {
            CancelPendingClose();
            if (m_Latch)
            {
                m_Latched = true;
            }
            m_Door.Open();
            return;
        }

        // Not satisfied. A latched-open door stays open, and auto-close can be disabled entirely.
        if (m_Latched || !m_AutoClose)
        {
            return;
        }

        if (m_CloseDelay > 0f && isActiveAndEnabled)
        {
            if (m_CloseRoutine == null)
            {
                m_CloseRoutine = StartCoroutine(CloseAfter(m_CloseDelay));
            }
        }
        else
        {
            m_Door.Close();
        }
    }

    // Count met conditions (skipping null slots) and apply the mode.
    bool IsSatisfied()
    {
        int met = 0;
        int total = 0;
        foreach (DoorCondition c in m_Conditions)
        {
            if (c == null)
            {
                continue;
            }
            total++;
            if (c.IsMet)
            {
                met++;
            }
        }

        switch (m_Mode)
        {
            case Mode.Any:
                return met >= 1;
            case Mode.AtLeast:
                return met >= m_AtLeast;
            default: // All
                return total > 0 && met >= total;
        }
    }

    IEnumerator CloseAfter(float delay)
    {
        yield return new WaitForSeconds(delay);
        m_CloseRoutine = null;
        // A condition may have been re-met during the delay -- only close if we're still
        // unsatisfied and haven't latched open in the meantime.
        if (!m_Latched && !IsSatisfied())
        {
            m_Door.Close();
        }
    }

    void CancelPendingClose()
    {
        if (m_CloseRoutine != null)
        {
            StopCoroutine(m_CloseRoutine);
            m_CloseRoutine = null;
        }
    }
}
