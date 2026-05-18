using System;
using DivineDragon.Msbt.Editor;
using UnityEditor;
using UnityEngine;

namespace DivineDragon.MapTools
{
    public static class MapToolsSettingsProvider
    {
        [SettingsProvider]
        public static SettingsProvider CreateMapToolsSettingsProvider()
        {
            return new SettingsProvider("Project/Map Tools", SettingsScope.Project)
            {
                label = "Map Tools",
                guiHandler = (searchContext) =>
                {
                    EditorGUILayout.Space(10);
                    EditorGUILayout.LabelField("Localization", EditorStyles.boldLabel);

                    Language[] languages = MsbtProvider.AvailableLanguages;
                    if (languages.Length > 0)
                    {
                        string[] codes = new string[languages.Length];
                        for (int i = 0; i < languages.Length; i++) codes[i] = languages[i].Code;

                        int currentIndex = Array.IndexOf(codes, MsbtProvider.CurrentLanguage.Code);
                        if (currentIndex < 0) currentIndex = 0;

                        int newIndex = EditorGUILayout.Popup("Language", currentIndex, codes);
                        if (newIndex != currentIndex)
                        {
                            MsbtProvider.SetLanguage(languages[newIndex]);
                            SceneView.RepaintAll();
                        }
                    }
                    else
                    {
                        EditorGUILayout.HelpBox(
                            "No localization files found. Open Set up Map Tools to extract them.",
                            MessageType.Info);
                        if (GUILayout.Button("Open Setup"))
                        {
                            MapToolsSetupWindow.Open();
                        }
                    }
                },
                keywords = new[] { "Language", "Localization", "Map Tools", "Terrain" }
            };
        }
    }
}
