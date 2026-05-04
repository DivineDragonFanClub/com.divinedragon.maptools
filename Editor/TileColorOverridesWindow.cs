using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace DivineDragon.MapTools
{
    /// <summary>
    /// Editor for the project's TileColorOverrides asset. Lists every terrain known to
    /// TerrainDefinitions and lets the user assign an editor-only color override per tid.
    /// </summary>
    public class TileColorOverridesWindow : EditorWindow
    {
        private const float SwatchSize = 18f;
        private const float RowHeight = 22f;

        private string searchText = string.Empty;
        private bool showOnlyOverridden;
        private Vector2 scroll;

        [MenuItem("Map Tools/Tile Color Overrides")]
        public static void ShowWindow()
        {
            var window = GetWindow<TileColorOverridesWindow>();
            window.titleContent = new GUIContent("Tile Color Overrides");
            window.minSize = new Vector2(420, 320);
            window.Show();
        }

        private void OnGUI()
        {
            DrawToolbar();
            DrawHeader();

            IReadOnlyList<TerrainType> terrains = TerrainDefinitions.GetAllTerrains();
            if (terrains.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No terrains loaded. Extract terrain.xml.bundle so the editor has tile metadata.",
                    MessageType.Info);
                return;
            }

            DrawList(terrains);
            DrawFooter();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            GUI.SetNextControlName("TileColorOverridesSearch");
            string newSearch = GUILayout.TextField(searchText, EditorStyles.toolbarSearchField, GUILayout.MinWidth(120));
            if (newSearch != searchText)
            {
                searchText = newSearch;
            }

            bool newOnlyOverridden = GUILayout.Toggle(showOnlyOverridden, "Overridden only", EditorStyles.toolbarButton, GUILayout.Width(120));
            if (newOnlyOverridden != showOnlyOverridden)
            {
                showOnlyOverridden = newOnlyOverridden;
            }

            GUILayout.FlexibleSpace();

            using (new EditorGUI.DisabledScope(TileColorOverridesProvider.Count == 0))
            {
                if (GUILayout.Button("Reset All", EditorStyles.toolbarButton, GUILayout.Width(70)))
                {
                    if (EditorUtility.DisplayDialog(
                            "Reset all tile color overrides?",
                            "This clears every editor-only color override. The asset stays in place but its entries are removed. Continue?",
                            "Reset",
                            "Cancel"))
                    {
                        TileColorOverridesProvider.ClearAll();
                    }
                }
            }

            if (GUILayout.Button("Ping Asset", EditorStyles.toolbarButton, GUILayout.Width(80)))
            {
                TileColorOverrides asset = TileColorOverridesProvider.Asset;
                if (asset != null)
                {
                    EditorGUIUtility.PingObject(asset);
                    Selection.activeObject = asset;
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawHeader()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            EditorGUILayout.LabelField("TID", EditorStyles.miniBoldLabel, GUILayout.Width(140));
            EditorGUILayout.LabelField("Name", EditorStyles.miniBoldLabel, GUILayout.MinWidth(120));
            EditorGUILayout.LabelField("Original", EditorStyles.miniBoldLabel, GUILayout.Width(60));
            EditorGUILayout.LabelField("Override", EditorStyles.miniBoldLabel, GUILayout.Width(80));
            EditorGUILayout.LabelField(string.Empty, GUILayout.Width(60));
            EditorGUILayout.EndHorizontal();
        }

        private void DrawList(IReadOnlyList<TerrainType> terrains)
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);

            string filter = string.IsNullOrEmpty(searchText) ? null : searchText.Trim();
            int shownCount = 0;

            foreach (TerrainType terrain in terrains)
            {
                if (string.IsNullOrEmpty(terrain.tid)) continue;

                bool isOverridden = TileColorOverridesProvider.TryGetOverride(terrain.tid, out Color overrideColor);
                if (showOnlyOverridden && !isOverridden) continue;

                if (filter != null)
                {
                    string name = TerrainDefinitions.GetTerrainName(terrain.tid) ?? string.Empty;
                    if (!ContainsIgnoreCase(terrain.tid, filter)
                        && !ContainsIgnoreCase(name, filter)
                        && !ContainsIgnoreCase(terrain.name, filter))
                    {
                        continue;
                    }
                }

                DrawRow(terrain, isOverridden, overrideColor);
                shownCount++;
            }

            if (shownCount == 0)
            {
                EditorGUILayout.LabelField(
                    showOnlyOverridden ? "No overrides set." : "No terrains match the filter.",
                    EditorStyles.centeredGreyMiniLabel);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawRow(TerrainType terrain, bool isOverridden, Color overrideColor)
        {
            Rect rowRect = EditorGUILayout.BeginHorizontal(GUILayout.Height(RowHeight));

            EditorGUILayout.LabelField(terrain.tid, GUILayout.Width(140));

            // GetTerrainName returns the localized string when MSBT data is loaded, or falls
            // back to the MTID key (e.g. "MTID_Ground") otherwise — either way it's readable.
            string display = TerrainDefinitions.GetTerrainName(terrain.tid);
            EditorGUILayout.LabelField(display ?? terrain.name ?? string.Empty, GUILayout.MinWidth(120));

            // Original swatch (read-only)
            Rect originalRect = GUILayoutUtility.GetRect(SwatchSize, SwatchSize, GUILayout.Width(60));
            DrawSwatch(originalRect, terrain.color);

            // Override field. Alpha is hidden so a stray drag can't make tiles render translucent.
            Color baseColor = isOverridden ? overrideColor : terrain.color;
            Color newOverride = EditorGUILayout.ColorField(GUIContent.none, baseColor, false, false, false, GUILayout.Width(80));
            newOverride.a = 1f;
            if (newOverride != baseColor)
            {
                TileColorOverridesProvider.SetOverride(terrain.tid, newOverride);
            }

            using (new EditorGUI.DisabledScope(!isOverridden))
            {
                if (GUILayout.Button("Reset", EditorStyles.miniButton, GUILayout.Width(60)))
                {
                    TileColorOverridesProvider.ClearOverride(terrain.tid);
                }
            }

            EditorGUILayout.EndHorizontal();

            if (isOverridden && Event.current.type == EventType.Repaint)
            {
                // Subtle bar so overridden rows stand out at a glance.
                Rect markRect = new Rect(rowRect.x, rowRect.y, 2f, rowRect.height);
                EditorGUI.DrawRect(markRect, new Color(1f, 0.85f, 0.2f, 0.9f));
            }
        }

        private static void DrawSwatch(Rect rect, Color color)
        {
            float yOffset = (rect.height - SwatchSize) * 0.5f;
            Rect chip = new Rect(rect.x, rect.y + yOffset, SwatchSize, SwatchSize);
            EditorGUI.DrawRect(chip, color);
            // Border
            EditorGUI.DrawRect(new Rect(chip.x, chip.y, chip.width, 1), Color.black);
            EditorGUI.DrawRect(new Rect(chip.x, chip.yMax - 1, chip.width, 1), Color.black);
            EditorGUI.DrawRect(new Rect(chip.x, chip.y, 1, chip.height), Color.black);
            EditorGUI.DrawRect(new Rect(chip.xMax - 1, chip.y, 1, chip.height), Color.black);
        }

        private void DrawFooter()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            EditorGUILayout.LabelField(
                $"{TileColorOverridesProvider.Count} override(s) active. Editor-only — never affects shipped game data.",
                EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
        }

        private static bool ContainsIgnoreCase(string source, string value)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(value)) return false;
            return source.IndexOf(value, System.StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
