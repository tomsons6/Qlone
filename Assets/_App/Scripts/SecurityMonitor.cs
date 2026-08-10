using UnityEngine;

/// <summary>
/// Displays a surveillance RenderTexture on this monitor's screen.
///
/// Every monitor in the level shares a single <c>Screen.mat</c> (slot 2 of the Monitor mesh:
/// StandColor / MonitorFrame / Screen), so a RenderTexture can't be dropped straight onto the
/// material -- that would light up every screen with the same feed. Instead this pushes the
/// texture through a per-renderer <see cref="MaterialPropertyBlock"/>, which overrides only THIS
/// renderer's screen slot and leaves the shared asset (and every other monitor) untouched.
///
/// Textures live OUTSIDE the UnityPerMaterial constant buffer, so -- unlike the LED color in
/// <see cref="KeyCardScanner"/>, which needs a full material instance because the URP SRP Batcher
/// ignores per-renderer color overrides -- a texture override survives the SRP Batcher and a
/// property block is the correct, cheap tool here.
///
/// Put this on the Monitor GameObject and assign the RenderTexture that its security camera
/// renders into (set that same RT as the camera's Target Texture on the Camera component).
/// </summary>
[RequireComponent(typeof(Renderer))]
public class SecurityMonitor : MonoBehaviour
{
    [SerializeField]
    [Tooltip("The feed to show. Assign the same RenderTexture the security camera renders into (its Target Texture).")]
    RenderTexture m_RenderTexture;

    [SerializeField]
    [Tooltip("Renderer carrying the screen material. Defaults to this GameObject's Renderer.")]
    Renderer m_ScreenRenderer;
    [SerializeField]
    [Tooltip("Material/submesh slot of the screen. On the Monitor mesh the screen is slot 2 (StandColor, MonitorFrame, Screen).")]
    int m_ScreenMaterialIndex = 2;

    [SerializeField]
    [Tooltip("Also drive the emission map so the screen glows with the feed. Screen.mat has emission enabled, so leave this on.")]
    bool m_DriveEmission = true;

    // Property IDs for the URP/Lit slots we override. Textures aren't in UnityPerMaterial, so
    // overriding them via a property block is safe (see class summary).
    static readonly int k_BaseMap = Shader.PropertyToID("_BaseMap");
    static readonly int k_EmissionMap = Shader.PropertyToID("_EmissionMap");

    MaterialPropertyBlock m_Block;

    void Awake()
    {
        if (m_ScreenRenderer == null)
        {
            m_ScreenRenderer = GetComponent<Renderer>();
        }

        ApplyFeed();
    }

    /// <summary>
    /// Push the feed onto ONLY this renderer's screen slot. Public so it can be re-applied if the
    /// feed is swapped at runtime. The single-argument property-block overloads would tint the
    /// whole renderer; the materialIndex overloads scope it to slot 2, leaving the stand and frame
    /// slots alone.
    /// </summary>
    public void ApplyFeed()
    {
        if (m_ScreenRenderer == null || m_RenderTexture == null)
        {
            return;
        }

        if (m_Block == null)
        {
            m_Block = new MaterialPropertyBlock();
        }

        m_ScreenRenderer.GetPropertyBlock(m_Block, m_ScreenMaterialIndex);
        m_Block.SetTexture(k_BaseMap, m_RenderTexture);
        if (m_DriveEmission)
        {
            m_Block.SetTexture(k_EmissionMap, m_RenderTexture);
        }
        m_ScreenRenderer.SetPropertyBlock(m_Block, m_ScreenMaterialIndex);
    }
}
