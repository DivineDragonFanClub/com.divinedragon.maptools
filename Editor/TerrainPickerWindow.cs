using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace DivineDragon.MapTools
{
    public class TerrainPickerWindow : EditorWindow
    {
        private Action<TerrainAssetAdapter> onSelected;
        private TerrainAssetAdapter excludeTerrain;
        private List<TerrainEntry> terrainEntries = new List<TerrainEntry>();
        private string searchFilter = "";
        private Vector2 scrollPosition;
        private int selectedIndex = -1;

        // Preview
        private Texture2D previewTexture;
        private TerrainAssetAdapter previewTerrain;
        private const int PREVIEW_SIZE = 128;

        private struct TerrainEntry
        {
            public string path;
            public string name;
            public int width;
            public int height;
            public TerrainAssetAdapter adapter;
        }

        public static void Show(Action<TerrainAssetAdapter> callback, TerrainAssetAdapter exclude = null)
        {
            var window = CreateInstance<TerrainPickerWindow>();
            window.onSelected = callback;
            window.excludeTerrain = exclude;
            window.titleContent = new GUIContent("Select Terrain");
            window.minSize = new Vector2(450, 400);
            window.LoadTerrainList();
            window.ShowUtility();
        }

        private void OnDisable()
        {
            if (previewTexture != null)
            {
                DestroyImmediate(previewTexture);
                previewTexture = null;
            }
        }

        private void LoadTerrainList()
        {
            terrainEntries.Clear();

            string[] guids = AssetDatabase.FindAssets("t:MapTerrain", new[] { "Assets" });
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var adapter = TerrainAssetAdapter.Load(path);
                if (adapter?.Asset != null)
                {
                    // Skip excluded terrain (current map)
                    if (excludeTerrain != null && adapter.Asset == excludeTerrain.Asset)
                        continue;

                    terrainEntries.Add(new TerrainEntry
                    {
                        path = path,
                        name = adapter.Name,
                        width = adapter.m_Width,
                        height = adapter.m_Height,
                        adapter = adapter
                    });
                }
            }

            // Sort by name
            terrainEntries.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
        }

        private void OnGUI()
        {
            EditorGUILayout.BeginHorizontal();

            // Left side: list
            EditorGUILayout.BeginVertical(GUILayout.Width(position.width - PREVIEW_SIZE - 20));
            DrawList();
            EditorGUILayout.EndVertical();

            // Right side: preview
            EditorGUILayout.BeginVertical(GUILayout.Width(PREVIEW_SIZE + 10));
            DrawPreview();
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();

            // Bottom buttons
            DrawBottomBar();

            // Handle keyboard
            HandleKeyboard();
        }

        private void DrawList()
        {
            // Search bar
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUI.SetNextControlName("SearchField");
            searchFilter = EditorGUILayout.TextField(searchFilter, EditorStyles.toolbarSearchField);
            if (GUILayout.Button("", GUI.skin.FindStyle("ToolbarSearchCancelButton") ?? EditorStyles.toolbarButton, GUILayout.Width(18)))
            {
                searchFilter = "";
                GUI.FocusControl(null);
            }
            EditorGUILayout.EndHorizontal();

            // Focus search on open
            if (Event.current.type == EventType.Repaint && string.IsNullOrEmpty(GUI.GetNameOfFocusedControl()))
            {
                EditorGUI.FocusTextInControl("SearchField");
            }

            // Terrain list
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

            int visibleIndex = 0;
            for (int i = 0; i < terrainEntries.Count; i++)
            {
                var entry = terrainEntries[i];

                // Filter
                if (!string.IsNullOrEmpty(searchFilter) &&
                    entry.name.IndexOf(searchFilter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                bool isSelected = (selectedIndex == i);
                Rect rowRect = EditorGUILayout.BeginHorizontal(isSelected ? "SelectionRect" : GUIStyle.none, GUILayout.Height(24));

                // Alternating background
                if (!isSelected && visibleIndex % 2 == 1)
                {
                    EditorGUI.DrawRect(rowRect, new Color(0, 0, 0, 0.05f));
                }

                // Name
                GUILayout.Label(entry.name, EditorStyles.label, GUILayout.ExpandWidth(true));

                // Dimensions
                GUILayout.Label($"{entry.width}×{entry.height}", EditorStyles.miniLabel, GUILayout.Width(60));

                EditorGUILayout.EndHorizontal();

                // Handle click
                if (Event.current.type == EventType.MouseDown && rowRect.Contains(Event.current.mousePosition))
                {
                    selectedIndex = i;
                    UpdatePreview(entry.adapter);
                    Event.current.Use();
                    Repaint();

                    // Double-click to select
                    if (Event.current.clickCount >= 2)
                    {
                        SelectAndClose(entry.adapter);
                    }
                }

                visibleIndex++;
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawPreview()
        {
            GUILayout.Label("Preview", EditorStyles.boldLabel);

            if (previewTexture != null)
            {
                // Draw preview with aspect ratio
                Rect previewRect = GUILayoutUtility.GetRect(PREVIEW_SIZE, PREVIEW_SIZE, GUILayout.ExpandWidth(false));
                EditorGUI.DrawRect(previewRect, new Color(0.15f, 0.15f, 0.15f, 1f));
                GUI.DrawTexture(previewRect, previewTexture, ScaleMode.ScaleToFit);

                // Show dimensions
                if (previewTerrain != null)
                {
                    EditorGUILayout.LabelField($"{previewTerrain.m_Width} × {previewTerrain.m_Height}", EditorStyles.centeredGreyMiniLabel);
                }
            }
            else
            {
                Rect previewRect = GUILayoutUtility.GetRect(PREVIEW_SIZE, PREVIEW_SIZE, GUILayout.ExpandWidth(false));
                EditorGUI.DrawRect(previewRect, new Color(0.15f, 0.15f, 0.15f, 1f));
                GUI.Label(previewRect, "Select a terrain", EditorStyles.centeredGreyMiniLabel);
            }

            GUILayout.FlexibleSpace();
        }

        private void DrawBottomBar()
        {
            EditorGUILayout.BeginHorizontal();

            GUILayout.Label($"{terrainEntries.Count} terrain(s)", EditorStyles.miniLabel);

            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Cancel", GUILayout.Width(80)))
            {
                Close();
            }

            EditorGUI.BeginDisabledGroup(selectedIndex < 0 || selectedIndex >= terrainEntries.Count);
            if (GUILayout.Button("Select", GUILayout.Width(80)))
            {
                if (selectedIndex >= 0 && selectedIndex < terrainEntries.Count)
                {
                    SelectAndClose(terrainEntries[selectedIndex].adapter);
                }
            }
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.EndHorizontal();
            GUILayout.Space(8);
        }

        private void UpdatePreview(TerrainAssetAdapter terrain)
        {
            if (terrain == null || terrain.Asset == previewTerrain?.Asset)
                return;

            previewTerrain = terrain;

            // Clean up old texture
            if (previewTexture != null)
            {
                DestroyImmediate(previewTexture);
            }

            // Generate preview
            var virtualGrid = TerrainVirtualGridCache.GetGrid(terrain);
            if (virtualGrid == null)
            {
                previewTexture = null;
                return;
            }

            int width = terrain.m_Width;
            int height = terrain.m_Height;

            previewTexture = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };

            Color[] pixels = new Color[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    string tid = virtualGrid.GetTerrainId(x, y);
                    Color color;
                    if (string.IsNullOrEmpty(tid) || TerrainVirtualGridCache.IsEmptyTerrain(tid))
                    {
                        color = new Color(0.2f, 0.2f, 0.2f, 1f);
                    }
                    else
                    {
                        color = TerrainDefinitions.GetColorOrFallback(tid);
                    }

                    // No flip - use same orientation as minimap with flipX=false, flipY=false
                    pixels[y * width + x] = color;
                }
            }

            previewTexture.SetPixels(pixels);
            previewTexture.Apply();
        }

        private void HandleKeyboard()
        {
            Event e = Event.current;
            if (e.type != EventType.KeyDown) return;

            if (e.keyCode == KeyCode.Escape)
            {
                Close();
                e.Use();
            }
            else if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
            {
                if (selectedIndex >= 0 && selectedIndex < terrainEntries.Count)
                {
                    SelectAndClose(terrainEntries[selectedIndex].adapter);
                }
                e.Use();
            }
            else if (e.keyCode == KeyCode.DownArrow)
            {
                MoveSelection(1);
                e.Use();
            }
            else if (e.keyCode == KeyCode.UpArrow)
            {
                MoveSelection(-1);
                e.Use();
            }
        }

        private void MoveSelection(int delta)
        {
            // Build filtered list indices
            List<int> visibleIndices = new List<int>();
            for (int i = 0; i < terrainEntries.Count; i++)
            {
                if (string.IsNullOrEmpty(searchFilter) ||
                    terrainEntries[i].name.IndexOf(searchFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    visibleIndices.Add(i);
                }
            }

            if (visibleIndices.Count == 0) return;

            int currentVisibleIndex = visibleIndices.IndexOf(selectedIndex);
            if (currentVisibleIndex < 0)
            {
                selectedIndex = visibleIndices[0];
            }
            else
            {
                int newVisibleIndex = Mathf.Clamp(currentVisibleIndex + delta, 0, visibleIndices.Count - 1);
                selectedIndex = visibleIndices[newVisibleIndex];
            }

            // Update preview when navigating with keyboard
            if (selectedIndex >= 0 && selectedIndex < terrainEntries.Count)
            {
                UpdatePreview(terrainEntries[selectedIndex].adapter);
            }

            Repaint();
        }

        private void SelectAndClose(TerrainAssetAdapter adapter)
        {
            onSelected?.Invoke(adapter);
            Close();
        }
    }
}
