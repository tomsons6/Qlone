using UnityEditor;
using UnityEngine;

/// <summary>
/// Inspector for <see cref="CorridorPiece"/>: re-bake the openings from the mesh, and drag them in
/// the scene view for the rare piece automatic detection gets wrong -- an angled end, or a mesh
/// whose opening is capped and so has no boundary rim to find.
/// </summary>
[CustomEditor(typeof(CorridorPiece))]
public class CorridorPieceEditor : Editor
{
    CorridorPiece m_Piece;

    void OnEnable()
    {
        m_Piece = (CorridorPiece)target;
        SceneView.duringSceneGui += DrawHandles;
    }

    void OnDisable()
    {
        SceneView.duringSceneGui -= DrawHandles;
    }

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        EditorGUILayout.Space(4f);
        EditorGUILayout.HelpBox(
            m_Piece.SocketCount + " opening(s). Positions are local to this transform, so they follow "
            + "its rotation and scale. Drag the handles in the scene view to adjust.",
            MessageType.None);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Re-bake from mesh"))
            {
                var log = new System.Text.StringBuilder();
                int n = CorridorSnap.Bake(m_Piece.gameObject, new CorridorSocketBaker.Settings(), log);
                Debug.Log("[Corridor Snap] " + m_Piece.name + ": " + n + " openings\n" + log);
                SceneView.RepaintAll();
            }
            if (GUILayout.Button("Open Snap window")) CorridorSnapWindow.Open();
        }
    }

    void DrawHandles(SceneView view)
    {
        if (m_Piece == null) return;
        Transform t = m_Piece.transform;

        for (int i = 0; i < m_Piece.SocketCount; i++)
        {
            CorridorPiece.Socket socket = m_Piece.Sockets[i];
            if (socket == null) continue;

            Vector3 world = m_Piece.GetSocketPosition(i);
            Vector3 direction = m_Piece.GetSocketDirection(i);
            float radius = m_Piece.GetSocketRadius(i);
            Quaternion look = Quaternion.LookRotation(direction, Vector3.up);

            Handles.color = new Color(0.25f, 1f, 0.45f, 1f);
            Handles.CircleHandleCap(0, world, look, radius, EventType.Repaint);
            Handles.DrawLine(world, world + direction * radius * 0.6f);
            Handles.Label(world + Vector3.up * (radius * 0.6f), "socket " + i);

            EditorGUI.BeginChangeCheck();
            Vector3 moved = Handles.PositionHandle(world, look);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(m_Piece, "Move Corridor Socket");
                socket.LocalPosition = t.InverseTransformPoint(moved);
                EditorUtility.SetDirty(m_Piece);
            }
        }
    }
}
