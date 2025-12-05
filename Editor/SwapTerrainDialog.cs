using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DivineDragon.MapTools
{
    public class SwapTerrainDialog : EditorWindow
    {
        private TerrainAssetAdapter terrain;
        private List<TerrainType> allTerrainTypes;      // All available types (for "To")
        private List<TerrainType> usedTerrainTypes;     // Types present in map (for "From")
        private TerrainType selectedFrom;
        private TerrainType selectedTo;
        private string fromFilter = "";
        private string toFilter = "";
        private Vector2 fromScrollPosition;
        private Vector2 toScrollPosition;

        public static void Show(TerrainAssetAdapter terrain, List<TerrainType> types)
        {
            if (terrain == null || types == null || types.Count == 0)
            {
                EditorUtility.DisplayDialog("Swap Terrains", "No terrain selected or no terrain types available.", "OK");
                return;
            }

            // Find which terrain types are actually used in the map
            var usedTids = new HashSet<string>(terrain.m_Terrains ?? Array.Empty<string>());
            var usedTypes = types.Where(t => usedTids.Contains(t.tid)).ToList();

            if (usedTypes.Count == 0)
            {
                EditorUtility.DisplayDialog("Swap Terrains", "No terrain types found in the current map.", "OK");
                return;
            }

            var window = GetWindow<SwapTerrainDialog>(true, "Swap Terrains", true);
            window.terrain = terrain;
            window.allTerrainTypes = types;
            window.usedTerrainTypes = usedTypes;
            window.selectedFrom = usedTypes[0];
            window.selectedTo = null;
            window.fromFilter = "";
            window.toFilter = "";
            window.minSize = new Vector2(500, 400);
            window.maxSize = new Vector2(600, 500);
            window.ShowUtility();
        }

        private void OnGUI()
        {
            if (terrain == null || usedTerrainTypes == null || usedTerrainTypes.Count == 0)
            {
                EditorGUILayout.HelpBox("No terrain data available.", MessageType.Warning);
                return;
            }

            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField("Replace all tiles of one terrain type with another.", EditorStyles.wordWrappedLabel);
            EditorGUILayout.Space(5);

            EditorGUILayout.BeginHorizontal();

            // FROM column
            EditorGUILayout.BeginVertical(GUILayout.Width(position.width / 2 - 10));
            EditorGUILayout.LabelField("From (in map)", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Filter", GUILayout.Width(35));
            fromFilter = EditorGUILayout.TextField(fromFilter ?? "");
            EditorGUILayout.EndHorizontal();

            string fromFilterLower = string.IsNullOrEmpty(fromFilter) ? null : fromFilter.ToLowerInvariant();

            fromScrollPosition = EditorGUILayout.BeginScrollView(fromScrollPosition, EditorStyles.helpBox, GUILayout.Height(250));
            foreach (var t in usedTerrainTypes)
            {
                if (!MatchesFilter(t, fromFilterLower)) continue;
                DrawTerrainRow(t, t == selectedFrom, () => selectedFrom = t);
            }
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();

            // TO column
            EditorGUILayout.BeginVertical(GUILayout.Width(position.width / 2 - 10));
            EditorGUILayout.LabelField("To (all)", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Filter", GUILayout.Width(35));
            toFilter = EditorGUILayout.TextField(toFilter ?? "");
            EditorGUILayout.EndHorizontal();

            string toFilterLower = string.IsNullOrEmpty(toFilter) ? null : toFilter.ToLowerInvariant();

            toScrollPosition = EditorGUILayout.BeginScrollView(toScrollPosition, EditorStyles.helpBox, GUILayout.Height(250));
            foreach (var t in allTerrainTypes)
            {
                if (!MatchesFilter(t, toFilterLower)) continue;
                DrawTerrainRow(t, t == selectedTo, () => selectedTo = t);
            }
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(10);

            // Summary
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Selected:", GUILayout.Width(55));
            string fromLabel = selectedFrom != null ? GetDisplayName(selectedFrom) : "(none)";
            string toLabel = selectedTo != null ? GetDisplayName(selectedTo) : "(none)";
            EditorGUILayout.LabelField($"{fromLabel}  →  {toLabel}", EditorStyles.wordWrappedLabel);
            EditorGUILayout.EndHorizontal();

            // Warning if same selection
            if (selectedFrom != null && selectedTo != null && selectedFrom.tid == selectedTo.tid)
            {
                EditorGUILayout.HelpBox("Source and target terrain types are the same.", MessageType.Warning);
            }

            EditorGUILayout.Space(5);

            // Buttons
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Cancel", GUILayout.Width(80)))
            {
                Close();
            }

            bool canSwap = selectedFrom != null && selectedTo != null && selectedFrom.tid != selectedTo.tid;
            EditorGUI.BeginDisabledGroup(!canSwap);
            if (GUILayout.Button("Swap", GUILayout.Width(80)))
            {
                PerformSwap(selectedFrom.tid, selectedTo.tid);
                Close();
            }
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.EndHorizontal();
        }

        private bool MatchesFilter(TerrainType t, string filterLower)
        {
            if (filterLower == null) return true;
            bool matchesTid = t.tid.ToLowerInvariant().Contains(filterLower);
            bool matchesName = !string.IsNullOrEmpty(t.name) && t.name.ToLowerInvariant().Contains(filterLower);
            return matchesTid || matchesName;
        }

        private string GetDisplayName(TerrainType t)
        {
            return string.IsNullOrEmpty(t.name) ? t.tid : $"{t.name} ({t.tid})";
        }

        private void DrawTerrainRow(TerrainType t, bool isSelected, System.Action onSelect)
        {
            Color bgColor = isSelected ? new Color(0.3f, 0.5f, 0.8f, 0.5f) : Color.clear;

            Rect rowRect = EditorGUILayout.BeginHorizontal();
            if (isSelected)
            {
                EditorGUI.DrawRect(rowRect, bgColor);
            }

            // Color chip
            DrawColorChip(t.color);

            // Label
            GUIStyle labelStyle = isSelected ? EditorStyles.boldLabel : EditorStyles.label;
            if (GUILayout.Button(GetDisplayName(t), labelStyle))
            {
                onSelect?.Invoke();
            }

            EditorGUILayout.EndHorizontal();

            // Handle click on the entire row
            if (Event.current.type == EventType.MouseDown && rowRect.Contains(Event.current.mousePosition))
            {
                onSelect?.Invoke();
                Event.current.Use();
                Repaint();
            }
        }

        private void DrawColorChip(Color color)
        {
            Rect colorRect = GUILayoutUtility.GetRect(18, 16, GUILayout.Width(18));
            EditorGUI.DrawRect(colorRect, color);
            // Border
            EditorGUI.DrawRect(new Rect(colorRect.x, colorRect.y, colorRect.width, 1), Color.black);
            EditorGUI.DrawRect(new Rect(colorRect.x, colorRect.yMax - 1, colorRect.width, 1), Color.black);
            EditorGUI.DrawRect(new Rect(colorRect.x, colorRect.y, 1, colorRect.height), Color.black);
            EditorGUI.DrawRect(new Rect(colorRect.xMax - 1, colorRect.y, 1, colorRect.height), Color.black);
        }

        private void PerformSwap(string fromTid, string toTid)
        {
            if (terrain == null || terrain.m_Terrains == null) return;

            // Register undo
            if (terrain.Asset != null)
            {
                Undo.RegisterCompleteObjectUndo(terrain.Asset, $"Swap Terrain: {fromTid} → {toTid}");
            }

            var tiles = terrain.m_Terrains;
            int count = 0;
            for (int i = 0; i < tiles.Length; i++)
            {
                if (tiles[i] == fromTid)
                {
                    tiles[i] = toTid;
                    count++;
                }
            }

            if (count > 0)
            {
                // Mark dirty
                if (terrain.Asset != null)
                {
                    EditorUtility.SetDirty(terrain.Asset);
                }

                // Invalidate caches via the paint tool
                TerrainPaintToolWindow.NotifyTerrainDataChanged(terrain);

                Debug.Log($"[Swap Terrain] Replaced {count} tiles: {fromTid} → {toTid}");
            }
            else
            {
                Debug.Log($"[Swap Terrain] No tiles found with terrain type: {fromTid}");
            }
        }
    }
}
