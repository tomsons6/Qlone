using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// The snapping maths and scene queries behind the Corridor Snap tool. Kept separate from the
/// window so the menu-item hotkeys and the scene-view handles share one implementation.
///
/// <para>Everything here works on <see cref="CorridorPiece.Socket"/>s -- an opening's centre and
/// facing -- never on transforms or bounding boxes. Two pieces are joined by making one opening
/// coincide exactly with another, which is the only alignment that guarantees no gap: the corridor
/// pivots are arbitrary (TCross sits ~12 units from its own mesh) and the bounding boxes are
/// asymmetric on a corner's closed sides, so both mislead.</para>
///
/// <para>Rotation is applied as a YAW about world up only, which keeps a piece level: these meshes
/// carry a baked -90 degree X rotation from Blender that has to survive being snapped. The yaw is
/// always the exact one the join needs -- rounding it to 90 degree steps up front would leave a
/// piece that arrived at an odd angle sitting in a correctly positioned but visibly tilted joint.
/// Tidying to whole degrees happens after, and only within <see cref="TidyRotationTolerance"/>.</para>
/// </summary>
public static class CorridorSnap
{
    /// <summary>How close two openings must be to count as already joined (world units).</summary>
    public const float JoinTolerance = 0.02f;

    /// <summary>
    /// How far off an exact 90 degree step a rotation may be and still get rounded to it by the
    /// "tidy rotation" option. Deliberately tiny: it exists to erase the ~0.02 degree drift the kit
    /// imports with (rotations read 270.0198), not to force a misaligned piece onto the axis. A
    /// residual angle here tilts the joint plane, so 0.1 degree caps the resulting wedge at about
    /// 0.002 units across a 2.26-wide corridor.
    /// </summary>
    public const float TidyRotationTolerance = 0.1f;

    /// <summary>One socket on one piece, resolved to world space.</summary>
    public struct SocketRef
    {
        public CorridorPiece Piece;
        public int Index;
        public Vector3 Position;
        public Vector3 Direction;
        public float Radius;

        public bool IsValid { get { return Piece != null; } }
    }

    /// <summary>A pair of openings that face each other but do not quite meet.</summary>
    public struct Joint
    {
        public SocketRef A;
        public SocketRef B;
        public float Distance;
        public float AngleError;
    }

    // ---------------------------------------------------------------- queries

    /// <summary>Every socket of every <see cref="CorridorPiece"/> in the open scenes.</summary>
    public static List<SocketRef> CollectSceneSockets()
    {
        CorridorPiece[] pieces = Object.FindObjectsByType<CorridorPiece>(FindObjectsSortMode.None);
        var sockets = new List<SocketRef>(pieces.Length * 3);
        foreach (CorridorPiece piece in pieces) AppendSockets(piece, sockets);
        return sockets;
    }

    public static void AppendSockets(CorridorPiece piece, List<SocketRef> into)
    {
        if (piece == null) return;
        for (int i = 0; i < piece.SocketCount; i++)
        {
            into.Add(new SocketRef
            {
                Piece = piece,
                Index = i,
                Position = piece.GetSocketPosition(i),
                Direction = piece.GetSocketDirection(i),
                Radius = piece.GetSocketRadius(i),
            });
        }
    }

    /// <summary>
    /// True when <paramref name="socket"/> already has another piece butted onto it. Sockets on
    /// <paramref name="ignore"/> (and its children) do not count, so a piece being moved never
    /// reads as connected to itself.
    /// </summary>
    public static bool IsOccupied(SocketRef socket, List<SocketRef> all, CorridorPiece ignore = null)
    {
        SocketRef partner;
        return TryFindPartner(socket, all, JoinTolerance, ignore, out partner);
    }

    /// <summary>Finds an opening within <paramref name="tolerance"/> of <paramref name="socket"/> that faces it.</summary>
    public static bool TryFindPartner(SocketRef socket, List<SocketRef> all, float tolerance,
        CorridorPiece ignore, out SocketRef partner)
    {
        partner = default(SocketRef);
        float best = float.MaxValue;
        for (int i = 0; i < all.Count; i++)
        {
            SocketRef other = all[i];
            if (other.Piece == socket.Piece) continue;
            if (ignore != null && other.Piece == ignore) continue;
            if (Vector3.Dot(other.Direction, socket.Direction) > -0.7f) continue;
            float d = Vector3.Distance(other.Position, socket.Position);
            if (d > tolerance || d >= best) continue;
            best = d;
            partner = other;
        }
        return partner.IsValid;
    }

    /// <summary>
    /// Openings that face each other within <paramref name="searchDistance"/> but are further apart
    /// than <see cref="JoinTolerance"/> -- i.e. the visible gaps and misalignments in the level.
    /// Each pair is reported once.
    /// </summary>
    public static List<Joint> FindGaps(float searchDistance)
    {
        List<SocketRef> all = CollectSceneSockets();
        var gaps = new List<Joint>();
        var seen = new HashSet<long>();

        for (int i = 0; i < all.Count; i++)
        {
            for (int j = i + 1; j < all.Count; j++)
            {
                SocketRef a = all[i];
                SocketRef b = all[j];
                if (a.Piece == b.Piece) continue;

                float d = Vector3.Distance(a.Position, b.Position);
                if (d <= JoinTolerance || d > searchDistance) continue;

                float dot = Vector3.Dot(a.Direction, b.Direction);
                if (dot > -0.7f) continue;

                long key = Pair(a.Piece.GetInstanceID(), a.Index, b.Piece.GetInstanceID(), b.Index);
                if (!seen.Add(key)) continue;

                gaps.Add(new Joint
                {
                    A = a,
                    B = b,
                    Distance = d,
                    AngleError = Vector3.Angle(a.Direction, -b.Direction),
                });
            }
        }

        gaps.Sort((x, y) => y.Distance.CompareTo(x.Distance));
        return gaps;
    }

    static long Pair(int idA, int socketA, int idB, int socketB)
    {
        long a = ((long)idA << 4) ^ (uint)socketA;
        long b = ((long)idB << 4) ^ (uint)socketB;
        return a < b ? (a * 31 + b) : (b * 31 + a);
    }

    // ---------------------------------------------------------------- snapping

    /// <summary>
    /// Moves <paramref name="piece"/> so its socket <paramref name="socketIndex"/> sits exactly on
    /// <paramref name="target"/>, facing into it. Registers undo.
    ///
    /// <para>The rotation is always the EXACT one that makes the two openings face each other, so
    /// the joint is never left with a tilted seam. <paramref name="tidyRotation"/> then rounds the
    /// result to whole 90 degree steps, but only where that is a move of under
    /// <see cref="TidyRotationTolerance"/> -- enough to erase import drift, never enough to pull a
    /// deliberately angled piece off its join. Translation happens last either way, so the openings
    /// coincide exactly whatever the rotation ended up being.</para>
    /// </summary>
    public static void AlignTo(CorridorPiece piece, int socketIndex, SocketRef target, bool tidyRotation)
    {
        if (piece == null || socketIndex < 0 || socketIndex >= piece.SocketCount) return;

        Transform t = piece.transform;
        Undo.RecordObject(t, "Snap Corridor Piece");

        // Yaw about world up so the two openings face each other. Yaw-only keeps the piece level
        // and preserves the baked -90 X rotation these meshes rely on.
        Vector3 up = Vector3.up;
        Vector3 mine = Vector3.ProjectOnPlane(piece.GetSocketDirection(socketIndex), up);
        Vector3 want = Vector3.ProjectOnPlane(-target.Direction, up);
        if (mine.sqrMagnitude > 1e-8f && want.sqrMagnitude > 1e-8f)
        {
            float angle = Vector3.SignedAngle(mine.normalized, want.normalized, up);
            if (Mathf.Abs(angle) > 1e-5f) t.rotation = Quaternion.AngleAxis(angle, up) * t.rotation;
        }

        if (tidyRotation) TidyRotation(t);

        // Translate last, so the openings coincide regardless of any rounding above.
        t.position += target.Position - piece.GetSocketPosition(socketIndex);
    }

    /// <summary>
    /// Rounds a rotation to exact 90 degree steps, but only on axes already within
    /// <see cref="TidyRotationTolerance"/> of one. Leaves a genuinely angled piece alone.
    /// </summary>
    static void TidyRotation(Transform t)
    {
        Vector3 e = t.eulerAngles;
        Vector3 tidy = e;
        for (int axis = 0; axis < 3; axis++)
        {
            float value = e[axis];
            float snapped = Mathf.Round(value / 90f) * 90f;
            if (Mathf.Abs(Mathf.DeltaAngle(value, snapped)) <= TidyRotationTolerance) tidy[axis] = snapped;
        }
        if (tidy != e) t.rotation = Quaternion.Euler(tidy);
    }

    /// <summary>
    /// Snaps <paramref name="piece"/> onto whichever nearby opening it is closest to. Free openings
    /// win over occupied ones, so dragging a piece to an open end does the obvious thing.
    /// Returns false (with a reason) when there is nothing in range.
    /// </summary>
    public static bool SnapToNearest(CorridorPiece piece, float searchRadius, bool tidyRotation,
        bool preferFree, out string message)
    {
        message = null;
        if (piece == null) { message = "No piece."; return false; }
        if (piece.SocketCount == 0)
        {
            message = piece.name + " has no sockets -- bake them first.";
            return false;
        }

        List<SocketRef> all = CollectSceneSockets();
        var mine = new List<SocketRef>();
        AppendSockets(piece, mine);

        int bestMine = -1;
        SocketRef bestTarget = default(SocketRef);
        float bestScore = float.MaxValue;
        bool bestIsFree = false;

        for (int m = 0; m < mine.Count; m++)
        {
            for (int i = 0; i < all.Count; i++)
            {
                SocketRef target = all[i];
                if (target.Piece == piece) continue;

                float d = Vector3.Distance(target.Position, mine[m].Position);
                if (d > searchRadius) continue;

                bool free = !IsOccupied(target, all, piece);

                // A free opening always beats an occupied one; among equals, nearest wins.
                bool better;
                if (bestMine < 0) better = true;
                else if (preferFree && free != bestIsFree) better = free;
                else better = d < bestScore;
                if (!better) continue;

                bestScore = d;
                bestMine = mine[m].Index;
                bestTarget = target;
                bestIsFree = free;
            }
        }

        if (bestMine < 0)
        {
            message = "No corridor opening within " + searchRadius.ToString("F1") + " units of " + piece.name + ".";
            return false;
        }

        AlignTo(piece, bestMine, bestTarget, tidyRotation);
        message = string.Format("Snapped {0} to {1} (socket {2}).", piece.name, bestTarget.Piece.name, bestTarget.Index);
        return true;
    }

    /// <summary>
    /// Re-snaps <paramref name="piece"/> to the joint it is already sitting in, but entering through
    /// its NEXT socket. On a corner that swaps which arm connects, which is how you flip the turn
    /// without hand-rotating.
    /// </summary>
    public static bool CycleEntrySocket(CorridorPiece piece, bool tidyRotation, out string message)
    {
        message = null;
        if (piece == null || piece.SocketCount < 2)
        {
            message = "Needs a piece with at least two sockets.";
            return false;
        }

        List<SocketRef> all = CollectSceneSockets();
        var mine = new List<SocketRef>();
        AppendSockets(piece, mine);

        // Which of our sockets is currently joined, and to what?
        for (int m = 0; m < mine.Count; m++)
        {
            SocketRef partner;
            if (!TryFindPartner(mine[m], all, JoinTolerance, null, out partner)) continue;

            int next = (mine[m].Index + 1) % piece.SocketCount;
            AlignTo(piece, next, partner, tidyRotation);
            message = string.Format("{0} now enters through socket {1}.", piece.name, next);
            return true;
        }

        message = piece.name + " is not joined to anything -- snap it first.";
        return false;
    }

    /// <summary>
    /// Removes accumulated drift: rounds the rotation to exact 90 degree steps and the position to
    /// <paramref name="positionGrid"/>. The kit imports with rotations like (270.0198, 0, 0), and
    /// that fraction of a degree is enough to open a visible seam over a few pieces.
    /// </summary>
    public static void Straighten(Transform t, float positionGrid)
    {
        if (t == null) return;
        Undo.RecordObject(t, "Straighten Corridor Piece");

        Vector3 e = t.eulerAngles;
        t.rotation = Quaternion.Euler(
            Mathf.Round(e.x / 90f) * 90f,
            Mathf.Round(e.y / 90f) * 90f,
            Mathf.Round(e.z / 90f) * 90f);

        if (positionGrid > 0f)
        {
            Vector3 p = t.position;
            t.position = new Vector3(
                Mathf.Round(p.x / positionGrid) * positionGrid,
                Mathf.Round(p.y / positionGrid) * positionGrid,
                Mathf.Round(p.z / positionGrid) * positionGrid);
        }
    }

    // ---------------------------------------------------------------- baking

    /// <summary>
    /// Writes freshly detected openings onto <paramref name="piece"/>. Returns how many were found.
    /// </summary>
    public static int Bake(GameObject root, CorridorSocketBaker.Settings settings, System.Text.StringBuilder log)
    {
        if (root == null) return 0;

        List<CorridorSocketBaker.Opening> openings = CorridorSocketBaker.FindOpenings(root, settings, log);

        CorridorPiece piece = root.GetComponent<CorridorPiece>();
        if (piece == null) piece = Undo.AddComponent<CorridorPiece>(root);
        else Undo.RecordObject(piece, "Bake Corridor Sockets");

        piece.Sockets.Clear();
        foreach (CorridorSocketBaker.Opening o in openings)
        {
            piece.Sockets.Add(new CorridorPiece.Socket
            {
                LocalPosition = o.Center,
                LocalDirection = o.Direction,
                Radius = o.Radius,
            });
        }

        EditorUtility.SetDirty(piece);
        return openings.Count;
    }
}
