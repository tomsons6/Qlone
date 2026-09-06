using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// <c>Tools &gt; Qlone &gt; Corridor Snap</c> -- the authoring tool for the modular corridor kit.
///
/// <para>Three things, in the order you use them:</para>
/// <list type="number">
/// <item><b>Bake</b> once per kit: finds each prefab's open ends from its mesh and stores them as
/// <see cref="CorridorPiece"/> sockets. Scene instances inherit them from the prefab.</item>
/// <item><b>Build</b>: pick a piece in the palette, then click any green opening in the scene view.
/// The piece is created already butted onto that opening -- no dragging, no gaps.</item>
/// <item><b>Audit</b>: lists every pair of openings that face each other but do not meet, so the
/// seams already in the level can be found and closed in one go.</item>
/// </list>
///
/// <para>Openings are drawn in the scene view while this window is open: green = free,
/// blue = properly joined, yellow/red = joined with a gap.</para>
/// </summary>
public class CorridorSnapWindow : EditorWindow
{
    const string KitFolderKey = "Qlone.CorridorSnap.KitFolder";
    const string DefaultKitFolder = "Assets/_App/Prefabs/EnviromentModel prefabs/EnvironmentCorridors";

    static readonly Color FreeColor = new Color(0.25f, 1f, 0.45f, 1f);
    static readonly Color JoinedColor = new Color(0.35f, 0.7f, 1f, 1f);
    static readonly Color GapColor = new Color(1f, 0.85f, 0.15f, 1f);
    static readonly Color BadColor = new Color(1f, 0.3f, 0.2f, 1f);

    string m_KitFolder = DefaultKitFolder;
    readonly List<GameObject> m_Kit = new List<GameObject>();
    int m_Brush = -1;
    int m_EntrySocket;

    bool m_TidyRotation = true;
    bool m_PreferFree = true;
    float m_SearchRadius = 6f;
    float m_GapSearch = 2.5f;
    float m_PositionGrid = 0.001f;
    bool m_DrawSockets = true;
    bool m_BuildMode = true;
    float m_DrawDistance = 60f;

    List<CorridorSnap.Joint> m_Gaps;
    Vector2 m_Scroll;
    string m_Status = string.Empty;

    // ---------------------------------------------------------------- menu

    [MenuItem("Tools/Qlone/Corridor Snap")]
    public static void Open()
    {
        CorridorSnapWindow window = GetWindow<CorridorSnapWindow>("Corridor Snap");
        window.minSize = new Vector2(320f, 420f);
    }

    [MenuItem("Tools/Qlone/Snap Corridor Selection &j")]
    public static void SnapSelectionMenu()
    {
        CorridorSnapWindow window = GetWindow<CorridorSnapWindow>("Corridor Snap");
        window.SnapSelection();
    }

    [MenuItem("Tools/Qlone/Cycle Corridor Joint &k")]
    public static void CycleSelectionMenu()
    {
        CorridorSnapWindow window = GetWindow<CorridorSnapWindow>("Corridor Snap");
        window.CycleSelection();
    }

    // ---------------------------------------------------------------- lifecycle

    void OnEnable()
    {
        m_KitFolder = EditorPrefs.GetString(KitFolderKey, DefaultKitFolder);
        RefreshKit();
        SceneView.duringSceneGui += OnSceneGUI;
    }

    void OnDisable()
    {
        SceneView.duringSceneGui -= OnSceneGUI;
        SceneView.RepaintAll();
    }

    // ---------------------------------------------------------------- window UI

    void OnGUI()
    {
        m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);

        DrawKitSection();
        EditorGUILayout.Space(6f);
        DrawBuildSection();
        EditorGUILayout.Space(6f);
        DrawSnapSection();
        EditorGUILayout.Space(6f);
        DrawAuditSection();

        if (!string.IsNullOrEmpty(m_Status))
        {
            EditorGUILayout.Space(6f);
            EditorGUILayout.HelpBox(m_Status, MessageType.None);
        }

        EditorGUILayout.EndScrollView();
    }

    void DrawKitSection()
    {
        EditorGUILayout.LabelField("1. Kit", EditorStyles.boldLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            string folder = EditorGUILayout.TextField("Prefab folder", m_KitFolder);
            if (folder != m_KitFolder)
            {
                m_KitFolder = folder;
                EditorPrefs.SetString(KitFolderKey, m_KitFolder);
                RefreshKit();
            }
            if (GUILayout.Button("...", GUILayout.Width(26f)))
            {
                string picked = EditorUtility.OpenFolderPanel("Corridor kit folder", m_KitFolder, string.Empty);
                if (!string.IsNullOrEmpty(picked))
                {
                    m_KitFolder = ToProjectPath(picked);
                    EditorPrefs.SetString(KitFolderKey, m_KitFolder);
                    RefreshKit();
                }
            }
            if (GUILayout.Button("Reload", GUILayout.Width(56f))) RefreshKit();
        }

        if (m_Kit.Count == 0)
        {
            EditorGUILayout.HelpBox("No prefabs found in that folder.", MessageType.Warning);
            return;
        }

        int unbaked = 0;
        foreach (GameObject go in m_Kit)
        {
            CorridorPiece piece = go.GetComponent<CorridorPiece>();
            int count = piece != null ? piece.SocketCount : 0;
            if (count == 0) unbaked++;
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(go.name, GUILayout.Width(140f));
                EditorGUILayout.LabelField(count == 0 ? "not baked" : count + " sockets", EditorStyles.miniLabel);
            }
        }

        if (GUILayout.Button(unbaked > 0
            ? "Bake sockets on kit prefabs (" + unbaked + " missing)"
            : "Re-bake sockets on kit prefabs"))
        {
            BakeKit();
        }

        using (new EditorGUI.DisabledScope(Selection.gameObjects.Length == 0))
        {
            if (GUILayout.Button("Bake sockets on selection")) BakeSelection();
        }
    }

    void DrawBuildSection()
    {
        EditorGUILayout.LabelField("2. Build", EditorStyles.boldLabel);
        m_BuildMode = EditorGUILayout.ToggleLeft("Click an opening in the scene to place a piece", m_BuildMode);

        using (new EditorGUI.DisabledScope(!m_BuildMode))
        {
            int columns = 3;
            for (int i = 0; i < m_Kit.Count; i++)
            {
                if (i % columns == 0) EditorGUILayout.BeginHorizontal();
                bool on = m_Brush == i;
                if (GUILayout.Toggle(on, m_Kit[i].name, EditorStyles.miniButton) != on)
                    m_Brush = on ? -1 : i;
                if (i % columns == columns - 1 || i == m_Kit.Count - 1) EditorGUILayout.EndHorizontal();
            }

            m_EntrySocket = EditorGUILayout.IntField(
                new GUIContent("Enter through socket", "Which opening of the new piece attaches to the one you click. Alt+K cycles it afterwards."),
                m_EntrySocket);
            if (m_EntrySocket < 0) m_EntrySocket = 0;
        }
    }

    void DrawSnapSection()
    {
        EditorGUILayout.LabelField("3. Snap existing pieces", EditorStyles.boldLabel);
        m_SearchRadius = EditorGUILayout.Slider(
            new GUIContent("Search radius", "How far to look for an opening to snap onto."), m_SearchRadius, 0.5f, 30f);
        m_TidyRotation = EditorGUILayout.ToggleLeft(
            new GUIContent("Tidy rotation after snapping",
                "Rounds the result to whole 90 degree steps when it is already within 0.1 degree of one, "
                + "which erases the drift the kit imports with (rotations read 270.0198). The join itself is "
                + "always exact either way -- this only cleans up the numbers in the inspector."),
            m_TidyRotation);
        m_PreferFree = EditorGUILayout.ToggleLeft(
            new GUIContent("Prefer free openings", "Snap to an unused opening even if an occupied one is slightly nearer."),
            m_PreferFree);

        using (new EditorGUI.DisabledScope(Selection.gameObjects.Length == 0))
        {
            if (GUILayout.Button("Snap selection to nearest opening   (Alt+J)")) SnapSelection();
            if (GUILayout.Button("Cycle which socket connects   (Alt+K)")) CycleSelection();

            EditorGUILayout.Space(2f);
            m_PositionGrid = EditorGUILayout.FloatField(
                new GUIContent("Straighten grid", "Rounding applied to position by Straighten. Rotation always goes to exact 90 degree steps."),
                m_PositionGrid);
            if (GUILayout.Button("Straighten selection (fix rotation drift)")) StraightenSelection();
        }
    }

    void DrawAuditSection()
    {
        EditorGUILayout.LabelField("4. Audit gaps", EditorStyles.boldLabel);
        m_DrawSockets = EditorGUILayout.ToggleLeft("Draw openings in the scene view", m_DrawSockets);
        m_DrawDistance = EditorGUILayout.Slider("Draw distance", m_DrawDistance, 10f, 300f);
        m_GapSearch = EditorGUILayout.Slider(
            new GUIContent("Gap search", "Openings facing each other but further apart than this are treated as separate joints, not gaps."),
            m_GapSearch, 0.1f, 10f);

        if (GUILayout.Button("Find gaps in open scenes"))
        {
            m_Gaps = CorridorSnap.FindGaps(m_GapSearch);
            m_Status = m_Gaps.Count == 0
                ? "No gaps found."
                : m_Gaps.Count + " joint(s) not closed. Worst is " + m_Gaps[0].Distance.ToString("F3") + " units.";
        }

        if (m_Gaps == null) return;

        if (m_Gaps.Count > 0 && GUILayout.Button("Close all " + m_Gaps.Count + " gap(s)")) CloseAllGaps();

        int shown = Mathf.Min(m_Gaps.Count, 20);
        for (int i = 0; i < shown; i++)
        {
            CorridorSnap.Joint joint = m_Gaps[i];
            if (joint.A.Piece == null || joint.B.Piece == null) continue;

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(
                    string.Format("{0:F3}u  {1} - {2}", joint.Distance, joint.A.Piece.name, joint.B.Piece.name),
                    EditorStyles.miniLabel);
                if (GUILayout.Button("Show", EditorStyles.miniButton, GUILayout.Width(46f)))
                {
                    Selection.objects = new Object[] { joint.B.Piece.gameObject };
                    SceneView.lastActiveSceneView?.LookAt(joint.A.Position);
                }
                if (GUILayout.Button("Close", EditorStyles.miniButton, GUILayout.Width(46f)))
                {
                    CorridorSnap.AlignTo(joint.B.Piece, joint.B.Index, joint.A, m_TidyRotation);
                    m_Gaps = CorridorSnap.FindGaps(m_GapSearch);
                    break;
                }
            }
        }
        if (m_Gaps.Count > shown) EditorGUILayout.LabelField("... and " + (m_Gaps.Count - shown) + " more", EditorStyles.miniLabel);
    }

    // ---------------------------------------------------------------- actions

    void RefreshKit()
    {
        m_Kit.Clear();
        if (string.IsNullOrEmpty(m_KitFolder) || !AssetDatabase.IsValidFolder(m_KitFolder)) return;

        string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { m_KitFolder });
        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            GameObject go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (go != null) m_Kit.Add(go);
        }
        m_Kit.Sort((a, b) => string.Compare(a.name, b.name, System.StringComparison.OrdinalIgnoreCase));
        if (m_Brush >= m_Kit.Count) m_Brush = -1;
    }

    void BakeKit()
    {
        var log = new System.Text.StringBuilder();
        int total = 0;

        foreach (GameObject asset in m_Kit)
        {
            string path = AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrEmpty(path)) continue;

            GameObject contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                log.AppendLine("=== " + asset.name);
                List<CorridorSocketBaker.Opening> openings =
                    CorridorSocketBaker.FindOpenings(contents, new CorridorSocketBaker.Settings(), log);

                CorridorPiece piece = contents.GetComponent<CorridorPiece>();
                if (piece == null) piece = contents.AddComponent<CorridorPiece>();
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

                PrefabUtility.SaveAsPrefabAsset(contents, path);
                total += openings.Count;
                log.AppendLine("  -> " + openings.Count + " sockets");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        AssetDatabase.SaveAssets();
        RefreshKit();
        m_Status = "Baked " + total + " sockets across " + m_Kit.Count + " prefab(s).";
        Debug.Log("[Corridor Snap] Bake report:\n" + log);
        SceneView.RepaintAll();
    }

    void BakeSelection()
    {
        int total = 0;
        var log = new System.Text.StringBuilder();
        foreach (GameObject go in Selection.gameObjects)
        {
            log.AppendLine("=== " + go.name);
            total += CorridorSnap.Bake(go, new CorridorSocketBaker.Settings(), log);
        }
        m_Status = "Baked " + total + " sockets on " + Selection.gameObjects.Length + " object(s).";
        Debug.Log("[Corridor Snap] Bake report:\n" + log);
        SceneView.RepaintAll();
    }

    void SnapSelection()
    {
        int done = 0;
        string last = null;
        foreach (GameObject go in Selection.gameObjects)
        {
            CorridorPiece piece = go.GetComponent<CorridorPiece>();
            if (piece == null) continue;
            if (CorridorSnap.SnapToNearest(piece, m_SearchRadius, m_TidyRotation, m_PreferFree, out last)) done++;
        }
        m_Status = done > 0
            ? "Snapped " + done + " piece(s)."
            : (last ?? "Nothing selected with a CorridorPiece component.");
        Repaint();
        SceneView.RepaintAll();
    }

    void CycleSelection()
    {
        string message = null;
        foreach (GameObject go in Selection.gameObjects)
        {
            CorridorPiece piece = go.GetComponent<CorridorPiece>();
            if (piece != null) CorridorSnap.CycleEntrySocket(piece, m_TidyRotation, out message);
        }
        m_Status = message ?? "Nothing selected with a CorridorPiece component.";
        Repaint();
        SceneView.RepaintAll();
    }

    void StraightenSelection()
    {
        foreach (GameObject go in Selection.gameObjects) CorridorSnap.Straighten(go.transform, m_PositionGrid);
        m_Status = "Straightened " + Selection.gameObjects.Length + " transform(s).";
        SceneView.RepaintAll();
    }

    void CloseAllGaps()
    {
        // Re-query between fixes: closing one joint can move a piece and change the rest.
        for (int guard = 0; guard < 200; guard++)
        {
            List<CorridorSnap.Joint> gaps = CorridorSnap.FindGaps(m_GapSearch);
            if (gaps.Count == 0) break;

            CorridorSnap.Joint worst = gaps[0];
            if (worst.B.Piece == null) break;
            CorridorSnap.AlignTo(worst.B.Piece, worst.B.Index, worst.A, m_TidyRotation);
        }

        m_Gaps = CorridorSnap.FindGaps(m_GapSearch);
        m_Status = m_Gaps.Count == 0 ? "All gaps closed." : m_Gaps.Count + " gap(s) could not be closed automatically.";
        SceneView.RepaintAll();
    }

    GameObject PlaceAt(CorridorSnap.SocketRef target)
    {
        if (m_Brush < 0 || m_Brush >= m_Kit.Count) return null;

        GameObject prefab = m_Kit[m_Brush];
        var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, target.Piece.gameObject.scene);
        if (instance == null) return null;

        Undo.RegisterCreatedObjectUndo(instance, "Place Corridor Piece");
        instance.transform.SetParent(target.Piece.transform.parent, true);

        CorridorPiece piece = instance.GetComponent<CorridorPiece>();
        if (piece == null || piece.SocketCount == 0)
        {
            m_Status = prefab.name + " has no baked sockets -- bake the kit first.";
            return instance;
        }

        int entry = Mathf.Clamp(m_EntrySocket, 0, piece.SocketCount - 1);
        CorridorSnap.AlignTo(piece, entry, target, m_TidyRotation);
        Selection.activeGameObject = instance;
        m_Status = "Placed " + prefab.name + " on " + target.Piece.name + ".";
        return instance;
    }

    static string ToProjectPath(string absolute)
    {
        absolute = absolute.Replace('\\', '/');
        string root = Application.dataPath.Replace('\\', '/');
        return absolute.StartsWith(root) ? "Assets" + absolute.Substring(root.Length) : absolute;
    }

    // ---------------------------------------------------------------- scene view

    void OnSceneGUI(SceneView view)
    {
        if (!m_DrawSockets) return;

        List<CorridorSnap.SocketRef> all = CorridorSnap.CollectSceneSockets();
        if (all.Count == 0) return;

        Camera cam = view.camera;
        Vector3 eye = cam != null ? cam.transform.position : Vector3.zero;
        float maxSq = m_DrawDistance * m_DrawDistance;
        bool canPlace = m_BuildMode && m_Brush >= 0 && m_Brush < m_Kit.Count;

        // Cull to what is on screen BEFORE the partner lookups. Those are O(n) each, so on a large
        // level running them against every socket in the scene on every repaint would crawl; the
        // candidate set is padded by the gap search so a partner just off screen still counts.
        float candidateRange = m_DrawDistance + m_GapSearch;
        float candidateSq = candidateRange * candidateRange;
        var nearby = new List<CorridorSnap.SocketRef>(all.Count);
        for (int i = 0; i < all.Count; i++)
            if ((all[i].Position - eye).sqrMagnitude <= candidateSq) nearby.Add(all[i]);

        for (int i = 0; i < nearby.Count; i++)
        {
            CorridorSnap.SocketRef socket = nearby[i];
            if ((socket.Position - eye).sqrMagnitude > maxSq) continue;

            CorridorSnap.SocketRef partner;
            bool joined = CorridorSnap.TryFindPartner(socket, nearby, CorridorSnap.JoinTolerance, null, out partner);

            Color color;
            if (joined)
            {
                color = JoinedColor;
            }
            else
            {
                CorridorSnap.SocketRef near;
                bool hasGap = CorridorSnap.TryFindPartner(socket, nearby, m_GapSearch, null, out near);
                if (hasGap)
                {
                    float d = Vector3.Distance(near.Position, socket.Position);
                    color = d > socket.Radius * 0.5f ? BadColor : GapColor;
                }
                else
                {
                    color = FreeColor;
                }
            }

            Handles.color = color;
            Quaternion look = Quaternion.LookRotation(socket.Direction, Vector3.up);
            Handles.CircleHandleCap(0, socket.Position, look, socket.Radius, EventType.Repaint);
            Handles.DrawLine(socket.Position, socket.Position + socket.Direction * socket.Radius * 0.6f);

            // A free opening is a build target: click it to place the palette piece there.
            if (!canPlace || joined) continue;

            float size = HandleUtility.GetHandleSize(socket.Position) * 0.18f;
            if (Handles.Button(socket.Position, look, size, size * 1.6f, Handles.SphereHandleCap))
            {
                PlaceAt(socket);
                Repaint();
            }
        }

        Handles.BeginGUI();
        var box = new Rect(10f, 10f, 250f, canPlace ? 58f : 44f);
        GUILayout.BeginArea(box, GUI.skin.box);
        GUILayout.Label("Corridor Snap", EditorStyles.miniBoldLabel);
        GUILayout.Label("green free   blue joined   yellow/red gap", EditorStyles.miniLabel);
        if (canPlace) GUILayout.Label("Click an opening to place " + m_Kit[m_Brush].name, EditorStyles.miniLabel);
        GUILayout.EndArea();
        Handles.EndGUI();
    }
}
