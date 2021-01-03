using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

[CustomEditor(typeof(TextController))]
[CanEditMultipleObjects]
public class TextControllerEditor : Editor
{

    TextController m_txtController;
    private void OnEnable()
    {
        m_txtController = (TextController)target;
    }
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        serializedObject.Update();

        if (GUILayout.Button("Get Text File"))
        {
            ListImport();
        }
    }
        public void ListImport()
    {
        string path = EditorUtility.OpenFilePanel("Find List CSV", "", "txt");

        m_txtController.m_Text = System.IO.File.ReadAllText(path);
    }
}
