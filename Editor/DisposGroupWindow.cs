using UnityEditor;
using UnityEngine;

namespace DivineDragon.MapTools
{
    public class DisposGroupWindow : EditorWindow
    {
        [MenuItem("Window/Dispos Group Inspector")]
        public static void ShowWindow()
        {
            var w = GetWindow<DisposGroupWindow>("Dispos Groups");
            w.minSize = new Vector2(280, 200);
        }

        private void OnEnable()
        {
            // Ensure the main tool exists so we have shared state/renderer/undo.
            DisposToolWindow.EnsureMainWindow();
        }

        private void OnGUI()
        {
            var main = DisposToolWindow.Instance;
            if (main == null)
            {
                EditorGUILayout.HelpBox("Open Dispos Tool to use the group inspector.", MessageType.Info);
                if (GUILayout.Button("Open Dispos Tool")) DisposToolWindow.EnsureMainWindow();
                return;
            }

            main.DrawGroupsPanelStandalone();
        }
    }
}

