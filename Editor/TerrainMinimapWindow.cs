using System;
using UnityEditor;
using UnityEngine;

namespace DivineDragon.MapTools
{
    public class TerrainMinimapWindow : EditorWindow
    {
        // Keep in sync with TerrainPaintToolWindow.TILE_SIZE (world units per tile)
        private const float TILE_SIZE = 5f;
        private const float MIN_CAMERA_DISTANCE = 1f;
        private const float MAX_CAMERA_DISTANCE = 5000f;
        private const float MIN_ORTHO_SIZE = 0.5f;
        private const float MAX_ORTHO_SIZE = 5000f;
        private const float FRUSTUM_INSET_RATIO = 0.02f; // shrink drawn frustum slightly to better match viewport

        // State
        private TerrainAssetAdapter terrain;
        private TerrainVirtualGrid virtualGrid;
        private bool isCurrentMapMode = true;
        private Texture2D minimapTexture;
        private bool textureDirty = true;
        private int lastTextureWidth;
        private int lastTextureHeight;

        // UI state
        private bool flipX = true; // default to match scene orientation
        private bool flipY = true; // default to match scene orientation
        private Rect minimapDrawRect;
        private Vector2Int hoveredTile = new Vector2Int(-1, -1);
        private string hoveredTerrainTid;
        private string hoveredTerrainName;
        private bool isDraggingMinimap;
        private Vector2 lastDragTile = new Vector2(float.MinValue, float.MinValue);
        private bool naturalScroll = false;

        // Reference map browsing
        private string referenceMapPath;

        private const string PREFS_NATURAL_SCROLL = "TerrainMinimap_NaturalScroll";

        [MenuItem("Window/Divine Dragon/Terrain Minimap")]
        public static void ShowWindow()
        {
            // Create a new instance each time (allows multiple windows)
            var window = CreateInstance<TerrainMinimapWindow>();
            window.titleContent = new GUIContent("Terrain Minimap");
            window.minSize = new Vector2(200, 200);
            window.Show();
        }

        /// <summary>
        /// Opens a minimap window pre-loaded with a specific terrain asset.
        /// </summary>
        public static TerrainMinimapWindow ShowForTerrain(TerrainAssetAdapter terrain, bool asReference = false)
        {
            var window = CreateInstance<TerrainMinimapWindow>();
            window.terrain = terrain;
            window.isCurrentMapMode = !asReference;
            window.textureDirty = true;
            window.UpdateWindowTitle();
            window.minSize = new Vector2(200, 200);
            window.Show();
            return window;
        }

        private void OnEnable()
        {
            SceneView.duringSceneGui += OnSceneGUI;
            EditorApplication.update += OnEditorUpdate;
            TerrainPaintToolWindow.OnTerrainDataChanged += OnTerrainDataChanged;
            naturalScroll = EditorPrefs.GetBool(PREFS_NATURAL_SCROLL, false);
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            EditorApplication.update -= OnEditorUpdate;
            TerrainPaintToolWindow.OnTerrainDataChanged -= OnTerrainDataChanged;
            CleanupTexture();
        }

        private void OnTerrainDataChanged(TerrainAssetAdapter changedTerrain)
        {
            if (isCurrentMapMode && changedTerrain == terrain)
            {
                textureDirty = true;
                Repaint();
            }
        }

        private void OnEditorUpdate()
        {
            // Repaint to keep frustum up to date
            if (isCurrentMapMode && terrain != null)
            {
                Repaint();
            }
        }

        private void OnSceneGUI(SceneView sceneView)
        {
            // Just trigger repaint when scene view changes
            if (isCurrentMapMode && terrain != null)
            {
                Repaint();
            }
        }

        private void OnGUI()
        {
            DrawToolbar();
            DrawMapInfo();
            DrawMinimap();
            DrawStatusBar();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            // Mode toggle
            EditorGUI.BeginChangeCheck();
            bool newCurrentMode = GUILayout.Toggle(isCurrentMapMode, "Current Map", EditorStyles.toolbarButton, GUILayout.Width(85));
            bool newRefMode = GUILayout.Toggle(!isCurrentMapMode, "Reference", EditorStyles.toolbarButton, GUILayout.Width(70));

            if (newCurrentMode != isCurrentMapMode || newRefMode == isCurrentMapMode)
            {
                isCurrentMapMode = newCurrentMode || !newRefMode;
                if (isCurrentMapMode)
                {
                    SyncWithPaintTool();
                }
                UpdateWindowTitle();
            }

            GUILayout.FlexibleSpace();

            if (!isCurrentMapMode)
            {
                if (GUILayout.Button("Browse...", EditorStyles.toolbarButton, GUILayout.Width(60)))
                {
                    BrowseForMap();
                }
            }
            else
            {
                if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(55)))
                {
                    SyncWithPaintTool();
                }
            }

            GUILayout.Space(6);
            bool newNaturalScroll = GUILayout.Toggle(naturalScroll, "Natural Scroll", EditorStyles.toolbarButton, GUILayout.Width(100));
            if (newNaturalScroll != naturalScroll)
            {
                naturalScroll = newNaturalScroll;
                EditorPrefs.SetBool(PREFS_NATURAL_SCROLL, naturalScroll);
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawMapInfo()
        {
            if (terrain == null)
            {
                EditorGUILayout.HelpBox(
                    isCurrentMapMode
                        ? "No terrain selected in paint tool."
                        : "Click 'Browse...' to load a reference map.",
                    MessageType.Info);
                return;
            }

            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            string name = terrain.Name ?? "Unknown";
            EditorGUILayout.LabelField($"{name} ({terrain.m_Width}×{terrain.m_Height})", EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
        }

        private void DrawMinimap()
        {
            if (terrain == null)
            {
                GUILayout.FlexibleSpace();
                return;
            }

            // Sync with paint tool if in current map mode
            if (isCurrentMapMode)
            {
                SyncWithPaintTool();
            }

            // Rebuild texture if needed
            if (textureDirty || minimapTexture == null ||
                lastTextureWidth != terrain.m_Width || lastTextureHeight != terrain.m_Height)
            {
                RebuildMinimapTexture();
            }

            if (minimapTexture == null)
            {
                GUILayout.FlexibleSpace();
                return;
            }

            // Calculate available space
            Rect available = GUILayoutUtility.GetRect(
                GUIContent.none,
                GUIStyle.none,
                GUILayout.ExpandWidth(true),
                GUILayout.ExpandHeight(true)
            );

            if (available.width < 10 || available.height < 10)
                return;

            // Maintain aspect ratio while fitting to available space
            float mapAspect = (float)terrain.m_Width / terrain.m_Height;
            float areaAspect = available.width / available.height;

            Rect drawRect;
            if (mapAspect > areaAspect)
            {
                // Map is wider - fit to width
                float height = available.width / mapAspect;
                float yOffset = (available.height - height) / 2f;
                drawRect = new Rect(available.x, available.y + yOffset, available.width, height);
            }
            else
            {
                // Map is taller - fit to height
                float width = available.height * mapAspect;
                float xOffset = (available.width - width) / 2f;
                drawRect = new Rect(available.x + xOffset, available.y, width, available.height);
            }

            minimapDrawRect = drawRect;

            // Draw background
            EditorGUI.DrawRect(drawRect, new Color(0.15f, 0.15f, 0.15f, 1f));

            // Draw minimap texture
            GUI.DrawTexture(drawRect, minimapTexture, ScaleMode.StretchToFill);

            // Draw view frustum if current map mode
            if (isCurrentMapMode)
            {
                DrawViewFrustum(drawRect);
            }

            // Handle input
            HandleMinimapInput(drawRect);
        }

        private void DrawViewFrustum(Rect minimapRect)
        {
            var sv = SceneView.lastActiveSceneView;
            if (sv == null || sv.camera == null || terrain == null)
                return;

            Camera cam = sv.camera;
            float terrainY = 0f; // Assume terrain is at Y=0

            // Get the four corners of what's visible on the terrain plane
            Vector2[] corners = new Vector2[4];
            bool allValid = true;

            // Viewport corners: bottom-left, bottom-right, top-right, top-left
            Vector3[] viewportCorners = new Vector3[]
            {
                new Vector3(0, 0, 0),
                new Vector3(1, 0, 0),
                new Vector3(1, 1, 0),
                new Vector3(0, 1, 0)
            };

            for (int i = 0; i < 4; i++)
            {
                Ray ray = cam.ViewportPointToRay(viewportCorners[i]);
                Vector3? hit = RayPlaneIntersect(ray, terrainY);
                if (hit.HasValue)
                {
                    // Convert world pos to minimap pos (relative to minimapRect origin for clipping)
                    corners[i] = WorldToMinimapLocal(hit.Value, minimapRect);
                }
                else
                {
                    allValid = false;
                    break;
                }
            }

            // Use clipping to restrict drawing to minimap bounds
            GUI.BeginClip(minimapRect);

            if (!allValid)
            {
                // Fallback: just show pivot as a crosshair
                Vector2 pivotLocal = WorldToMinimapLocal(sv.pivot, minimapRect);
                DrawCrosshairLocal(pivotLocal, minimapRect.size);
            }
            else
            {
                // Draw the quadrilateral outline
                Color frustumColor = new Color(1f, 1f, 0f, 0.9f);
                float thickness = 2f;

                // Slightly inset to better match actual viewport cropping
                Vector2 center = (corners[0] + corners[1] + corners[2] + corners[3]) * 0.25f;
                float insetFactor = 1f - FRUSTUM_INSET_RATIO;
                for (int i = 0; i < 4; i++)
                {
                    corners[i] = center + (corners[i] - center) * insetFactor;
                }

                DrawLineLocal(corners[0], corners[1], frustumColor, thickness);
                DrawLineLocal(corners[1], corners[2], frustumColor, thickness);
                DrawLineLocal(corners[2], corners[3], frustumColor, thickness);
                DrawLineLocal(corners[3], corners[0], frustumColor, thickness);
            }

            GUI.EndClip();
        }

        private Vector3? RayPlaneIntersect(Ray ray, float planeY)
        {
            // Avoid division by zero / parallel ray
            if (Mathf.Abs(ray.direction.y) < 0.0001f)
                return null;

            float t = (planeY - ray.origin.y) / ray.direction.y;
            if (t < 0)
                return null; // Ray points away from plane

            return ray.origin + ray.direction * t;
        }

        /// <summary>
        /// Convert world position to local coordinates within the minimap rect (for use with GUI.BeginClip)
        /// </summary>
        private Vector2 WorldToMinimapLocal(Vector3 worldPos, Rect minimapRect)
        {
            // Use the same world offset as the paint tool so the frustum overlays line up with the rendered terrain
            Vector3 offset = TerrainPaintToolWindow.GetWorldOffset();

            float originX = terrain.m_X + offset.x;
            float originZ = terrain.m_Z + offset.z;

            float tileX = (worldPos.x - originX) / TILE_SIZE;
            float tileZ = (worldPos.z - originZ) / TILE_SIZE;

            float scaleX = minimapRect.width / terrain.m_Width;
            float scaleY = minimapRect.height / terrain.m_Height;

            // Return local coordinates (0,0 is top-left of minimapRect) matching the displayed orientation
            float displayX = flipX ? terrain.m_Width - 1 - tileX : tileX;
            float displayY = flipY ? tileZ : terrain.m_Height - 1 - tileZ;
            return new Vector2(displayX * scaleX, displayY * scaleY);
        }

        private void DrawLineLocal(Vector2 a, Vector2 b, Color color, float thickness)
        {
            Vector2 delta = b - a;
            float length = delta.magnitude;
            if (length < 1f) return;

            float angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;

            Matrix4x4 matrixBackup = GUI.matrix;
            GUIUtility.RotateAroundPivot(angle, a);
            EditorGUI.DrawRect(new Rect(a.x, a.y - thickness / 2f, length, thickness), color);
            GUI.matrix = matrixBackup;
        }

        private void DrawCrosshairLocal(Vector2 center, Vector2 boundsSize)
        {
            if (center.x < 0 || center.y < 0 || center.x > boundsSize.x || center.y > boundsSize.y)
                return;

            Color color = new Color(1f, 1f, 0f, 0.9f);
            float size = 8f;
            float thickness = 2f;

            // Horizontal
            EditorGUI.DrawRect(new Rect(center.x - size, center.y - thickness / 2f, size * 2, thickness), color);
            // Vertical
            EditorGUI.DrawRect(new Rect(center.x - thickness / 2f, center.y - size, thickness, size * 2), color);
        }

        private Rect ClampRect(Rect rect, Rect bounds)
        {
            float x = Mathf.Max(rect.x, bounds.x);
            float y = Mathf.Max(rect.y, bounds.y);
            float xMax = Mathf.Min(rect.xMax, bounds.xMax);
            float yMax = Mathf.Min(rect.yMax, bounds.yMax);
            return new Rect(x, y, Mathf.Max(0, xMax - x), Mathf.Max(0, yMax - y));
        }

        private void HandleMinimapInput(Rect minimapRect)
        {
            Event e = Event.current;
            Vector2 mousePos = e.mousePosition;

            bool mouseInside = minimapRect.Contains(mousePos);

            if (!mouseInside && !isDraggingMinimap)
            {
                hoveredTile = new Vector2Int(-1, -1);
                hoveredTerrainTid = null;
                hoveredTerrainName = null;
                return;
            }

            // Handle zoom with scroll wheel inside minimap
            if (mouseInside && e.type == EventType.ScrollWheel)
            {
                ZoomSceneView(e.delta.y);
                e.Use();
                Repaint();
                return;
            }

            // Convert mouse position to tile coordinates
            float clampedX = Mathf.Clamp(mousePos.x, minimapRect.xMin, minimapRect.xMax);
            float clampedY = Mathf.Clamp(mousePos.y, minimapRect.yMin, minimapRect.yMax);

            float relX = (clampedX - minimapRect.x) / minimapRect.width;
            float relY = (clampedY - minimapRect.y) / minimapRect.height;

            // Map from GUI position (top-left origin) to displayed tile coordinates (floats for smooth drag)
            float displayX = Mathf.Clamp(relX * terrain.m_Width, 0f, terrain.m_Width - 0.0001f);
            float displayY = Mathf.Clamp(relY * terrain.m_Height, 0f, terrain.m_Height - 0.0001f);
            int displayTileX = Mathf.FloorToInt(displayX);
            int displayTileY = Mathf.FloorToInt(displayY);

            // Convert displayed tile back to world-space tile index
            int worldTileX = flipX ? terrain.m_Width - 1 - displayTileX : displayTileX;
            int worldTileY = flipY ? displayTileY : terrain.m_Height - 1 - displayTileY;
            float worldTileXFloat = flipX ? terrain.m_Width - 1 - displayX : displayX;
            float worldTileYFloat = flipY ? displayY : terrain.m_Height - 1 - displayY;

            worldTileX = Mathf.Clamp(worldTileX, 0, terrain.m_Width - 1);
            worldTileY = Mathf.Clamp(worldTileY, 0, terrain.m_Height - 1);

            hoveredTile = new Vector2Int(worldTileX, worldTileY);

            // Get terrain type at hovered tile
            if (virtualGrid != null)
            {
                hoveredTerrainTid = virtualGrid.GetTerrainId(worldTileX, worldTileY);
                hoveredTerrainName = TerrainDefinitions.GetTerrainName(hoveredTerrainTid);
            }

            // Handle clicks
            if (e.type == EventType.MouseDown && e.button == 0)
            {
                bool isSampleModifier = e.control || e.command;

                if (isSampleModifier)
                {
                    // Sample terrain as brush
                    if (!string.IsNullOrEmpty(hoveredTerrainTid))
                    {
                        TerrainPaintToolWindow.SetBrushTerrain(hoveredTerrainTid);
                        Debug.Log($"[Minimap] Sampled brush: {hoveredTerrainTid}");
                    }
                }
                else
                {
                    // Jump camera to tile and start drag-pan (continuous)
                    JumpToTile(worldTileXFloat, worldTileYFloat);
                    isDraggingMinimap = true;
                    lastDragTile = new Vector2(worldTileXFloat, worldTileYFloat);
                }

                e.Use();
            }

            if (e.type == EventType.MouseDrag && isDraggingMinimap && e.button == 0)
            {
                Vector2 current = new Vector2(worldTileXFloat, worldTileYFloat);
                if (!Mathf.Approximately(current.x, lastDragTile.x) || !Mathf.Approximately(current.y, lastDragTile.y))
                {
                    JumpToTile(worldTileXFloat, worldTileYFloat);
                    lastDragTile = current;
                }
                e.Use();
            }

            if (e.type == EventType.MouseUp && e.button == 0)
            {
                isDraggingMinimap = false;
                lastDragTile = new Vector2(float.MinValue, float.MinValue);
            }

            Repaint();
        }

        private void ZoomSceneView(float scrollDelta)
        {
            var sv = SceneView.lastActiveSceneView;
            if (sv == null) return;

            // Respect natural scroll preference (invert if needed)
            float direction = naturalScroll ? 1f : -1f;
            float factor = Mathf.Exp(scrollDelta * direction * 0.05f);
            factor = Mathf.Clamp(factor, 0.01f, 100f);

            if (sv.orthographic)
            {
                float newSize = Mathf.Clamp(sv.size * factor, MIN_ORTHO_SIZE, MAX_ORTHO_SIZE);
                sv.size = newSize;
                sv.Repaint();
                return;
            }

            if (sv.camera == null) return;

            float currentDistance = Vector3.Distance(sv.camera.transform.position, sv.pivot);
            currentDistance = Mathf.Max(currentDistance, 0.0001f);
            float newDistance = Mathf.Clamp(currentDistance * factor, MIN_CAMERA_DISTANCE, MAX_CAMERA_DISTANCE);

            sv.LookAt(sv.pivot, sv.rotation, newDistance);
            sv.Repaint();
        }

        private void JumpToTile(float tileX, float tileY)
        {
            if (terrain == null) return;

            var sv = SceneView.lastActiveSceneView;
            if (sv == null) return;

            Vector3 offset = TerrainPaintToolWindow.GetWorldOffset();

            // Calculate world position
            float worldX = terrain.m_X + offset.x + (tileX + 0.5f) * TILE_SIZE;
            float worldZ = terrain.m_Z + offset.z + (tileY + 0.5f) * TILE_SIZE;
            float worldY = sv.pivot.y; // Keep current height

            // Smooth movement by interpolating toward target when dragging
            const float dragLerp = 0.35f;
            bool isDragUpdate = isDraggingMinimap;
            Vector3 target = new Vector3(worldX, worldY, worldZ);
            if (isDragUpdate)
            {
                sv.pivot = Vector3.Lerp(sv.pivot, target, dragLerp);
            }
            else
            {
                sv.pivot = target;
            }
            sv.Repaint();
        }

        private void DrawStatusBar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

            if (hoveredTile.x >= 0 && terrain != null)
            {
                string displayName = !string.IsNullOrEmpty(hoveredTerrainName)
                    ? $"{hoveredTerrainName} ({hoveredTerrainTid})"
                    : hoveredTerrainTid ?? "Empty";
                EditorGUILayout.LabelField($"({hoveredTile.x}, {hoveredTile.y}): {displayName}", EditorStyles.miniLabel);
            }
            else
            {
                string hint = isCurrentMapMode
                    ? "Click/Drag: Move | Scroll: Zoom | Ctrl+Click: Sample"
                    : "Ctrl+Click: Sample brush";
                EditorGUILayout.LabelField(hint, EditorStyles.miniLabel);
            }

            EditorGUILayout.EndHorizontal();
        }

        private void RebuildMinimapTexture()
        {
            if (terrain == null) return;

            int width = terrain.m_Width;
            int height = terrain.m_Height;

            if (width <= 0 || height <= 0) return;

            // Get virtual grid from cache
            virtualGrid = TerrainVirtualGridCache.GetGrid(terrain);
            if (virtualGrid == null) return;

            // Create or resize texture
            if (minimapTexture == null || minimapTexture.width != width || minimapTexture.height != height)
            {
                CleanupTexture();
                minimapTexture = new Texture2D(width, height, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp
                };
            }

            // Fill texture with terrain colors
            Color[] pixels = new Color[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    string tid = virtualGrid.GetTerrainId(x, y);
                    Color color;
                    if (string.IsNullOrEmpty(tid) || tid == "YOURNO0123" || tid == "YOURNO0000")
                    {
                        color = new Color(0.2f, 0.2f, 0.2f, 1f); // Empty/no-entry
                    }
                    else
                    {
                        color = TerrainDefinitions.GetColorOrFallback(tid);
                    }

                    // Apply flip settings. Texture SetPixels uses bottom-left origin.
                    int texY = flipY ? height - 1 - y : y;
                    int texX = flipX ? width - 1 - x : x;
                    pixels[texY * width + texX] = color;
                }
            }

            minimapTexture.SetPixels(pixels);
            minimapTexture.Apply();

            lastTextureWidth = width;
            lastTextureHeight = height;
            textureDirty = false;
        }

        private void CleanupTexture()
        {
            if (minimapTexture != null)
            {
                DestroyImmediate(minimapTexture);
                minimapTexture = null;
            }
        }

        private void SyncWithPaintTool()
        {
            var paintToolTerrain = TerrainPaintToolWindow.SelectedTerrain;
            if (paintToolTerrain != terrain)
            {
                terrain = paintToolTerrain;
                textureDirty = true;
                UpdateWindowTitle();
            }
        }

        private void UpdateWindowTitle()
        {
            string prefix = isCurrentMapMode ? "Minimap" : "Ref";
            string mapName = terrain?.Name ?? "None";
            titleContent = new GUIContent($"{prefix}: {mapName}");
        }

        private void BrowseForMap()
        {
            string path = EditorUtility.OpenFilePanel("Select Terrain Asset", "Assets", "asset");
            if (string.IsNullOrEmpty(path)) return;

            // Convert to project-relative path
            if (path.StartsWith(Application.dataPath))
            {
                path = "Assets" + path.Substring(Application.dataPath.Length);
            }

            var loadedTerrain = TerrainAssetAdapter.Load(path);
            if (loadedTerrain != null)
            {
                terrain = loadedTerrain;
                referenceMapPath = path;
                textureDirty = true;
                isCurrentMapMode = false;
                UpdateWindowTitle();
            }
            else
            {
                EditorUtility.DisplayDialog("Error", "Failed to load terrain asset.", "OK");
            }
        }

        /// <summary>
        /// Called externally when terrain data changes
        /// </summary>
        public void MarkTextureDirty()
        {
            textureDirty = true;
            Repaint();
        }
    }
}
