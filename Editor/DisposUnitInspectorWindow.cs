using UnityEditor;
using UnityEngine;

namespace DivineDragon.MapTools
{
    public class DisposUnitInspectorWindow : EditorWindow
    {
        [MenuItem("Window/Dispos Unit Inspector")]
        public static void ShowWindow()
        {
            var w = GetWindow<DisposUnitInspectorWindow>("Dispos Unit");
            w.minSize = new Vector2(320, 240);
        }

        private void OnEnable()
        {
            DisposToolWindow.EnsureMainWindow();
        }

        private void OnGUI()
        {
            var main = DisposToolWindow.Instance;
            if (main == null)
            {
                EditorGUILayout.HelpBox("Open Dispos Tool to use the unit inspector.", MessageType.Info);
                if (GUILayout.Button("Open Dispos Tool")) DisposToolWindow.EnsureMainWindow();
                return;
            }

            main.DrawUnitInspectorStandalone();
        }
    }
}

