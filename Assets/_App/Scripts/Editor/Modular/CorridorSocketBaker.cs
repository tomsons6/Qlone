using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Finds the open ends of a modular corridor mesh so they can be baked into
/// <see cref="CorridorPiece.Socket"/>s.
///
/// <para>How: a corridor piece is an open-ended tube, so every opening shows up in the mesh as a
/// loop of BOUNDARY edges -- edges used by exactly one triangle. Closed sides (the back of a
/// corner, the stem wall of a T) are capped, so they have no boundary. Walking the boundary edges
/// into loops therefore recovers the openings straight from the geometry, with no assumptions
/// about grid size, pivot placement or piece type.</para>
///
/// <para>Two details make it work on real Blender exports:</para>
/// <list type="bullet">
/// <item>Vertices are WELDED by position first. Split normals, UV seams and submesh boundaries
/// (these meshes have 5 material submeshes) duplicate vertices at identical positions, and every
/// such duplicate would otherwise read as a false boundary edge.</item>
/// <item>An opening with wall thickness produces TWO concentric loops (inner and outer rim) in the
/// same plane. Coplanar, co-centred loops are merged into a single socket.</item>
/// </list>
/// </summary>
public static class CorridorSocketBaker
{
    /// <summary>An opening found on a piece, in the piece root's local space.</summary>
    public struct Opening
    {
        public Vector3 Center;
        public Vector3 Direction;
        public float Radius;
        public float Area;
        public int RimCount;
    }

    /// <summary>Tuning for <see cref="FindOpenings"/>. The defaults suit the Qlone corridor kit.</summary>
    public class Settings
    {
        [Tooltip("Reject openings whose facing is more vertical than this (|dot| with world up). Corridor ends face sideways; floor and ceiling detail does not.")]
        public float MaxVerticalDot = 0.35f;
        [Tooltip("Reject loops smaller than this fraction of the largest loop found. Filters out small detail holes (bolts, vents, wire cutouts).")]
        public float MinRelativeArea = 0.20f;
        [Tooltip("Reject loops that are not flat, as a fraction of their own radius. A real opening rim is planar.")]
        public float MaxPlanarity = 0.30f;
        [Tooltip("Weld distance for duplicate vertices, as a fraction of the mesh overall size.")]
        public float WeldEpsilonFraction = 0.0005f;
    }

    /// <summary>
    /// Finds the openings of <paramref name="root"/>, returned in the local space of
    /// <paramref name="root"/>. Works on a prefab asset root or a scene instance.
    /// <paramref name="log"/>, if given, receives a readable account of what was found and rejected.
    /// </summary>
    public static List<Opening> FindOpenings(GameObject root, Settings settings, System.Text.StringBuilder log = null)
    {
        var result = new List<Opening>();
        if (root == null) return result;
        if (settings == null) settings = new Settings();

        MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>(true);
        if (filters.Length == 0)
        {
            if (log != null) log.AppendLine("  no MeshFilter found");
            return result;
        }

        // Overall extent in root-local space, used for the weld epsilon, the merge distance and
        // to decide which way an opening faces.
        Bounds localBounds = default(Bounds);
        bool haveBounds = false;
        foreach (MeshFilter mf in filters)
        {
            Mesh m = mf.sharedMesh;
            if (m == null) continue;
            Bounds b = m.bounds;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = b.center + Vector3.Scale(b.extents,
                    new Vector3((i & 1) == 0 ? 1f : -1f, (i & 2) == 0 ? 1f : -1f, (i & 4) == 0 ? 1f : -1f));
                Vector3 p = ToRootLocal(root, mf, corner);
                if (!haveBounds) { localBounds = new Bounds(p, Vector3.zero); haveBounds = true; }
                else localBounds.Encapsulate(p);
            }
        }
        if (!haveBounds)
        {
            if (log != null) log.AppendLine("  no mesh geometry");
            return result;
        }

        float meshSize = Mathf.Max(localBounds.size.magnitude, 1e-4f);
        var welder = new VertexWelder(meshSize * Mathf.Max(settings.WeldEpsilonFraction, 1e-6f));
        var tris = new List<int>();

        foreach (MeshFilter mf in filters)
        {
            Mesh m = mf.sharedMesh;
            if (m == null || !m.isReadable) continue;
            Vector3[] verts = m.vertices;
            int[] indices = m.triangles;
            var remap = new int[verts.Length];
            for (int i = 0; i < verts.Length; i++) remap[i] = welder.Add(ToRootLocal(root, mf, verts[i]));
            for (int i = 0; i < indices.Length; i++) tris.Add(remap[indices[i]]);
        }

        if (tris.Count == 0)
        {
            if (log != null) log.AppendLine("  mesh not readable (enable Read/Write in the model importer)");
            return result;
        }

        List<Vector3> points = welder.Points;

        // Count how many triangles use each undirected edge. Exactly one means a boundary.
        var edgeUse = new Dictionary<long, int>(tris.Count);
        for (int t = 0; t < tris.Count; t += 3)
        {
            AddEdge(edgeUse, tris[t], tris[t + 1]);
            AddEdge(edgeUse, tris[t + 1], tris[t + 2]);
            AddEdge(edgeUse, tris[t + 2], tris[t]);
        }

        var adjacency = new Dictionary<int, List<int>>();
        int boundaryEdges = 0;
        foreach (KeyValuePair<long, int> e in edgeUse)
        {
            if (e.Value != 1) continue;
            boundaryEdges++;
            int a = (int)(e.Key >> 32);
            int b = (int)(e.Key & 0xFFFFFFFF);
            Link(adjacency, a, b);
            Link(adjacency, b, a);
        }

        if (log != null)
        {
            log.AppendLine(string.Format("  welded {0} verts, {1} tris, {2} boundary edges",
                points.Count, tris.Count / 3, boundaryEdges));
        }
        if (boundaryEdges == 0) return result;

        List<List<int>> loops = TraceLoops(adjacency);
        var candidates = new List<Opening>();
        float largestArea = 0f;

        foreach (List<int> loop in loops)
        {
            if (loop.Count < 3) continue;

            Vector3 center = Vector3.zero;
            for (int i = 0; i < loop.Count; i++) center += points[loop[i]];
            center /= loop.Count;

            // Area-weighted normal: the summed fan cross products give twice the enclosed area.
            Vector3 n = Vector3.zero;
            for (int i = 0; i < loop.Count; i++)
            {
                Vector3 p0 = points[loop[i]] - center;
                Vector3 p1 = points[loop[(i + 1) % loop.Count]] - center;
                n += Vector3.Cross(p0, p1);
            }
            float twiceArea = n.magnitude;
            if (twiceArea <= 1e-8f) continue;
            float area = twiceArea * 0.5f;
            n /= twiceArea;

            float radius = Mathf.Sqrt(area / Mathf.PI);

            float maxDeviation = 0f;
            for (int i = 0; i < loop.Count; i++)
                maxDeviation = Mathf.Max(maxDeviation, Mathf.Abs(Vector3.Dot(points[loop[i]] - center, n)));
            if (maxDeviation > settings.MaxPlanarity * radius)
            {
                if (log != null)
                    log.AppendLine(string.Format("  reject loop ({0} pts, r={1:F2}): not planar (deviation {2:F3})",
                        loop.Count, radius, maxDeviation));
                continue;
            }

            // Face away from the piece.
            if (Vector3.Dot(n, center - localBounds.center) < 0f) n = -n;

            largestArea = Mathf.Max(largestArea, area);
            candidates.Add(new Opening { Center = center, Direction = n, Radius = radius, Area = area, RimCount = 1 });
        }

        // Drop detail holes, then anything that does not face sideways.
        var kept = new List<Opening>();
        foreach (Opening o in candidates)
        {
            if (o.Area < largestArea * settings.MinRelativeArea)
            {
                if (log != null)
                    log.AppendLine(string.Format("  reject opening r={0:F2}: too small ({1:P0} of largest)",
                        o.Radius, o.Area / largestArea));
                continue;
            }
            Vector3 world = root.transform.TransformDirection(o.Direction).normalized;
            if (Mathf.Abs(Vector3.Dot(world, Vector3.up)) > settings.MaxVerticalDot)
            {
                if (log != null)
                    log.AppendLine(string.Format("  reject opening r={0:F2}: faces vertically (world {1})",
                        o.Radius, world.ToString("F2")));
                continue;
            }
            kept.Add(o);
        }

        // Merge concentric coplanar rims (the inner and outer wall of one opening).
        float mergeDistance = meshSize * 0.02f;
        foreach (Opening o in kept)
        {
            int hit = -1;
            for (int i = 0; i < result.Count; i++)
            {
                if (Vector3.Dot(result[i].Direction, o.Direction) < 0.98f) continue;
                if (Mathf.Abs(Vector3.Dot(result[i].Center - o.Center, o.Direction)) > mergeDistance) continue;
                if (Vector3.Distance(result[i].Center, o.Center) > Mathf.Max(mergeDistance, o.Radius)) continue;
                hit = i;
                break;
            }
            if (hit < 0)
            {
                result.Add(o);
                continue;
            }

            Opening merged = result[hit];
            float wa = merged.Area;
            float wb = o.Area;
            merged.Center = (merged.Center * wa + o.Center * wb) / Mathf.Max(wa + wb, 1e-6f);
            merged.Direction = (merged.Direction * wa + o.Direction * wb).normalized;
            merged.Radius = Mathf.Max(merged.Radius, o.Radius);
            merged.Area = Mathf.Max(merged.Area, o.Area);
            merged.RimCount = merged.RimCount + 1;
            result[hit] = merged;
        }

        if (log != null)
        {
            foreach (Opening o in result)
            {
                log.AppendLine(string.Format(
                    "  OPENING local c=({0:F3},{1:F3},{2:F3}) dir=({3:F2},{4:F2},{5:F2}) r={6:F3} rims={7}",
                    o.Center.x, o.Center.y, o.Center.z,
                    o.Direction.x, o.Direction.y, o.Direction.z, o.Radius, o.RimCount));
            }
        }

        return result;
    }

    static Vector3 ToRootLocal(GameObject root, MeshFilter mf, Vector3 meshPoint)
    {
        if (mf.transform == root.transform) return meshPoint;
        return root.transform.InverseTransformPoint(mf.transform.TransformPoint(meshPoint));
    }

    static void AddEdge(Dictionary<long, int> map, int a, int b)
    {
        if (a == b) return;
        long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        int n;
        map[key] = map.TryGetValue(key, out n) ? n + 1 : 1;
    }

    static void Link(Dictionary<int, List<int>> adjacency, int a, int b)
    {
        List<int> list;
        if (!adjacency.TryGetValue(a, out list)) { list = new List<int>(2); adjacency[a] = list; }
        list.Add(b);
    }

    /// <summary>Walks a boundary-edge adjacency graph into closed loops.</summary>
    static List<List<int>> TraceLoops(Dictionary<int, List<int>> adjacency)
    {
        var loops = new List<List<int>>();
        var visited = new HashSet<int>();

        foreach (KeyValuePair<int, List<int>> start in adjacency)
        {
            if (visited.Contains(start.Key)) continue;

            var loop = new List<int>();
            int current = start.Key;
            int previous = -1;

            while (current >= 0 && !visited.Contains(current))
            {
                visited.Add(current);
                loop.Add(current);

                int next = -1;
                List<int> neighbours;
                if (adjacency.TryGetValue(current, out neighbours))
                {
                    // Prefer an unvisited neighbour; a non-manifold junction just takes the first.
                    for (int i = 0; i < neighbours.Count; i++)
                    {
                        int candidate = neighbours[i];
                        if (candidate == previous || visited.Contains(candidate)) continue;
                        next = candidate;
                        break;
                    }
                }
                previous = current;
                current = next;
            }

            if (loop.Count >= 3) loops.Add(loop);
        }

        return loops;
    }

    /// <summary>Spatial-hash welder: one index per distinct position within epsilon.</summary>
    sealed class VertexWelder
    {
        readonly Dictionary<long, List<int>> m_Cells = new Dictionary<long, List<int>>();
        readonly List<Vector3> m_Points = new List<Vector3>();
        readonly float m_Cell;
        readonly float m_EpsilonSq;

        public VertexWelder(float epsilon)
        {
            m_Cell = Mathf.Max(epsilon, 1e-6f);
            m_EpsilonSq = m_Cell * m_Cell;
        }

        public List<Vector3> Points { get { return m_Points; } }

        static long Key(int x, int y, int z)
        {
            return ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);
        }

        public int Add(Vector3 p)
        {
            int cx = Mathf.FloorToInt(p.x / m_Cell);
            int cy = Mathf.FloorToInt(p.y / m_Cell);
            int cz = Mathf.FloorToInt(p.z / m_Cell);

            // Check the 27 surrounding cells so a pair straddling a cell edge still welds.
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        List<int> bucket;
                        if (!m_Cells.TryGetValue(Key(cx + dx, cy + dy, cz + dz), out bucket)) continue;
                        for (int i = 0; i < bucket.Count; i++)
                            if ((m_Points[bucket[i]] - p).sqrMagnitude <= m_EpsilonSq) return bucket[i];
                    }
                }
            }

            int index = m_Points.Count;
            m_Points.Add(p);
            long key = Key(cx, cy, cz);
            List<int> own;
            if (!m_Cells.TryGetValue(key, out own)) { own = new List<int>(4); m_Cells[key] = own; }
            own.Add(index);
            return index;
        }
    }
}
