using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace DivineDragon.MapTools
{
    public class TerrainMinimapWindow : EditorWindow
    {
        private enum MinimapGridMode { None, Simple, Island }
        private enum MinimapOrientationMode { NorthUp, CameraStable, CameraLive }

        // Keep in sync with TerrainPaintToolWindow.TILE_SIZE (world units per tile)
        private const float TILE_SIZE = 5f;
        private const float MIN_CAMERA_DISTANCE = 1f;
        private const float MAX_CAMERA_DISTANCE = 5000f;
        private const float MIN_ORTHO_SIZE = 0.5f;
        private const float MAX_ORTHO_SIZE = 5000f;
        private const float VIEWPORT_INSET = 0.02f;  // Small inset for numerical stability
        private static Material lineMaterial;

        // State
        private TerrainAssetAdapter terrain;
        private TerrainAssetAdapter referenceTerrain;  // Remembered reference map
        private TerrainVirtualGrid virtualGrid;
        private bool isCurrentMapMode = true;
        private Texture2D minimapTexture;
        private bool textureDirty = true;
        private int lastTextureWidth;
        private int lastTextureHeight;

        // UI state
        private bool flipX = false; // default to match scene orientation
        private bool flipY = false; // default to match scene orientation
        private Vector2Int hoveredTile = new Vector2Int(-1, -1);
        private string hoveredTerrainTid;
        private string hoveredTerrainName;
        private bool isDraggingMinimap;
        private bool isMinimapSampleMode; // true when Ctrl/Cmd held while hovering minimap
        private Vector2 lastDragTile = new Vector2(float.MinValue, float.MinValue);
        private Vector2[] cachedFrustumCorners; // absolute screen coords; null when invalid
        private bool isGrabPanning;
        private Vector3 grabStartCursorWorld;
        private Vector3 grabStartPivot;
        private bool naturalScroll = false;
        private MinimapGridMode gridMode = MinimapGridMode.None;
        private MinimapOrientationMode orientationMode = MinimapOrientationMode.NorthUp;

        private const string PREFS_NATURAL_SCROLL = "TerrainMinimap_NaturalScroll";
        private const string PREFS_GRID_MODE = "TerrainMinimap_GridMode";
        private const string PREFS_ORIENTATION_MODE = "TerrainMinimap_OrientationMode";

        [MenuItem("Map Tools/Terrain Minimap")]
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
            if (asReference)
            {
                window.referenceTerrain = terrain;  // Remember for toggling
            }
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
            TerrainPaintToolWindow.OnTerrainSelectionChanged += OnTerrainSelectionChanged;
            naturalScroll = EditorPrefs.GetBool(PREFS_NATURAL_SCROLL, false);
            gridMode = (MinimapGridMode)EditorPrefs.GetInt(PREFS_GRID_MODE, 0);
            orientationMode = (MinimapOrientationMode)EditorPrefs.GetInt(PREFS_ORIENTATION_MODE, 0);

            // Auto-sync with paint tool on enable (handles recompile/reopen)
            if (isCurrentMapMode)
            {
                SyncWithPaintTool();
            }
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            EditorApplication.update -= OnEditorUpdate;
            TerrainPaintToolWindow.OnTerrainDataChanged -= OnTerrainDataChanged;
            TerrainPaintToolWindow.OnTerrainSelectionChanged -= OnTerrainSelectionChanged;
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

        private void OnTerrainSelectionChanged(TerrainAssetAdapter newTerrain)
        {
            if (isCurrentMapMode)
            {
                SyncWithPaintTool();
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

            // Mode toggle using toolbar for proper mutual exclusion
            int mode = isCurrentMapMode ? 0 : 1;
            int newMode = GUILayout.Toolbar(mode, new[] { "Current Map", "Reference" }, EditorStyles.toolbarButton, GUILayout.Width(160));
            if (newMode != mode)
            {
                isCurrentMapMode = (newMode == 0);
                if (isCurrentMapMode)
                {
                    // Switching to Current Map - restore from paint tool
                    SyncWithPaintTool();
                }
                else
                {
                    // Switching to Reference - show remembered reference or clear
                    terrain = referenceTerrain;  // Will be null if none selected yet
                    textureDirty = true;
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

            if (GUILayout.Button(new GUIContent("+", "Open new minimap window with same settings"), EditorStyles.toolbarButton, GUILayout.Width(22)))
            {
                CloneWindow();
            }

            GUILayout.Space(6);
            bool newNaturalScroll = GUILayout.Toggle(naturalScroll, "Natural Scroll", EditorStyles.toolbarButton, GUILayout.Width(100));
            if (newNaturalScroll != naturalScroll)
            {
                naturalScroll = newNaturalScroll;
                EditorPrefs.SetBool(PREFS_NATURAL_SCROLL, naturalScroll);
            }

            // Grid mode dropdown
            string[] gridModeNames = { "No Grid", "Grid", "Islands" };
            int newGridMode = EditorGUILayout.Popup((int)gridMode, gridModeNames, EditorStyles.toolbarPopup, GUILayout.Width(70));
            if (newGridMode != (int)gridMode)
            {
                gridMode = (MinimapGridMode)newGridMode;
                EditorPrefs.SetInt(PREFS_GRID_MODE, newGridMode);
                Repaint();
            }

            // Orientation dropdown: north-up vs camera-forward (stable size or live-fit)
            string[] orientationNames = { "North", "Camera", "Camera (Live)" };
            int newOrientation = EditorGUILayout.Popup((int)orientationMode, orientationNames, EditorStyles.toolbarPopup, GUILayout.Width(110));
            if (newOrientation != (int)orientationMode)
            {
                orientationMode = (MinimapOrientationMode)newOrientation;
                EditorPrefs.SetInt(PREFS_ORIENTATION_MODE, newOrientation);
                Repaint();
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

            // Always paint the bounding background un-rotated so any spillover doesn't bleed
            // beyond the minimap rect.
            EditorGUI.DrawRect(drawRect, new Color(0.15f, 0.15f, 0.15f, 1f));

            // Camera-forward orientation rotates the canvas around the minimap center so the
            // scene camera's facing direction always points "up" on the minimap. We also
            // rescale uniformly so the rotated bounding box fits inside the original drawRect
            // (otherwise corners poke outside).
            float rotationDeg = ComputeMinimapRotationDegrees();
            bool isRotated = !Mathf.Approximately(rotationDeg, 0f);
            Vector2 rotPivot = drawRect.center;
            float fitScale = isRotated ? ComputeFitScaleForMode(rotationDeg, drawRect) : 1f;

            Matrix4x4 prevMatrix = GUI.matrix;
            if (isRotated)
            {
                GUIUtility.RotateAroundPivot(rotationDeg, rotPivot);
                GUIUtility.ScaleAroundPivot(new Vector2(fitScale, fitScale), rotPivot);
            }

            // Draw minimap texture
            GUI.DrawTexture(drawRect, minimapTexture, ScaleMode.StretchToFill);

            // Draw grid overlay
            if (gridMode != MinimapGridMode.None)
            {
                DrawGridOverlay(drawRect);
            }

            // Draw view frustum if current map mode
            if (isCurrentMapMode)
            {
                DrawViewFrustum(drawRect);
            }

            // Draw hover highlight (subtle) and sample highlight (inverted)
            DrawHoverHighlight(drawRect);
            DrawSampleHighlight(drawRect);

            GUI.matrix = prevMatrix;

            // Handle input (un-rotates and un-scales the cursor internally)
            HandleMinimapInput(drawRect, rotationDeg, rotPivot, fitScale);
        }

        private static float ComputeFitScale(float rotationDeg, Rect rect)
        {
            float radAbs = Mathf.Abs(rotationDeg) * Mathf.Deg2Rad;
            float c = Mathf.Abs(Mathf.Cos(radAbs));
            float s = Mathf.Abs(Mathf.Sin(radAbs));
            float boundingWidth = rect.width * c + rect.height * s;
            float boundingHeight = rect.width * s + rect.height * c;
            if (boundingWidth <= 0f || boundingHeight <= 0f) return 1f;
            return Mathf.Min(rect.width / boundingWidth, rect.height / boundingHeight);
        }

        // Worst-case scale across all rotation angles for this rect. Used by CameraStable so the
        // minimap stays a constant size as the camera spins.
        private static float ComputeWorstCaseFitScale(Rect rect)
        {
            float worst = 1f;
            for (int deg = 0; deg <= 90; deg += 5)
            {
                worst = Mathf.Min(worst, ComputeFitScale(deg, rect));
            }
            return worst;
        }

        private float ComputeFitScaleForMode(float rotationDeg, Rect rect)
        {
            if (orientationMode == MinimapOrientationMode.CameraStable)
            {
                return ComputeWorstCaseFitScale(rect);
            }
            return ComputeFitScale(rotationDeg, rect);
        }

        private float ComputeMinimapRotationDegrees()
        {
            if (orientationMode == MinimapOrientationMode.NorthUp) return 0f;
            if (!isCurrentMapMode) return 0f;
            var sv = SceneView.lastActiveSceneView;
            if (sv == null || sv.camera == null) return 0f;
            // Negate the camera's world Y rotation so the canvas spins opposite to the camera,
            // bringing the camera's forward direction to screen-up.
            return -sv.camera.transform.eulerAngles.y;
        }

        private void DrawViewFrustum(Rect minimapRect)
        {
            var sv = SceneView.lastActiveSceneView;
            if (sv == null || sv.camera == null || terrain == null)
                return;

            Camera cam = sv.camera;
            Vector3 camPos = cam.transform.position;
            float terrainY = TerrainPaintToolWindow.GetWorldOffset().y;

            // Cast center ray to get reference distance
            Ray centerRay = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0));
            Vector3? centerHit = RayPlaneIntersect(centerRay, terrainY);

            if (!centerHit.HasValue)
            {
                // Camera not looking at terrain - show crosshair at pivot
                cachedFrustumCorners = null;
                GUI.BeginClip(minimapRect);
                Vector2 pivotLocal = WorldToMinimapLocal(sv.pivot, minimapRect);
                DrawCrosshairLocal(pivotLocal, minimapRect.size);
                GUI.EndClip();
                return;
            }

            // Auto-calculate multiplier based on FOV and pitch angle
            float pitchDeg = cam.transform.eulerAngles.x;
            if (pitchDeg > 180f) pitchDeg -= 360f; // Normalize to -180 to 180

            float pitchRad = Mathf.Abs(pitchDeg) * Mathf.Deg2Rad;
            float halfFovRad = cam.fieldOfView * 0.5f * Mathf.Deg2Rad;

            // Natural multiplier: ratio of corner distance to center distance.
            // Floor the denominator so the formula stays continuous across all pitches; the
            // Clamp below already enforces the safety bound that the previous else-branch was
            // doing discretely (causing a visible jump as pitch crossed the 0.15 rad threshold).
            float topCornerAngle = pitchRad - halfFovRad;
            float safeTopAngle = Mathf.Max(topCornerAngle, 0.15f);
            float autoMultiplier = Mathf.Sin(pitchRad) / Mathf.Sin(safeTopAngle) * 0.85f;
            autoMultiplier = Mathf.Clamp(autoMultiplier, 1.1f, 2.5f);

            float centerDistance = Vector3.Distance(camPos, centerHit.Value);
            float maxDistance = Mathf.Max(centerDistance * autoMultiplier, 10f);

            // Viewport corners with small inset for numerical stability
            float v0 = VIEWPORT_INSET;
            float v1 = 1f - VIEWPORT_INSET;
            Vector3[] viewportCorners = new Vector3[]
            {
                new Vector3(v0, v0, 0),  // bottom-left
                new Vector3(v1, v0, 0),  // bottom-right
                new Vector3(v1, v1, 0),  // top-right
                new Vector3(v0, v1, 0)   // top-left
            };

            Vector2[] corners = new Vector2[4];

            for (int i = 0; i < 4; i++)
            {
                Ray ray = cam.ViewportPointToRay(viewportCorners[i]);
                Vector3? hit = RayPlaneIntersect(ray, terrainY);

                Vector3 finalHit;
                if (hit.HasValue)
                {
                    // Clamp hit point if too far from camera
                    float hitDistance = Vector3.Distance(camPos, hit.Value);
                    if (hitDistance > maxDistance)
                    {
                        Vector3 rayDir = (hit.Value - camPos).normalized;
                        finalHit = camPos + rayDir * maxDistance;
                        finalHit.y = terrainY;
                    }
                    else
                    {
                        finalHit = hit.Value;
                    }
                }
                else
                {
                    // Ray doesn't hit the terrain plane (horizontal or pointing above-horizon
                    // when the camera pitch is shallower than half-FOV). Synthesize a corner
                    // by walking maxDistance along the ray and dropping onto the plane — this
                    // keeps the quad continuous as top corners cross the horizon, instead of
                    // flickering to a crosshair fallback for one frame.
                    finalHit = camPos + ray.direction.normalized * maxDistance;
                    finalHit.y = terrainY;
                }

                corners[i] = WorldToMinimapLocal(finalHit, minimapRect);
            }

            // Convert to absolute screen coordinates without clamping. Per-axis clamping was
            // collapsing far corners onto the rect edges (often producing degenerate triangles
            // when several corners snapped to the same edge), which caused the frustum quad to
            // briefly mis-shape during drag. Instead we keep the true corners and let the line
            // clipper trim each edge to the rect.
            Vector2[] absCorners = new Vector2[4];
            for (int i = 0; i < 4; i++)
            {
                absCorners[i] = new Vector2(
                    minimapRect.x + corners[i].x,
                    minimapRect.y + corners[i].y
                );
            }
            cachedFrustumCorners = absCorners;

            Color frustumColor = new Color(1f, 1f, 0f, 0.9f);
            float thickness = 2f;

            // Sutherland-Hodgman polygon clipping: clip the whole frustum quad against the rect
            // and draw the resulting closed polyline. Per-edge line clipping (Liang-Barsky) caused
            // popping when the quad extended past the rect — adjacent edges' clipped portions had
            // gaps along the rect boundary that flickered in/out as corners crossed thresholds.
            // Polygon clipping closes those gaps with rect-boundary segments, so the outline morphs
            // continuously instead of edges blinking on and off.
            List<Vector2> clipped = ClipPolygonToRect(absCorners, minimapRect);
            int n = clipped.Count;
            for (int i = 0; i < n; i++)
            {
                DrawLineLocal(clipped[i], clipped[(i + 1) % n], frustumColor, thickness);
            }
        }

        // Sutherland-Hodgman polygon clipping. Reused buffers avoid per-frame allocations.
        private static readonly List<Vector2> sClipBufA = new List<Vector2>(8);
        private static readonly List<Vector2> sClipBufB = new List<Vector2>(8);

        private static List<Vector2> ClipPolygonToRect(Vector2[] polygon, Rect rect)
        {
            sClipBufA.Clear();
            sClipBufA.AddRange(polygon);

            sClipBufB.Clear();
            ClipAgainstEdge(sClipBufA, sClipBufB, 0, rect.xMin); // x >= xMin
            if (sClipBufB.Count == 0) return sClipBufB;

            sClipBufA.Clear();
            ClipAgainstEdge(sClipBufB, sClipBufA, 1, rect.xMax); // x <= xMax
            if (sClipBufA.Count == 0) return sClipBufA;

            sClipBufB.Clear();
            ClipAgainstEdge(sClipBufA, sClipBufB, 2, rect.yMin); // y >= yMin
            if (sClipBufB.Count == 0) return sClipBufB;

            sClipBufA.Clear();
            ClipAgainstEdge(sClipBufB, sClipBufA, 3, rect.yMax); // y <= yMax
            return sClipBufA;
        }

        private static void ClipAgainstEdge(List<Vector2> input, List<Vector2> output, int edgeKind, float v)
        {
            if (input.Count == 0) return;

            Vector2 prev = input[input.Count - 1];
            bool prevInside = IsInsideEdge(prev, edgeKind, v);

            for (int i = 0; i < input.Count; i++)
            {
                Vector2 curr = input[i];
                bool currInside = IsInsideEdge(curr, edgeKind, v);

                if (currInside)
                {
                    if (!prevInside) output.Add(EdgeIntersect(prev, curr, edgeKind, v));
                    output.Add(curr);
                }
                else if (prevInside)
                {
                    output.Add(EdgeIntersect(prev, curr, edgeKind, v));
                }

                prev = curr;
                prevInside = currInside;
            }
        }

        private static bool IsInsideEdge(Vector2 p, int edgeKind, float v)
        {
            switch (edgeKind)
            {
                case 0: return p.x >= v;
                case 1: return p.x <= v;
                case 2: return p.y >= v;
                default: return p.y <= v;
            }
        }

        private static Vector2 EdgeIntersect(Vector2 a, Vector2 b, int edgeKind, float v)
        {
            if (edgeKind <= 1)
            {
                float dx = b.x - a.x;
                if (Mathf.Approximately(dx, 0f)) return new Vector2(v, a.y);
                float t = (v - a.x) / dx;
                return new Vector2(v, a.y + t * (b.y - a.y));
            }
            float dy = b.y - a.y;
            if (Mathf.Approximately(dy, 0f)) return new Vector2(a.x, v);
            float ty = (v - a.y) / dy;
            return new Vector2(a.x + ty * (b.x - a.x), v);
        }

        // Convex-quad point-in-polygon: cursor is inside iff it's on the same side
        // of every edge (consistent cross-product sign).
        private static bool PointInQuad(Vector2 p, Vector2[] q)
        {
            if (q == null || q.Length != 4) return false;
            int sign = 0;
            for (int i = 0; i < 4; i++)
            {
                Vector2 a = q[i];
                Vector2 b = q[(i + 1) % 4];
                float cross = (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);
                int s = cross > 0f ? 1 : (cross < 0f ? -1 : 0);
                if (s == 0) continue;
                if (sign == 0) sign = s;
                else if (sign != s) return false;
            }
            return true;
        }

        private void DrawHoverHighlight(Rect minimapRect)
        {
            if (terrain == null || virtualGrid == null) return;
            // Don't show hover highlight if sampling (sample highlight takes over)
            if (isMinimapSampleMode) return;
            if (isCurrentMapMode && TerrainPaintToolWindow.IsSampleModeActive) return;
            // Only show when hovering in minimap
            if (hoveredTile.x < 0 || hoveredTile.y < 0) return;

            // Bounds check
            if (hoveredTile.x >= terrain.m_Width || hoveredTile.y >= terrain.m_Height) return;

            // Get terrain color and lighten/darken based on brightness
            string terrainId = virtualGrid.GetTerrainId(hoveredTile.x, hoveredTile.y);
            Color terrainColor = TerrainDefinitions.GetColorOrFallback(terrainId);

            // Calculate luminance to decide whether to lighten or darken
            float luminance = 0.299f * terrainColor.r + 0.587f * terrainColor.g + 0.114f * terrainColor.b;
            Color highlightColor = luminance > 0.5f
                ? new Color(0f, 0f, 0f, 0.3f)  // Darken light tiles
                : new Color(1f, 1f, 1f, 0.3f); // Lighten dark tiles

            // Convert tile to display coordinates
            int displayX = flipX ? terrain.m_Width - 1 - hoveredTile.x : hoveredTile.x;
            int displayY = flipY ? hoveredTile.y : terrain.m_Height - 1 - hoveredTile.y;

            float cellWidth = minimapRect.width / terrain.m_Width;
            float cellHeight = minimapRect.height / terrain.m_Height;

            float left = minimapRect.x + displayX * cellWidth;
            float top = minimapRect.y + displayY * cellHeight;

            Rect tileRect = new Rect(left, top, cellWidth, cellHeight);

            // Draw subtle highlight overlay
            EditorGUI.DrawRect(tileRect, highlightColor);
        }

        private void DrawSampleHighlight(Rect minimapRect)
        {
            if (terrain == null || virtualGrid == null) return;

            // Determine which tile to highlight:
            // - If mouse is in minimap with Ctrl/Cmd held, use the minimap's hovered tile (works in both modes)
            // - Otherwise, use the main scene view's hovered tile (only in current map mode)
            Vector2Int sampleTile;
            if (isMinimapSampleMode && hoveredTile.x >= 0)
            {
                sampleTile = hoveredTile;
            }
            else if (isCurrentMapMode && TerrainPaintToolWindow.IsSampleModeActive)
            {
                sampleTile = TerrainPaintToolWindow.HoveredTile;
            }
            else
            {
                return;
            }

            if (sampleTile.x < 0 || sampleTile.y < 0) return;

            // Bounds check
            if (sampleTile.x >= terrain.m_Width || sampleTile.y >= terrain.m_Height) return;

            // Get terrain color and invert it
            string terrainId = virtualGrid.GetTerrainId(sampleTile.x, sampleTile.y);
            Color terrainColor = TerrainDefinitions.GetColorOrFallback(terrainId);
            Color invertedColor = new Color(1f - terrainColor.r, 1f - terrainColor.g, 1f - terrainColor.b, 1f);

            // Convert tile to display coordinates
            int displayX = flipX ? terrain.m_Width - 1 - sampleTile.x : sampleTile.x;
            int displayY = flipY ? sampleTile.y : terrain.m_Height - 1 - sampleTile.y;

            float cellWidth = minimapRect.width / terrain.m_Width;
            float cellHeight = minimapRect.height / terrain.m_Height;

            float left = minimapRect.x + displayX * cellWidth;
            float top = minimapRect.y + displayY * cellHeight;

            Rect tileRect = new Rect(left, top, cellWidth, cellHeight);

            // Draw inverted color tile
            EditorGUI.DrawRect(tileRect, invertedColor);
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
            float displayX = flipX ? terrain.m_Width - tileX : tileX;
            float displayY = flipY ? tileZ : terrain.m_Height - tileZ;
            return new Vector2(displayX * scaleX, displayY * scaleY);
        }

        private void DrawLineLocal(Vector2 a, Vector2 b, Color color, float thickness)
        {
            // Guard: GL.LoadPixelMatrix + GL.QUADS during a non-Repaint event (Layout, MouseDrag,
            // MouseMove…) can draw into whichever GL context is currently bound — which during
            // an editor-drag cascade is sometimes the scene view, leaving phantom geometry there.
            // All visible IMGUI drawing happens in Repaint anyway, so skipping the others is safe.
            if (Event.current == null || Event.current.type != EventType.Repaint) return;

            Vector2 delta = b - a;
            float length = delta.magnitude;
            if (length < 0.5f) return;

            CreateLineMaterial();
            
            Vector2 dir = delta / length;
            Vector2 perp = new Vector2(-dir.y, dir.x) * thickness * 0.5f;
            
            GL.PushMatrix();
            GL.LoadPixelMatrix();
            lineMaterial.SetPass(0);
            
            GL.Begin(GL.QUADS);
            GL.Color(color);
            GL.Vertex3(a.x - perp.x, a.y - perp.y, 0);
            GL.Vertex3(a.x + perp.x, a.y + perp.y, 0);
            GL.Vertex3(b.x + perp.x, b.y + perp.y, 0);
            GL.Vertex3(b.x - perp.x, b.y - perp.y, 0);
            GL.End();
            
            GL.PopMatrix();
        }

        private static void CreateLineMaterial()
        {
            if (lineMaterial != null) return;
            
            Shader shader = Shader.Find("Hidden/Internal-Colored");
            lineMaterial = new Material(shader);
            lineMaterial.hideFlags = HideFlags.HideAndDontSave;
            lineMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            lineMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            lineMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            lineMaterial.SetInt("_ZWrite", 0);
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

        private void DrawGridOverlay(Rect minimapRect)
        {
            if (terrain == null) return;

            switch (gridMode)
            {
                case MinimapGridMode.Simple:
                    DrawSimpleGrid(minimapRect);
                    break;
                case MinimapGridMode.Island:
                    DrawIslandGrid(minimapRect);
                    break;
            }
        }

        private void DrawSimpleGrid(Rect minimapRect)
        {
            if (terrain == null) return;

            // Two-pass drawing: dark outline then light line for visibility on any background
            Color darkColor = new Color(0f, 0f, 0f, 0.25f);
            Color lightColor = new Color(1f, 1f, 1f, 0.3f);
            float cellWidth = minimapRect.width / terrain.m_Width;
            float cellHeight = minimapRect.height / terrain.m_Height;

            // Vertical lines
            for (int x = 1; x < terrain.m_Width; x++)
            {
                float xPos = minimapRect.x + x * cellWidth;
                Vector2 top = new Vector2(xPos, minimapRect.y);
                Vector2 bottom = new Vector2(xPos, minimapRect.yMax);
                DrawLineLocal(top, bottom, darkColor, 2f);
                DrawLineLocal(top, bottom, lightColor, 1f);
            }

            // Horizontal lines
            for (int y = 1; y < terrain.m_Height; y++)
            {
                float yPos = minimapRect.y + y * cellHeight;
                Vector2 left = new Vector2(minimapRect.x, yPos);
                Vector2 right = new Vector2(minimapRect.xMax, yPos);
                DrawLineLocal(left, right, darkColor, 2f);
                DrawLineLocal(left, right, lightColor, 1f);
            }
        }

        private void DrawIslandGrid(Rect minimapRect)
        {
            if (terrain == null || virtualGrid == null) return;

            // Get islands from paint tool (shared cache)
            var islands = TerrainPaintToolWindow.GetIslandsForTerrain(terrain);
            if (islands == null || islands.Count == 0) return;

            float cellWidth = minimapRect.width / terrain.m_Width;
            float cellHeight = minimapRect.height / terrain.m_Height;

            foreach (var island in islands)
            {
                // Skip empty terrain islands
                if (TerrainVirtualGridCache.IsEmptyTerrain(island.terrainId))
                    continue;

                Color borderColor = GetIslandBorderColor(island.terrainId);
                var tileSet = island.TileSet;

                foreach (var tile in island.tiles)
                {
                    // Convert to display coordinates (apply flip)
                    int displayX = flipX ? terrain.m_Width - 1 - tile.x : tile.x;
                    int displayY = flipY ? tile.y : terrain.m_Height - 1 - tile.y;

                    float left = minimapRect.x + displayX * cellWidth;
                    float top = minimapRect.y + displayY * cellHeight;
                    float right = left + cellWidth;
                    float bottom = top + cellHeight;

                    // Check each edge - draw if neighbor not in same island
                    // The edge positions need to account for flip

                    // Top edge (in world space: y+1)
                    Vector2Int neighborUp = new Vector2Int(tile.x, tile.y + 1);
                    if (!tileSet.Contains(neighborUp))
                    {
                        float edgeY = flipY ? bottom : top;
                        DrawLineLocal(new Vector2(left, edgeY), new Vector2(right, edgeY), borderColor, 1f);
                    }

                    // Bottom edge (in world space: y-1)
                    Vector2Int neighborDown = new Vector2Int(tile.x, tile.y - 1);
                    if (!tileSet.Contains(neighborDown))
                    {
                        float edgeY = flipY ? top : bottom;
                        DrawLineLocal(new Vector2(left, edgeY), new Vector2(right, edgeY), borderColor, 1f);
                    }

                    // Right edge (in world space: x+1)
                    Vector2Int neighborRight = new Vector2Int(tile.x + 1, tile.y);
                    if (!tileSet.Contains(neighborRight))
                    {
                        float edgeX = flipX ? left : right;
                        DrawLineLocal(new Vector2(edgeX, top), new Vector2(edgeX, bottom), borderColor, 1f);
                    }

                    // Left edge (in world space: x-1)
                    Vector2Int neighborLeft = new Vector2Int(tile.x - 1, tile.y);
                    if (!tileSet.Contains(neighborLeft))
                    {
                        float edgeX = flipX ? right : left;
                        DrawLineLocal(new Vector2(edgeX, top), new Vector2(edgeX, bottom), borderColor, 1f);
                    }
                }
            }
        }

        private static Color GetIslandBorderColor(string terrainId)
        {
            Color color = TerrainDefinitions.GetColorOrFallback(terrainId);
            // Darken to 60% like main view
            return new Color(color.r * 0.6f, color.g * 0.6f, color.b * 0.6f, 0.8f);
        }

        private void HandleMinimapInput(Rect minimapRect, float rotationDegrees, Vector2 rotationPivot, float fitScale)
        {
            Event e = Event.current;
            Vector2 mousePos = e.mousePosition;

            // If the canvas was transformed, undo the scale and rotation on the cursor so the
            // existing logic that works in pre-transform (logical) minimap coords keeps working.
            if (!Mathf.Approximately(rotationDegrees, 0f))
            {
                Vector2 d = mousePos - rotationPivot;
                if (fitScale > 0f && !Mathf.Approximately(fitScale, 1f))
                {
                    d /= fitScale;
                }
                float rad = -rotationDegrees * Mathf.Deg2Rad;
                float c = Mathf.Cos(rad);
                float s = Mathf.Sin(rad);
                mousePos = new Vector2(d.x * c - d.y * s, d.x * s + d.y * c) + rotationPivot;
            }

            bool mouseInside = minimapRect.Contains(mousePos);

            if (!mouseInside && !isDraggingMinimap && !isGrabPanning)
            {
                hoveredTile = new Vector2Int(-1, -1);
                hoveredTerrainTid = null;
                hoveredTerrainName = null;
                isMinimapSampleMode = false;
                return;
            }

            // Track if we're in minimap sample mode (Ctrl/Cmd held while mouse inside)
            isMinimapSampleMode = mouseInside && (e.control || e.command);

            // Handle zoom with scroll wheel inside minimap (only in current map mode)
            if (mouseInside && e.type == EventType.ScrollWheel && isCurrentMapMode)
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
                    // Sample terrain as brush (works in both modes)
                    if (!string.IsNullOrEmpty(hoveredTerrainTid))
                    {
                        TerrainPaintToolWindow.SetBrushTerrain(hoveredTerrainTid);
                    }
                    e.Use();
                }
                else if (isCurrentMapMode)
                {
                    if (cachedFrustumCorners != null && PointInQuad(mousePos, cachedFrustumCorners))
                    {
                        // Click inside the frustum: grab and pan relatively (no jump).
                        var sv = SceneView.lastActiveSceneView;
                        if (sv != null)
                        {
                            grabStartCursorWorld = MinimapCursorToWorld(worldTileXFloat, worldTileYFloat);
                            grabStartPivot = sv.pivot;
                            isGrabPanning = true;
                            e.Use();
                        }
                    }
                    else
                    {
                        // Click outside the frustum: jump camera to tile, then drag-pan to follow cursor.
                        JumpToTile(worldTileXFloat, worldTileYFloat);
                        isDraggingMinimap = true;
                        lastDragTile = new Vector2(worldTileXFloat, worldTileYFloat);
                        e.Use();
                    }
                }
            }

            if (e.type == EventType.MouseDrag && e.button == 0 && isCurrentMapMode)
            {
                if (isGrabPanning)
                {
                    var sv = SceneView.lastActiveSceneView;
                    if (sv != null)
                    {
                        Vector3 currentCursorWorld = MinimapCursorToWorld(worldTileXFloat, worldTileYFloat);
                        Vector3 worldDelta = currentCursorWorld - grabStartCursorWorld;
                        Vector3 target = grabStartPivot + new Vector3(worldDelta.x, 0f, worldDelta.z);
                        const float dragLerp = 0.35f;
                        sv.pivot = Vector3.Lerp(sv.pivot, target, dragLerp);
                        sv.Repaint();
                    }
                    e.Use();
                }
                else if (isDraggingMinimap)
                {
                    Vector2 current = new Vector2(worldTileXFloat, worldTileYFloat);
                    if (!Mathf.Approximately(current.x, lastDragTile.x) || !Mathf.Approximately(current.y, lastDragTile.y))
                    {
                        JumpToTile(worldTileXFloat, worldTileYFloat);
                        lastDragTile = current;
                    }
                    e.Use();
                }
            }

            if (e.type == EventType.MouseUp && e.button == 0)
            {
                isDraggingMinimap = false;
                isGrabPanning = false;
                lastDragTile = new Vector2(float.MinValue, float.MinValue);
            }

            Repaint();
        }

        private void ZoomSceneView(float scrollDelta)
        {
            var sv = SceneView.lastActiveSceneView;
            if (sv == null) return;

            // scrollDelta > 0 = scroll down, scrollDelta < 0 = scroll up
            // Natural (default): scroll down = zoom in, scroll up = zoom out (content follows finger)
            // Traditional: scroll up = zoom in, scroll down = zoom out
            float direction = naturalScroll ? 1f : -1f;
            float factor = Mathf.Exp(scrollDelta * direction * 0.05f);

            // sv.size controls zoom for both orthographic and perspective cameras
            float newSize = sv.size * factor;
            newSize = Mathf.Clamp(newSize, MIN_ORTHO_SIZE, MAX_ORTHO_SIZE);
            sv.size = newSize;
            sv.Repaint();
        }

        private Vector3 MinimapCursorToWorld(float worldTileXFloat, float worldTileYFloat)
        {
            Vector3 offset = TerrainPaintToolWindow.GetWorldOffset();
            float worldX = terrain.m_X + offset.x + worldTileXFloat * TILE_SIZE;
            float worldZ = terrain.m_Z + offset.z + worldTileYFloat * TILE_SIZE;
            return new Vector3(worldX, 0f, worldZ);
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

        // Snapshot of status-bar branch state captured on the Layout event and reused on the
        // matching Repaint. Without this, mouse-driven state (hoveredTile, isMinimapSampleMode,
        // paint-tool sample mode) can flip between the paired Layout and Repaint passes, the two
        // passes take different branches with different GUILayout control counts, and IMGUI
        // throws "control N's position in a group with only N controls".
        private bool statusChipVisible;
        private string statusChipTid;
        private string statusLabel;

        private void DrawStatusBar()
        {
            if (Event.current.type == EventType.Layout)
            {
                CaptureStatusBarState();
            }

            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            if (statusChipVisible)
            {
                DrawStatusBarColorChip(statusChipTid);
            }
            EditorGUILayout.LabelField(statusLabel ?? string.Empty, EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
        }

        private void CaptureStatusBarState()
        {
            bool isSamplingFromMinimap = isMinimapSampleMode && hoveredTile.x >= 0;
            bool isSamplingFromScene = isCurrentMapMode && !isMinimapSampleMode && TerrainPaintToolWindow.IsSampleModeActive;

            if (isSamplingFromMinimap && terrain != null && virtualGrid != null)
            {
                statusChipVisible = true;
                statusChipTid = hoveredTerrainTid;
                string displayName = !string.IsNullOrEmpty(hoveredTerrainTid)
                    ? TerrainDefinitions.GetDisplayString(hoveredTerrainTid)
                    : "Empty";
                statusLabel = $"Sampling ({hoveredTile.x}, {hoveredTile.y}): {displayName}";
                return;
            }

            if (isSamplingFromScene && terrain != null && virtualGrid != null)
            {
                Vector2Int sampleTile = TerrainPaintToolWindow.HoveredTile;
                if (sampleTile.x >= 0)
                {
                    string sampleTerrainTid = virtualGrid.GetTerrainId(sampleTile.x, sampleTile.y);
                    statusChipVisible = true;
                    statusChipTid = sampleTerrainTid;
                    string displayName = !string.IsNullOrEmpty(sampleTerrainTid)
                        ? TerrainDefinitions.GetDisplayString(sampleTerrainTid)
                        : "Empty";
                    statusLabel = $"Sampling ({sampleTile.x}, {sampleTile.y}): {displayName}";
                }
                else
                {
                    statusChipVisible = false;
                    statusChipTid = null;
                    statusLabel = "Sampling...";
                }
                return;
            }

            if (hoveredTile.x >= 0 && terrain != null)
            {
                statusChipVisible = true;
                statusChipTid = hoveredTerrainTid;
                string displayName = !string.IsNullOrEmpty(hoveredTerrainTid)
                    ? TerrainDefinitions.GetDisplayString(hoveredTerrainTid)
                    : "Empty";
                statusLabel = $"({hoveredTile.x}, {hoveredTile.y}): {displayName}";
                return;
            }

            statusChipVisible = false;
            statusChipTid = null;
            statusLabel = isCurrentMapMode
                ? "Click/Drag: Move | Scroll: Zoom | Ctrl+Click: Sample"
                : "Ctrl+Click: Sample brush";
        }

        private void DrawStatusBarColorChip(string terrainTid)
        {
            Color chipColor;
            if (string.IsNullOrEmpty(terrainTid) || TerrainVirtualGridCache.IsEmptyTerrain(terrainTid))
            {
                chipColor = new Color(0.3f, 0.3f, 0.3f, 1f);
            }
            else
            {
                chipColor = TerrainDefinitions.GetColorOrFallback(terrainTid);
            }

            const float chipSize = 12f;
            Rect reservedRect = GUILayoutUtility.GetRect(chipSize, EditorGUIUtility.singleLineHeight, GUILayout.Width(chipSize));
            GUILayout.Space(4);

            if (Event.current.type != EventType.Repaint) return;

            float yOffset = (reservedRect.height - chipSize) / 2f;
            Rect chipRect = new Rect(reservedRect.x, reservedRect.y + yOffset, chipSize, chipSize);

            EditorGUI.DrawRect(chipRect, chipColor);
            EditorGUI.DrawRect(new Rect(chipRect.x, chipRect.y, chipRect.width, 1), Color.black);
            EditorGUI.DrawRect(new Rect(chipRect.x, chipRect.yMax - 1, chipRect.width, 1), Color.black);
            EditorGUI.DrawRect(new Rect(chipRect.x, chipRect.y, 1, chipRect.height), Color.black);
            EditorGUI.DrawRect(new Rect(chipRect.xMax - 1, chipRect.y, 1, chipRect.height), Color.black);
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
                    if (string.IsNullOrEmpty(tid) || TerrainVirtualGridCache.IsEmptyTerrain(tid))
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

        private void CloneWindow()
        {
            var window = CreateInstance<TerrainMinimapWindow>();
            window.terrain = terrain;
            window.referenceTerrain = referenceTerrain;
            window.isCurrentMapMode = isCurrentMapMode;
            window.flipX = flipX;
            window.flipY = flipY;
            window.naturalScroll = naturalScroll;
            window.gridMode = gridMode;
            window.orientationMode = orientationMode;
            window.textureDirty = true;
            window.UpdateWindowTitle();
            window.minSize = new Vector2(200, 200);
            window.Show();
        }

        private void BrowseForMap()
        {
            // Exclude current map from picker (don't want to reference yourself)
            var currentMapTerrain = TerrainPaintToolWindow.SelectedTerrain;

            TerrainPickerWindow.Show(selected =>
            {
                if (selected != null)
                {
                    terrain = selected;
                    referenceTerrain = selected;  // Remember for toggling
                    textureDirty = true;
                    isCurrentMapMode = false;
                    UpdateWindowTitle();
                    Repaint();
                }
            }, exclude: currentMapTerrain);
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