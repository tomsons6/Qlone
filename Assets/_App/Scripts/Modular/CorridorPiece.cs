using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Marks a modular corridor piece (corner, straight, T-cross, cross...) and records where its
/// open ends are. Each open end is a <see cref="Socket"/>: the centre of the opening plus the
/// direction it faces, stored in the piece root's LOCAL space so the data survives being
/// rotated, scaled and re-parented.
///
/// <para>Why sockets instead of just snapping transforms to a grid: the corridor meshes come out
/// of Blender with pivots that have nothing to do with their geometry -- <c>TCross</c>'s pivot
/// sits ~12 units away from its own mesh, <c>Corner</c>'s is offset by ~1.4 on two axes, and the
/// straights are 7.23 / 7.37 long rather than a round number. Aligning two pieces by their
/// transforms (or by their bounding boxes, which are asymmetric on the closed side of a corner)
/// therefore leaves gaps. Aligning one opening onto another cannot, because it matches the
/// geometry that has to meet.</para>
///
/// <para>Sockets are normally filled in by the editor tool (<c>Tools > Qlone > Corridor Snap</c>),
/// which finds the openings automatically from the mesh; they can also be nudged or added by hand
/// in the inspector. This component holds data only -- it has no runtime behaviour.</para>
/// </summary>
[DisallowMultipleComponent]
public class CorridorPiece : MonoBehaviour
{
    /// <summary>One open end of a piece, in the piece root's local space.</summary>
    [System.Serializable]
    public class Socket
    {
        [Tooltip("Centre of the opening, in the piece root's local space.")]
        public Vector3 LocalPosition;
        [Tooltip("Direction the opening faces (outward, away from the piece), in the piece root's local space.")]
        public Vector3 LocalDirection = Vector3.forward;
        [Tooltip("Approximate radius of the opening. Used for gizmo size and to warn when two mismatched openings are joined.")]
        public float Radius = 1f;
    }

    [Tooltip("The open ends of this piece. Bake them from the mesh with the Corridor Snap window, or author them by hand.")]
    [SerializeField]
    List<Socket> m_Sockets = new List<Socket>();

    /// <summary>The raw socket list (mutable -- the editor tool writes to it).</summary>
    public List<Socket> Sockets
    {
        get
        {
            if (m_Sockets == null) m_Sockets = new List<Socket>();
            return m_Sockets;
        }
    }

    public int SocketCount { get { return Sockets.Count; } }

    /// <summary>World-space centre of socket <paramref name="index"/>.</summary>
    public Vector3 GetSocketPosition(int index)
    {
        return transform.TransformPoint(Sockets[index].LocalPosition);
    }

    /// <summary>World-space outward direction of socket <paramref name="index"/>.</summary>
    public Vector3 GetSocketDirection(int index)
    {
        Vector3 d = transform.TransformDirection(Sockets[index].LocalDirection);
        return d.sqrMagnitude > 1e-10f ? d.normalized : transform.forward;
    }

    /// <summary>World-space radius of socket <paramref name="index"/>, accounting for the piece's scale.</summary>
    public float GetSocketRadius(int index)
    {
        Vector3 s = transform.lossyScale;
        float k = Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));
        return Sockets[index].Radius * k;
    }

    void OnDrawGizmosSelected()
    {
        for (int i = 0; i < SocketCount; i++)
        {
            Vector3 p = GetSocketPosition(i);
            Vector3 d = GetSocketDirection(i);
            float r = GetSocketRadius(i);
            Gizmos.color = new Color(0.2f, 1f, 0.6f, 0.9f);
            Gizmos.DrawWireSphere(p, r * 0.15f);
            Gizmos.DrawLine(p, p + d * Mathf.Max(r, 0.25f));
        }
    }
}
