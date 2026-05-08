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

                    string[] languages = TerrainLocalizer.AvailableLanguages;
                    if (languages.Length > 0)
                    {
                        int currentIndex = System.Array.IndexOf(languages, TerrainLocalizer.CurrentLanguage);
                        if (currentIndex < 0) currentIndex = 0;

                        int newIndex = EditorGUILayout.Popup("Language", currentIndex, languages);
                        if (newIndex != currentIndex)
                        {
                            TerrainLocalizer.SetLanguage(languages[newIndex]);
                            SceneView.RepaintAll();
                        }
                    }
                    else
                    {
                        EditorGUILayout.HelpBox(
                            "No localization files found. Extract GameData.txt using the Terrain Paint Tool.",
                            MessageType.Info);
                    }
                },
                keywords = new[] { "Language", "Localization", "Map Tools", "Terrain" }
            };
        }
    }
}
