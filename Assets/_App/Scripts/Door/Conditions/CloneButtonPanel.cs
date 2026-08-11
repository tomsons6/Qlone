using UnityEngine;

/// <summary>Which of a panel's two caps is meant. The order is the press order: the first body
/// to press always takes <see cref="Left"/>.</summary>
public enum CloneButtonSide
{
    Left = 0,
    Right = 1,
}

/// <summary>
/// One physical clone-button panel (the <c>CloneButtons</c> model: <c>Base</c>, <c>ButtonLeft</c>,
/// <c>ButtonRight</c>, <c>Indicator</c>). This is only the <i>view</i> plus the thing the player
/// looks at: the puzzle state lives in <see cref="CloneButtonLink"/>, which is shared by every
/// panel, so pressing the left cap at one panel lights the left cap on all of them.
///
/// Drop it on the model root and it wires itself -- the three renderers are found by child name
/// and the link is found in the scene. Assign the fields only to override that.
///
/// Needs a solid (non-trigger) collider anywhere on the panel: <see cref="Interactor"/> raycasts
/// with <c>QueryTriggerInteraction.Ignore</c> and resolves this component with
/// <c>GetComponentInParent</c>, so a collider on the root or on the caps both work.
/// </summary>
public class CloneButtonPanel : MonoBehaviour, IInteractable
{
    [Header("Renderers (auto-found by child name if empty)")]
    [Tooltip("The left cap's renderer. Its transform is the cap that sinks in. Defaults to the child named ButtonLeft.")]
    [SerializeField]
    Renderer m_LeftRenderer;
    [Tooltip("The right cap's renderer. Defaults to the child named ButtonRight.")]
    [SerializeField]
    Renderer m_RightRenderer;
    [Tooltip("The middle indicator's renderer. Lit only once BOTH slots are held. Defaults to the child named Indicator.")]
    [SerializeField]
    Renderer m_IndicatorRenderer;
    [Tooltip("Material slot to tint on each renderer above. The CloneButtons model has one material per part, so 0.")]
    [SerializeField]
    int m_MaterialIndex = 0;

    [Header("Press")]
    [Tooltip("How far a cap sinks when pressed, in the caps' parent space (they are siblings, so one offset serves both). Tune by nudging a cap in the Scene view.")]
    [SerializeField]
    Vector3 m_PressedLocalOffset = new Vector3(0f, -0.01f, 0f);
    [Tooltip("Seconds a cap takes to sink in or pop back out.")]
    [SerializeField]
    float m_PressDuration = 0.15f;

    [Header("Colours")]
    [SerializeField]
    Color m_IdleColor = Color.red;
    [SerializeField]
    Color m_LitColor = Color.green;
    [Tooltip("Emission is colour * this. URP emission is HDR, so raise it to make the lit state glow.")]
    [SerializeField]
    float m_EmissionIntensity = 1f;

    [Header("Link")]
    [Tooltip("Shared puzzle state driving every panel. Auto-found in the scene if left empty.")]
    [SerializeField]
    CloneButtonLink m_Link;

    // Property IDs for the colours we drive at runtime (same set KeyCardScanner uses).
    static readonly int k_BaseColor = Shader.PropertyToID("_BaseColor");
    static readonly int k_Color = Shader.PropertyToID("_Color");
    static readonly int k_EmissionColor = Shader.PropertyToID("_EmissionColor");

    // Runtime state for one cap. Not serialized -- the authored data is the renderer above.
    class Cap
    {
        public Renderer Renderer;
        public Transform Target;
        public Vector3 RestPosition;
        public float Blend;        // 0 = out, 1 = fully pressed
        public bool Pressed;       // the state Blend is heading to
        public Material TintInstance;
    }

    Cap m_Left;
    Cap m_Right;
    Material m_IndicatorInstance;
    bool m_IndicatorLit;

    void Awake()
    {
        if (m_LeftRenderer == null)
        {
            m_LeftRenderer = FindChildRenderer("ButtonLeft");
        }
        if (m_RightRenderer == null)
        {
            m_RightRenderer = FindChildRenderer("ButtonRight");
        }
        if (m_IndicatorRenderer == null)
        {
            m_IndicatorRenderer = FindChildRenderer("Indicator");
        }

        m_Left = MakeCap(m_LeftRenderer);
        m_Right = MakeCap(m_RightRenderer);
    }

    void OnEnable()
    {
        if (m_Link == null)
        {
            m_Link = FindFirstObjectByType<CloneButtonLink>();
        }
        // Paint the idle state up front so a panel never starts showing a stale lit colour.
        Tint(m_Left, false);
        Tint(m_Right, false);
        TintIndicator(false);
    }

    Renderer FindChildRenderer(string childName)
    {
        foreach (Renderer r in GetComponentsInChildren<Renderer>(true))
        {
            if (r.name == childName)
            {
                return r;
            }
        }
        return null;
    }

    // Capture the cap's rest pose now so the press offset works from wherever the model sits.
    static Cap MakeCap(Renderer renderer)
    {
        var cap = new Cap { Renderer = renderer };
        if (renderer != null)
        {
            cap.Target = renderer.transform;
            cap.RestPosition = cap.Target.localPosition;
        }
        return cap;
    }

    /// <summary>Called by <see cref="Interactor"/> when a body looks at this panel and presses the
    /// interact key. The link decides which slot the acting body gets.</summary>
    public void Interact()
    {
        if (m_Link != null)
        {
            m_Link.PressFrom(this);
        }
    }

    /// <summary>Show the shared puzzle state. Every panel is given the same values, which is what
    /// makes a press at one panel appear on the other.</summary>
    public void SetState(bool leftHeld, bool rightHeld, bool indicatorLit)
    {
        SetCap(m_Left, leftHeld);
        SetCap(m_Right, rightHeld);

        if (m_IndicatorLit != indicatorLit)
        {
            m_IndicatorLit = indicatorLit;
            TintIndicator(indicatorLit);
        }
    }

    void SetCap(Cap cap, bool pressed)
    {
        if (cap == null || cap.Pressed == pressed)
        {
            return;
        }
        cap.Pressed = pressed;
        Tint(cap, pressed);
    }

    /// <summary>World point the presser's hand should reach for. The model's four children all
    /// pivot at the panel origin, so a cap's <i>transform</i> is useless as an anchor -- use the
    /// renderer's world bounds centre, which sits on the actual cap and follows it as it sinks.</summary>
    public Vector3 GetCapAnchor(CloneButtonSide side)
    {
        Cap cap = side == CloneButtonSide.Left ? m_Left : m_Right;
        if (cap != null && cap.Renderer != null)
        {
            return cap.Renderer.bounds.center;
        }
        return transform.position;
    }

    /// <summary>Where the panel "is" for the walk-away check -- the visible middle of the panel
    /// rather than the model origin, which the mesh pivots leave off to one side.</summary>
    public Vector3 Anchor => m_IndicatorRenderer != null ? m_IndicatorRenderer.bounds.center : transform.position;

    void Update()
    {
        AnimateCap(m_Left);
        AnimateCap(m_Right);
    }

    // Ease the cap between its rest pose and the pressed offset, matching the SmoothStep feel of
    // Door.Animate. Runs on every panel, so a mirrored press animates the same as a real one.
    void AnimateCap(Cap cap)
    {
        if (cap == null || cap.Target == null)
        {
            return;
        }
        float target = cap.Pressed ? 1f : 0f;
        float step = m_PressDuration > 0f ? Time.deltaTime / m_PressDuration : 1f;
        cap.Blend = Mathf.MoveTowards(cap.Blend, target, step);
        cap.Target.localPosition = Vector3.Lerp(
            cap.RestPosition,
            cap.RestPosition + m_PressedLocalOffset,
            Mathf.SmoothStep(0f, 1f, cap.Blend));
    }

    void Tint(Cap cap, bool lit)
    {
        if (cap == null)
        {
            return;
        }
        Apply(ref cap.TintInstance, cap.Renderer, lit);
    }

    void TintIndicator(bool lit)
    {
        Apply(ref m_IndicatorInstance, m_IndicatorRenderer, lit);
    }

    // Recolour one renderer slot. Mirrors KeyCardScanner.ToggleLed: renderer.materials returns
    // per-renderer INSTANCES, so this tints only this panel's copy -- not the shared .mat asset
    // and not the other panel. A MaterialPropertyBlock does NOT work here: URP's SRP Batcher
    // ignores per-renderer overrides of _BaseColor/_EmissionColor (they live in the
    // UnityPerMaterial CBUFFER).
    void Apply(ref Material instance, Renderer renderer, bool lit)
    {
        if (renderer == null)
        {
            return;
        }
        if (instance == null)
        {
            Material[] mats = renderer.materials;
            if (m_MaterialIndex < 0 || m_MaterialIndex >= mats.Length)
            {
                return;
            }
            instance = mats[m_MaterialIndex];
            instance.EnableKeyword("_EMISSION"); // make sure the emission pass is active
        }

        Color color = lit ? m_LitColor : m_IdleColor;
        instance.SetColor(k_BaseColor, color);   // URP Lit
        instance.SetColor(k_Color, color);       // Built-in / Standard fallback
        instance.SetColor(k_EmissionColor, color * m_EmissionIntensity);
    }
}
