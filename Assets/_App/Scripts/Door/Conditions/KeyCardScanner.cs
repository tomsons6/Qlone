using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A keycard reader. Satisfied while an accepted card is BOTH inside this scanner's trigger
/// AND currently being held (by the player or the clone) -- so a card must be carried into the
/// slot and kept in hand, not just dropped in. Two scanners under a <see cref="DoorLock"/> in
/// <see cref="DoorLock.Mode.All"/> require both cards held at once (one by the player, one by
/// the clone): the game's core clone puzzle.
///
/// Requires a trigger collider on this GameObject, sized to the card slot.
/// </summary>
public class KeyCardScanner : DoorCondition
{
    [SerializeField]
    string m_AcceptedTag = "KeyCard";

    [SerializeField]
    private Renderer ledRenderer;
    [SerializeField]
    private int ledMaterialIndex = 0;

    // Property IDs for the LED colors we drive at runtime.
    static readonly int k_BaseColor = Shader.PropertyToID("_BaseColor");
    static readonly int k_Color = Shader.PropertyToID("_Color");
    static readonly int k_EmissionColor = Shader.PropertyToID("_EmissionColor");

    // Per-renderer material instance for the LED slot. Resolved lazily so recoloring this scanner
    // affects only its own LED -- not the shared .mat asset and not the other scanners.
    // (A MaterialPropertyBlock does NOT work here: the URP SRP Batcher ignores per-renderer overrides
    // of _BaseColor/_EmissionColor because they live in the UnityPerMaterial constant buffer.)
    Material m_LedInstance;

    // Cards inside the trigger, tracked by Rigidbody so a card's multiple colliders dedupe to a
    // single occupant and a card that is dragged each FixedUpdate keeps counting as inside.
    readonly HashSet<Rigidbody> m_Occupants = new HashSet<Rigidbody>();
    GrabScript m_Grabber; // the single grabber (player root, tag "Main"), resolved lazily

    void OnTriggerEnter(Collider other)
    {
        Rigidbody rb = other.attachedRigidbody;
        if (rb != null && (string.IsNullOrEmpty(m_AcceptedTag) || rb.CompareTag(m_AcceptedTag)))
        {
            Debug.Log("Added ocupant");
            m_Occupants.Add(rb);
        }
    }

    void OnTriggerExit(Collider other)
    {
        Rigidbody rb = other.attachedRigidbody;
        if (rb != null)
        {
            m_Occupants.Remove(rb);
        }
    }

    // Evaluate "inside AND held" each frame rather than on trigger-stay: a held card is moved by
    // the grab system every FixedUpdate so it reliably stays inside the trigger, and this is the
    // reliable moment to test whether any card in the slot is actually being carried.
    void Update()
    {
        if (m_Grabber == null)
        {
            GameObject main = GameObject.FindGameObjectWithTag("Main");
            if (main != null)
            {
                m_Grabber = main.GetComponent<GrabScript>();
            }
        }

        // Drop cards that were destroyed while inside (the reference goes null).
        m_Occupants.RemoveWhere(rb => rb == null);

        bool held = false;
        if (m_Grabber != null)
        {
            foreach (Rigidbody rb in m_Occupants)
            {
                if (m_Grabber.IsHeldRigidbody(rb))
                {
                    held = true;
                    break;
                }
            }
        }
        ToggleLed(held);
        SetMet(held);
    }

    private void ToggleLed(bool toggle)
    {
        if (ledRenderer == null)
        {
            return;
        }

        if (m_LedInstance == null)
        {
            // renderer.materials returns per-renderer INSTANCES (and assigns them back to this
            // renderer), so from here on we edit only this scanner's copy of the LED material.
            Material[] mats = ledRenderer.materials;
            if (ledMaterialIndex < 0 || ledMaterialIndex >= mats.Length)
            {
                return;
            }
            m_LedInstance = mats[ledMaterialIndex];
            m_LedInstance.EnableKeyword("_EMISSION"); // make sure the emission pass is active
        }

        Color color = toggle ? Color.green : Color.red;
        m_LedInstance.SetColor(k_BaseColor, color);   // URP Lit
        m_LedInstance.SetColor(k_Color, color);       // Built-in / Standard fallback
        m_LedInstance.SetColor(k_EmissionColor, color);
    }
}
