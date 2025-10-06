using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering;

namespace DivineDragon.MapTools
{
    public enum DisplayMode
    {
        ColorOnly,
        Both
    }
    
    public enum TextDisplayMode
    {
        ShowTID,
        ShowName,
        ShowBoth
    }
    
    // Class to represent a connected group of terrain tiles
    public class TerrainIsland
    {
        public string terrainId;
        public List<Vector2Int> tiles;
        public Vector2 center;
        public List<Vector2> labelPositions;
        private readonly HashSet<Vector2Int> tileLookup;
        
        public TerrainIsland(string id)
        {
            terrainId = id;
            tiles = new List<Vector2Int>();
            labelPositions = new List<Vector2>();
            tileLookup = new HashSet<Vector2Int>();
        }

        public void AddTile(Vector2Int tile)
        {
            tiles.Add(tile);
            tileLookup.Add(tile);
        }

        public bool ContainsTile(Vector2Int tile) => tileLookup.Contains(tile);

        public HashSet<Vector2Int> TileSet => tileLookup;
        
        public void CalculateCenter()
        {
            if (tiles.Count == 0) return;
            
            float sumX = 0;
            float sumY = 0;
            foreach (var tile in tiles)
            {
                sumX += tile.x;
                sumY += tile.y;
            }
            center = new Vector2(sumX / tiles.Count, sumY / tiles.Count);
        }
        
        public void CalculateLabelPositions(float cameraDistance)
        {
            labelPositions.Clear();
            
            if (tiles.Count == 0) return;
            
            // Calculate center of mass first
            CalculateCenter();
            
            // Check if the center of mass actually falls within our tiles
            // This handles cases like sea that surrounds land
            Vector2Int centerInt = new Vector2Int(Mathf.RoundToInt(center.x), Mathf.RoundToInt(center.y));
            bool centerIsInTiles = tileLookup.Contains(centerInt);
            
            // Also check nearby tiles in case of rounding issues
            if (!centerIsInTiles)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        Vector2Int checkPos = new Vector2Int(centerInt.x + dx, centerInt.y + dy);
                        if (tileLookup.Contains(checkPos))
                        {
                            centerIsInTiles = true;
                            break;
                        }
                    }
                    if (centerIsInTiles) break;
                }
            }
            
            if (centerIsInTiles)
            {
                // Center is valid, use it
                labelPositions.Add(center);
            }
            else
            {
                // Center falls outside our tiles (e.g., sea surrounding land)
                // Find the largest contiguous section and place label there
                
                // Find bounds
                int minX = int.MaxValue, maxX = int.MinValue;
                int minY = int.MaxValue, maxY = int.MinValue;
                foreach (var tile in tiles)
                {
                    minX = Mathf.Min(minX, tile.x);
                    maxX = Mathf.Max(maxX, tile.x);
                    minY = Mathf.Min(minY, tile.y);
                    maxY = Mathf.Max(maxY, tile.y);
                }
                
                // Try placing label in corners/edges where we're likely to have solid sections
                Vector2Int[] candidatePositions = new Vector2Int[]
                {
                    new Vector2Int(minX + 2, minY + 2), // Bottom-left
                    new Vector2Int(maxX - 2, minY + 2), // Bottom-right
                    new Vector2Int(minX + 2, maxY - 2), // Top-left
                    new Vector2Int(maxX - 2, maxY - 2), // Top-right
                    new Vector2Int((minX + maxX) / 2, minY + 2), // Bottom-center
                    new Vector2Int((minX + maxX) / 2, maxY - 2), // Top-center
                    new Vector2Int(minX + 2, (minY + maxY) / 2), // Left-center
                    new Vector2Int(maxX - 2, (minY + maxY) / 2), // Right-center
                };
                
                // Find the candidate that has the most tiles around it
                Vector2Int bestPosition = tiles.First();
                int maxNeighbors = 0;
                
                foreach (var candidate in candidatePositions)
                {
                    if (!tileLookup.Contains(candidate)) continue;
                    
                    // Count tiles in a 5x5 area around this candidate
                    int neighborCount = 0;
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        for (int dy = -2; dy <= 2; dy++)
                        {
                            Vector2Int checkPos = new Vector2Int(candidate.x + dx, candidate.y + dy);
                            if (tileLookup.Contains(checkPos))
                            {
                                neighborCount++;
                            }
                        }
                    }
                    
                    if (neighborCount > maxNeighbors)
                    {
                        maxNeighbors = neighborCount;
                        bestPosition = candidate;
                    }
                }
                
                labelPositions.Add(new Vector2(bestPosition.x, bestPosition.y));
            }
        }
    }

    // Screen-space label node cached across frames for relaxed layout
    class LabelNode
    {
        public string key;
        public Vector2 anchorGui;
        public Vector2 posGui;
        public Vector2 preservedOffset; // preserved screen-space offset from anchor during camera movement
        public float width;
        public float height;
        public float priority;
        public bool seenThisFrame;
    }
    
    public partial class TerrainPaintToolWindow : EditorWindow
    {
        private static TerrainPaintToolWindow instance;
        // External tools (e.g., Dispos tool) can lock painting while keeping visualization
        private static bool externalInteractionLock = false;
        private static TerrainAssetAdapter selectedTerrain;
        private static bool visualizationEnabled = true;
        private static bool showGridLines = true;
        private static float textSize = 0.5f;
        private static Color textColor = Color.white;
        private static Color gridColor = new Color(1f, 1f, 1f, 0.3f);
        private static float gridThickness = 1f;
        private static float currentGridThicknessWorld = 0.01f;
        private static Vector3 worldOffset = Vector3.zero;

        private enum TerrainHeightMode
        {
            FixedOffset,
            RaycastMesh
        }

        private class TerrainHeightSettings
        {
            public float offset;
            public TerrainHeightMode mode;
            public bool autoSelectCollider = true;
            public string colliderPath = string.Empty;
        }

        private class TerrainHeightCache
        {
            public TerrainHeightMode mode;
            public int width;
            public int height;
            public float originX;
            public float originZ;
            public float offsetX;
            public float offsetZ;
            public string sceneKey;
            public float[] centerSamples;
            public float[] cornerSamples;
            public bool anyCenterHits;
            public bool anyCornerHits;
            public bool autoSelection;
            public string colliderPath;
            public string requestedColliderPath;
        }

        private class OverlayMeshData
        {
            public Mesh mesh;
            public Vector3[] vertices;
            public Color[] colors;
            public int[] indices;
            public TerrainHeightCache heightCache;
            public TerrainHeightMode heightMode;
            public float offsetX;
            public float offsetZ;
            public string sceneKey;
            public int width;
            public int height;
            public float colorOpacity;
            public float colorBrightness;
            public DisplayMode displayMode;
            public string colliderPath;
            public float fixedOffset;
        }

        private class GridMeshData
        {
            public Mesh mesh;
            public Vector3[] vertices;
            public Color[] colors;
            public int[] indices;
            public TerrainHeightCache heightCache;
            public TerrainHeightMode heightMode;
            public float offsetX;
            public float offsetZ;
            public string sceneKey;
            public int width;
            public int height;
            public string colliderPath;
            public float fixedOffset;
            public float pixelThickness;
            public float worldThickness;
            public Color color;
        }

        private static readonly Dictionary<string, TerrainHeightSettings> terrainHeightSettings = new Dictionary<string, TerrainHeightSettings>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<TerrainAssetAdapter, TerrainHeightCache> terrainHeightCache = new Dictionary<TerrainAssetAdapter, TerrainHeightCache>();
        private static readonly Dictionary<TerrainAssetAdapter, OverlayMeshData> overlayMeshCache = new Dictionary<TerrainAssetAdapter, OverlayMeshData>();
        private static readonly Dictionary<TerrainAssetAdapter, GridMeshData> gridMeshCache = new Dictionary<TerrainAssetAdapter, GridMeshData>();
        private static readonly Dictionary<string, MeshCollider> sceneColliderCache = new Dictionary<string, MeshCollider>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<TerrainAssetAdapter> meshCacheRemovalBuffer = new List<TerrainAssetAdapter>();
        private static Material overlayMaterial;
        private static readonly HashSet<string> loggedRaycastFailures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<string> raycastLogRemovalBuffer = new List<string>();
        private static bool terrainHeightPrefsLoaded = false;
        private static DisplayMode displayMode = DisplayMode.Both;
        // Label display is always Labels mode (no chips)
        private static float colorOpacity = 0.5f;
        private static float colorBrightness = 1.0f;
        private static TerrainTypeDatabase terrainDatabase;
        private static TextDisplayMode textDisplayMode = TextDisplayMode.ShowTID;
        
        // Island caching for smooth transitions
        private static readonly Dictionary<TerrainAssetAdapter, List<TerrainIsland>> islandCache = new Dictionary<TerrainAssetAdapter, List<TerrainIsland>>();
        private static TerrainAssetAdapter lastCachedTerrain = null;
        private static float lastIslandCameraDistance = -1f;
        private static float lastFrameTime = 0f;
        
        // Screen-space relaxation state/tunables
        private static Dictionary<string, LabelNode> s_LabelNodes = new Dictionary<string, LabelNode>();
        private static bool relaxEnabled = true;
        private static int relaxIterations = 1;          // committed default
        private static float relaxAnchorK = 0.05f;       // committed default
        private static float relaxMaxStepPx = 3.0f;
        private static float relaxRadiusPxBase = 40f;
        private static bool relaxFreezeWhileMoving = true; // preserve offsets while moving
        private static int relaxLargeIslandTiles = 80;
        private static float relaxPriorityLarge = 1.6f;
        private static float relaxViewportPad = 8f;

        // Camera movement detection
        private static Vector3 lastCameraPosition;
        private static Quaternion lastCameraRotation;
        private static float lastCameraFOV;
        private static float cameraStillTime = 0f;
        private static bool cameraIsMoving = false;
        private static bool wasCameraMoving = false;
        private static bool justStartedMoving = false;
        // Configurable via Settings (Label Relaxation)
        private static float cameraStillThreshold = 0.02f; // Seconds after camera stops before repulsion resumes (committed default)
        
        // Common 4-way neighbor directions
        private static readonly Vector2Int[] Directions4 = new Vector2Int[]
        {
            new Vector2Int(0, 1),   // up
            new Vector2Int(1, 0),   // right
            new Vector2Int(0, -1),  // down
            new Vector2Int(-1, 0)   // left
        };
        
        // Brush painting variables
        private static bool paintMode = false;
        private static string selectedBrushTerrain = "";
        private static int brushSize = 1;
        private static Vector2Int hoveredTile = new Vector2Int(-1, -1);
        private static bool isMouseOverGrid = false;
        
        private const string PREFS_PREFIX = "TerrainPaintTool_";
        
        private const string PREFS_ENABLED = PREFS_PREFIX + "Enabled";
        private const string PREFS_SHOW_GRID = PREFS_PREFIX + "ShowGrid";
        private const string PREFS_TEXT_SIZE = PREFS_PREFIX + "TextSize";
        private const string PREFS_TEXT_COLOR = PREFS_PREFIX + "TextColor";
        private const string PREFS_GRID_COLOR = PREFS_PREFIX + "GridColor";
        private const string PREFS_GRID_THICKNESS = PREFS_PREFIX + "GridThickness";
        private const string PREFS_WORLD_OFFSET = PREFS_PREFIX + "WorldOffset";
        private const string PREFS_TERRAIN_HEIGHTS = PREFS_PREFIX + "TerrainHeights";
        private const string PREFS_SELECTED_TERRAIN = PREFS_PREFIX + "SelectedTerrain";
        private const string PREFS_DISPLAY_MODE = PREFS_PREFIX + "DisplayMode";
        private const string PREFS_COLOR_OPACITY = PREFS_PREFIX + "ColorOpacity";
        private const string PREFS_COLOR_BRIGHTNESS = PREFS_PREFIX + "ColorBrightness";
        // Relaxation settings are committed defaults; no EditorPrefs persistence
        
        private Vector2 scrollPosition;
        private readonly List<TerrainAssetAdapter> availableTerrains = new List<TerrainAssetAdapter>();
        private string[] terrainNames;
        private int selectedIndex = -1;
        private Vector2 paletteScrollPosition;
        private string terrainSearchFilter = "";
        
        private const float TILE_SIZE = 5f;
        private const float LABEL_ICON_SIZE = 8f;
        private const float LABEL_ICON_PADDING = 3f;
        
        
        [MenuItem("Window/Terrain Paint Tool")]
        public static void ShowWindow()
        {
            instance = GetWindow<TerrainPaintToolWindow>("Terrain Paint Tool");
            instance.minSize = new Vector2(300, 400);
        }

        // Called by other editor tools to prevent terrain edits while keeping colors visible
        public static void SetExternalInteractionLocked(bool locked)
        {
            externalInteractionLock = locked;
        }
        
        private void OnEnable()
        {
            instance = this;
            SceneView.duringSceneGui -= OnSceneGUI;
            SceneView.duringSceneGui += OnSceneGUI;
            Undo.undoRedoPerformed -= OnUndoRedo;
            Undo.undoRedoPerformed += OnUndoRedo;
            EditorSceneManager.activeSceneChangedInEditMode -= OnActiveSceneChanged;
            EditorSceneManager.activeSceneChangedInEditMode += OnActiveSceneChanged;
            LoadSettings();
            RefreshTerrainList();
            LoadTerrainDatabase();
        }
        
        private static void OnUndoRedo()
        {
            // Clear island cache when undo/redo is performed
            // This ensures borders are recalculated after terrain changes
            islandCache.Clear();
            lastCachedTerrain = null;
            s_LabelNodes.Clear();
            TerrainVirtualGridCache.ClearAll();
            TerrainRegionCache.ClearAll();
            SceneView.RepaintAll();
            if (instance != null)
            {
                instance.Repaint();
            }
        }
        
        private void LoadTerrainDatabase()
        {
            terrainDatabase = TerrainTypeDatabase.Instance;
            if (terrainDatabase == null || terrainDatabase.Count == 0)
            {
                terrainDatabase = null;
                Debug.LogWarning($"No terrain definitions loaded. Extract terrain.xml.bundle so that '{TerrainTypeDatabase.TerrainXmlAssetRelativePath}' is available.");
                InvalidateTerrainCaches();
                return;
            }

            InvalidateTerrainCaches();

            if (IsEmptyTerrain(selectedBrushTerrain))
            {
                selectedBrushTerrain = string.Empty;
            }
        }
        
        private static Color GetContrastColor(Color backgroundColor)
        {
            // Calculate perceived luminance using the relative luminance formula
            // Using gamma-corrected values for better accuracy
            float r = backgroundColor.r;
            float g = backgroundColor.g;
            float b = backgroundColor.b;
            
            // Apply gamma correction for more accurate luminance calculation
            r = r <= 0.03928f ? r / 12.92f : Mathf.Pow((r + 0.055f) / 1.055f, 2.4f);
            g = g <= 0.03928f ? g / 12.92f : Mathf.Pow((g + 0.055f) / 1.055f, 2.4f);
            b = b <= 0.03928f ? b / 12.92f : Mathf.Pow((b + 0.055f) / 1.055f, 2.4f);
            
            // Calculate relative luminance
            float luminance = 0.2126f * r + 0.7152f * g + 0.0722f * b;
            
            // For mid-range colors, check if we need to add an outline
            if (luminance > 0.4f && luminance < 0.6f)
            {
                // For mid-range brightness, prefer white with black outline (handled in DrawLabelWithColoredIcon)
                return Color.white;
            }
            else if (luminance > 0.45f)
            {
                // For lighter backgrounds, use black
                return Color.black;
            }
            else
            {
                // For darker backgrounds, use white
                return Color.white;
            }
        }
        
    // Resolve label color with auto contrast
        private static Color ResolveLabelColor(string terrainId, bool checkDisplayMode = true)
        {
            if ((!checkDisplayMode || displayMode == DisplayMode.Both) && terrainDatabase != null)
            {
                return GetContrastColor(GetBaseTerrainColor(terrainId));
            }
            return textColor;
        }

        private static void InvalidateTerrainCaches()
        {
            terrainColorCache.Clear();
            paintableTerrainsDirty = true;
            InvalidateTerrainHeightCache(null);
            InvalidateOverlayMesh(null);
            InvalidateGridMesh(null);
        }

        private static void RemoveRaycastFailureEntries(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
            {
                return;
            }

            raycastLogRemovalBuffer.Clear();
            foreach (string key in loggedRaycastFailures)
            {
                string[] parts = key.Split('|');
                if (parts.Length >= 2 && string.Equals(parts[1], assetPath, StringComparison.OrdinalIgnoreCase))
                {
                    raycastLogRemovalBuffer.Add(key);
                }
            }

            foreach (string key in raycastLogRemovalBuffer)
            {
                loggedRaycastFailures.Remove(key);
            }
        }
        private static void InvalidateOverlayMesh(TerrainAssetAdapter terrain)
        {
            if (terrain == null)
            {
                DisposeOverlayMeshes();
                return;
            }

            if (overlayMeshCache.TryGetValue(terrain, out var data))
            {
                if (data.mesh != null)
                {
                    UnityEngine.Object.DestroyImmediate(data.mesh);
                }
                overlayMeshCache.Remove(terrain);
            }
        }

        private static void DisposeOverlayMeshes()
        {
            foreach (var kv in overlayMeshCache)
            {
                if (kv.Value.mesh != null)
                {
                    UnityEngine.Object.DestroyImmediate(kv.Value.mesh);
                }
            }
            overlayMeshCache.Clear();
        }

        private static void EnsureOverlayMaterial()
        {
            if (overlayMaterial != null)
            {
                return;
            }

            Shader shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null)
            {
                shader = Shader.Find("Sprites/Default");
            }

            overlayMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            overlayMaterial.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            overlayMaterial.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            overlayMaterial.SetInt("_Cull", (int)CullMode.Off);
            overlayMaterial.SetInt("_ZWrite", 0);
            overlayMaterial.SetInt("_ZTest", (int)CompareFunction.Always);
        }

        private static void InvalidateGridMesh(TerrainAssetAdapter terrain)
        {
            if (terrain == null)
            {
                DisposeGridMeshes();
                return;
            }

            if (gridMeshCache.TryGetValue(terrain, out var data))
            {
                if (data.mesh != null)
                {
                    UnityEngine.Object.DestroyImmediate(data.mesh);
                }
                gridMeshCache.Remove(terrain);
            }
        }

        private static void DisposeGridMeshes()
        {
            foreach (var kv in gridMeshCache)
            {
                if (kv.Value.mesh != null)
                {
                    UnityEngine.Object.DestroyImmediate(kv.Value.mesh);
                }
            }
            gridMeshCache.Clear();
        }

        private static void PruneMeshCaches(TerrainAssetAdapter activeTerrain)
        {
            if (activeTerrain == null)
            {
                DisposeOverlayMeshes();
                DisposeGridMeshes();
                return;
            }

            meshCacheRemovalBuffer.Clear();
            foreach (var kv in overlayMeshCache)
            {
                if (!Equals(kv.Key, activeTerrain))
                {
                    if (kv.Value?.mesh != null)
                    {
                        UnityEngine.Object.DestroyImmediate(kv.Value.mesh);
                    }
                    meshCacheRemovalBuffer.Add(kv.Key);
                }
            }

            foreach (var key in meshCacheRemovalBuffer)
            {
                overlayMeshCache.Remove(key);
            }

            meshCacheRemovalBuffer.Clear();

            foreach (var kv in gridMeshCache)
            {
                if (!Equals(kv.Key, activeTerrain))
                {
                    if (kv.Value?.mesh != null)
                    {
                        UnityEngine.Object.DestroyImmediate(kv.Value.mesh);
                    }
                    meshCacheRemovalBuffer.Add(kv.Key);
                }
            }

            foreach (var key in meshCacheRemovalBuffer)
            {
                gridMeshCache.Remove(key);
            }

            meshCacheRemovalBuffer.Clear();
        }

        private static Mesh GetGridMesh(TerrainAssetAdapter terrain, TerrainVirtualGrid grid, TerrainHeightCache cache, TerrainHeightSettings settings)
        {
            if (terrain == null || grid == null)
            {
                return null;
            }

            if (!gridMeshCache.TryGetValue(terrain, out var data))
            {
                data = new GridMeshData();
                gridMeshCache[terrain] = data;
            }

            bool needsRebuild = data.mesh == null;
            float currentOffsetX = worldOffset.x;
            float currentOffsetZ = worldOffset.z;
            string currentSceneKey = cache?.sceneKey ?? GetActiveSceneKey();
            string currentColliderPath = cache?.colliderPath ?? string.Empty;
            float currentFixedOffset = settings?.offset ?? 0f;
            float currentThickness = gridThickness;
            float currentWorldThickness = Mathf.Max(0.0005f, currentGridThicknessWorld);
            Color currentColor = gridColor;
            TerrainHeightMode currentMode = settings?.mode ?? TerrainHeightMode.FixedOffset;

            if (!needsRebuild)
            {
                needsRebuild |= data.width != terrain.m_Width || data.height != terrain.m_Height;
                needsRebuild |= !Mathf.Approximately(data.offsetX, currentOffsetX) || !Mathf.Approximately(data.offsetZ, currentOffsetZ);
                needsRebuild |= data.heightMode != currentMode;
                needsRebuild |= data.colliderPath != currentColliderPath;
                needsRebuild |= data.sceneKey != currentSceneKey;
                needsRebuild |= !Mathf.Approximately(data.pixelThickness, currentThickness);
                needsRebuild |= !Mathf.Approximately(data.worldThickness, currentWorldThickness);
                needsRebuild |= data.color != currentColor;
                if (currentMode == TerrainHeightMode.FixedOffset)
                {
                    needsRebuild |= !Mathf.Approximately(data.fixedOffset, currentFixedOffset);
                }
                else
                {
                    needsRebuild |= data.heightCache != cache || cache == null;
                }
            }

            if (needsRebuild)
            {
                BuildGridMesh(data, terrain, grid, cache, settings, currentWorldThickness);
            }

            return data.mesh;
        }

        private static void BuildGridMesh(GridMeshData data, TerrainAssetAdapter terrain, TerrainVirtualGrid grid, TerrainHeightCache cache, TerrainHeightSettings settings, float currentWorldThickness)
        {
            int width = terrain.m_Width;
            int height = terrain.m_Height;
            if (!showGridLines || width <= 0 || height <= 0)
            {
                if (data.mesh != null)
                {
                    data.mesh.Clear();
                }
                data.width = width;
                data.height = height;
                data.vertices = null;
                data.colors = null;
                data.indices = null;
                data.heightCache = cache;
                data.heightMode = settings?.mode ?? TerrainHeightMode.FixedOffset;
                data.offsetX = worldOffset.x;
                data.offsetZ = worldOffset.z;
                data.sceneKey = cache?.sceneKey ?? GetActiveSceneKey();
                data.colliderPath = cache?.colliderPath ?? string.Empty;
                data.fixedOffset = settings?.offset ?? 0f;
                data.pixelThickness = gridThickness;
                data.worldThickness = currentWorldThickness;
                data.color = gridColor;
                return;
            }

            float startX = terrain.m_X + worldOffset.x;
            float startZ = terrain.m_Z + worldOffset.z;
            const float lift = 0.01f;
            float halfThickness = Mathf.Max(0.0005f, currentWorldThickness * 0.5f);

            int horizontalSegments = width * (height + 1);
            int verticalSegments = height * (width + 1);
            int totalSegments = horizontalSegments + verticalSegments;
            int vertexCount = totalSegments * 4;
            int indexCount = totalSegments * 6;

            if (data.vertices == null || data.vertices.Length != vertexCount)
            {
                data.vertices = new Vector3[vertexCount];
            }
            if (data.colors == null || data.colors.Length != vertexCount)
            {
                data.colors = new Color[vertexCount];
            }
            if (data.indices == null || data.indices.Length != indexCount)
            {
                data.indices = new int[indexCount];
            }

            int vertexOffset = 0;
            int indexOffset = 0;
            Color vertexColor = gridColor;

            for (int row = 0; row <= height; row++)
            {
                float z = startZ + row * TILE_SIZE;
                for (int col = 0; col < width; col++)
                {
                    float x0 = startX + col * TILE_SIZE;
                    float x1 = startX + (col + 1) * TILE_SIZE;
                    float hStart = ResolveCornerHeight(cache, settings, col, row) + lift;
                    float hEnd = ResolveCornerHeight(cache, settings, col + 1, row) + lift;
                    Vector3 start = new Vector3(x0, hStart, z);
                    Vector3 end = new Vector3(x1, hEnd, z);
                    AppendGridSegment(data, ref vertexOffset, ref indexOffset, start, end, halfThickness, vertexColor);
                }
            }

            for (int col = 0; col <= width; col++)
            {
                float x = startX + col * TILE_SIZE;
                for (int rowIndex = 0; rowIndex < height; rowIndex++)
                {
                    float z0 = startZ + rowIndex * TILE_SIZE;
                    float z1 = startZ + (rowIndex + 1) * TILE_SIZE;
                    float hStart = ResolveCornerHeight(cache, settings, col, rowIndex) + lift;
                    float hEnd = ResolveCornerHeight(cache, settings, col, rowIndex + 1) + lift;
                    Vector3 start = new Vector3(x, hStart, z0);
                    Vector3 end = new Vector3(x, hEnd, z1);
                    AppendGridSegment(data, ref vertexOffset, ref indexOffset, start, end, halfThickness, vertexColor);
                }
            }

            if (data.mesh == null)
            {
                data.mesh = new Mesh { name = $"TerrainGrid_{terrain.Name}" };
            }

            data.mesh.Clear();
            data.mesh.indexFormat = vertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            data.mesh.vertices = data.vertices;
            data.mesh.colors = data.colors;
            data.mesh.SetIndices(data.indices, MeshTopology.Triangles, 0, false);
            data.mesh.RecalculateBounds();

            data.width = width;
            data.height = height;
            data.offsetX = worldOffset.x;
            data.offsetZ = worldOffset.z;
            data.sceneKey = cache?.sceneKey ?? GetActiveSceneKey();
            data.colliderPath = cache?.colliderPath ?? string.Empty;
            data.heightCache = cache;
            data.heightMode = settings?.mode ?? TerrainHeightMode.FixedOffset;
            data.fixedOffset = settings?.offset ?? 0f;
            data.pixelThickness = gridThickness;
            data.worldThickness = currentWorldThickness;
            data.color = gridColor;
        }

        private static void AppendGridSegment(GridMeshData data, ref int vertexOffset, ref int indexOffset, Vector3 start, Vector3 end, float halfThickness, Color color)
        {
            Vector3 direction = end - start;
            float magnitude = direction.magnitude;
            if (magnitude <= 1e-6f)
            {
                direction = Vector3.right;
                magnitude = 1f;
            }

            Vector3 widthDir = Vector3.Cross(direction / magnitude, Vector3.up);
            if (widthDir.sqrMagnitude <= 1e-6f)
            {
                widthDir = Vector3.Cross(direction / magnitude, Vector3.right);
            }
            widthDir = widthDir.normalized * halfThickness;

            Vector3 v0 = start + widthDir;
            Vector3 v1 = start - widthDir;
            Vector3 v2 = end - widthDir;
            Vector3 v3 = end + widthDir;

            data.vertices[vertexOffset] = v0;
            data.vertices[vertexOffset + 1] = v1;
            data.vertices[vertexOffset + 2] = v2;
            data.vertices[vertexOffset + 3] = v3;

            data.colors[vertexOffset] = color;
            data.colors[vertexOffset + 1] = color;
            data.colors[vertexOffset + 2] = color;
            data.colors[vertexOffset + 3] = color;

            data.indices[indexOffset] = vertexOffset;
            data.indices[indexOffset + 1] = vertexOffset + 1;
            data.indices[indexOffset + 2] = vertexOffset + 2;
            data.indices[indexOffset + 3] = vertexOffset;
            data.indices[indexOffset + 4] = vertexOffset + 2;
            data.indices[indexOffset + 5] = vertexOffset + 3;

            vertexOffset += 4;
            indexOffset += 6;
        }

        private static Mesh GetOverlayMesh(TerrainAssetAdapter terrain, TerrainVirtualGrid grid, TerrainHeightCache cache, TerrainHeightSettings settings)
        {
            if (terrain == null || grid == null || terrainDatabase == null)
            {
                return null;
            }

            if (!overlayMeshCache.TryGetValue(terrain, out var data))
            {
                data = new OverlayMeshData();
                overlayMeshCache[terrain] = data;
            }

            bool needsRebuild = data.mesh == null;

            float currentOffsetX = worldOffset.x;
            float currentOffsetZ = worldOffset.z;
            float currentFixedOffset = settings?.offset ?? 0f;
            string currentSceneKey = cache?.sceneKey ?? GetActiveSceneKey();
            string currentColliderPath = cache?.colliderPath ?? string.Empty;

            if (!needsRebuild)
            {
                needsRebuild |= data.width != terrain.m_Width || data.height != terrain.m_Height;
                needsRebuild |= !Mathf.Approximately(data.offsetX, currentOffsetX) || !Mathf.Approximately(data.offsetZ, currentOffsetZ);
                needsRebuild |= data.heightMode != (settings?.mode ?? TerrainHeightMode.FixedOffset);
                needsRebuild |= data.colorOpacity != colorOpacity || data.colorBrightness != colorBrightness || data.displayMode != displayMode;
                needsRebuild |= data.colliderPath != currentColliderPath;
                needsRebuild |= data.sceneKey != currentSceneKey;
                if (data.heightMode == TerrainHeightMode.FixedOffset)
                {
                    needsRebuild |= !Mathf.Approximately(data.fixedOffset, currentFixedOffset);
                }
                else
                {
                    needsRebuild |= data.heightCache != cache || cache == null;
                }
            }

            if (needsRebuild)
            {
                BuildOverlayMesh(data, terrain, grid, cache, settings);
            }

            return data.mesh;
        }

        private static void BuildOverlayMesh(OverlayMeshData data, TerrainAssetAdapter terrain, TerrainVirtualGrid grid, TerrainHeightCache cache, TerrainHeightSettings settings)
        {
            int width = terrain.m_Width;
            int height = terrain.m_Height;
            int tileCount = width * height;
            if (tileCount <= 0)
            {
                if (data.mesh != null)
                {
                    data.mesh.Clear();
                }
                data.width = width;
                data.height = height;
                data.vertices = null;
                data.colors = null;
                data.indices = null;
                data.heightCache = cache;
                data.heightMode = settings?.mode ?? TerrainHeightMode.FixedOffset;
                data.offsetX = worldOffset.x;
                data.offsetZ = worldOffset.z;
                data.sceneKey = cache?.sceneKey ?? GetActiveSceneKey();
                data.colorOpacity = colorOpacity;
                data.colorBrightness = colorBrightness;
                data.displayMode = displayMode;
                data.colliderPath = cache?.colliderPath ?? string.Empty;
                data.fixedOffset = settings?.offset ?? 0f;
                return;
            }

            int vertexCount = tileCount * 4;
            int indexCount = tileCount * 6;

            if (data.vertices == null || data.vertices.Length != vertexCount)
            {
                data.vertices = new Vector3[vertexCount];
            }
            if (data.colors == null || data.colors.Length != vertexCount)
            {
                data.colors = new Color[vertexCount];
            }
            if (data.indices == null || data.indices.Length != indexCount)
            {
                data.indices = new int[indexCount];
                int tIndex = 0;
                for (int tile = 0; tile < tileCount; tile++)
                {
                    int vBase = tile * 4;
                    data.indices[tIndex++] = vBase;
                    data.indices[tIndex++] = vBase + 2;
                    data.indices[tIndex++] = vBase + 1;
                    data.indices[tIndex++] = vBase;
                    data.indices[tIndex++] = vBase + 3;
                    data.indices[tIndex++] = vBase + 2;
                }
            }

            float startX = terrain.m_X + worldOffset.x;
            float startZ = terrain.m_Z + worldOffset.z;

            int vertexOffset = 0;
            for (int row = 0; row < height; row++)
            {
                for (int col = 0; col < width; col++)
                {
                    float baseX = startX + col * TILE_SIZE;
                    float baseZ = startZ + row * TILE_SIZE;

                    float h00 = ResolveCornerHeight(cache, settings, col, row);
                    float h10 = ResolveCornerHeight(cache, settings, col + 1, row);
                    float h11 = ResolveCornerHeight(cache, settings, col + 1, row + 1);
                    float h01 = ResolveCornerHeight(cache, settings, col, row + 1);

                    data.vertices[vertexOffset] = new Vector3(baseX, h00, baseZ);
                    data.vertices[vertexOffset + 1] = new Vector3(baseX + TILE_SIZE, h10, baseZ);
                    data.vertices[vertexOffset + 2] = new Vector3(baseX + TILE_SIZE, h11, baseZ + TILE_SIZE);
                    data.vertices[vertexOffset + 3] = new Vector3(baseX, h01, baseZ + TILE_SIZE);

                    string terrainId = grid.GetTerrainId(col, row);
                    Color tileColor = IsEmptyTerrain(terrainId) ? new Color(0f, 0f, 0f, 0f) : GetTileFillColor(terrainId);
                    data.colors[vertexOffset] = tileColor;
                    data.colors[vertexOffset + 1] = tileColor;
                    data.colors[vertexOffset + 2] = tileColor;
                    data.colors[vertexOffset + 3] = tileColor;

                    vertexOffset += 4;
                }
            }

            if (data.mesh == null)
            {
                data.mesh = new Mesh { name = $"TerrainOverlay_{terrain.Name}" };
            }

            data.mesh.Clear();
            data.mesh.indexFormat = vertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            data.mesh.vertices = data.vertices;
            data.mesh.colors = data.colors;
            data.mesh.SetIndices(data.indices, MeshTopology.Triangles, 0, false);
            data.mesh.RecalculateBounds();

            data.width = width;
            data.height = height;
            data.heightCache = cache;
            data.heightMode = settings?.mode ?? TerrainHeightMode.FixedOffset;
            data.offsetX = worldOffset.x;
            data.offsetZ = worldOffset.z;
            data.sceneKey = cache?.sceneKey ?? GetActiveSceneKey();
            data.colorOpacity = colorOpacity;
            data.colorBrightness = colorBrightness;
            data.displayMode = displayMode;
            data.colliderPath = cache?.colliderPath ?? string.Empty;
            data.fixedOffset = settings?.offset ?? 0f;
        }


        private static void InvalidateTerrainHeightCache(TerrainAssetAdapter terrain)
        {
            if (terrain == null)
            {
                terrainHeightCache.Clear();
                sceneColliderCache.Clear();
                loggedRaycastFailures.Clear();
                DisposeGridMeshes();
                return;
            }

            terrainHeightCache.Remove(terrain);
            sceneColliderCache.Clear();
            InvalidateOverlayMesh(terrain);
            InvalidateGridMesh(terrain);

            if (terrain?.Asset != null)
            {
                string assetPath = AssetDatabase.GetAssetPath(terrain.Asset);
                RemoveRaycastFailureEntries(assetPath);
            }
        }

        internal static void NotifyTerrainDatabaseChanged()
        {
            InvalidateTerrainCaches();
            if (instance != null)
            {
                instance.Repaint();
            }
            SceneView.RepaintAll();
        }

        internal static Vector3 GetWorldOffset()
        {
            return worldOffset;
        }

        internal static float GetHeightOffsetForTerrain(TerrainAssetAdapter terrain)
        {
            return GetHeightSettings(terrain).offset;
        }

        private static TerrainHeightSettings GetHeightSettings(TerrainAssetAdapter terrain)
        {
            LoadTerrainHeightPreferencesIfNeeded();

            if (terrain?.Asset == null)
            {
                return new TerrainHeightSettings
                {
                    offset = 0f,
                    mode = TerrainHeightMode.FixedOffset
                };
            }

            string path = AssetDatabase.GetAssetPath(terrain.Asset);
            return GetHeightSettingsForPath(path);
        }

        private static TerrainHeightSettings GetHeightSettingsForPath(string assetPath)
        {
            LoadTerrainHeightPreferencesIfNeeded();

            if (string.IsNullOrEmpty(assetPath))
            {
                return new TerrainHeightSettings
                {
                    offset = 0f,
                    mode = TerrainHeightMode.FixedOffset
                };
            }

            if (!terrainHeightSettings.TryGetValue(assetPath, out TerrainHeightSettings settings))
            {
                settings = new TerrainHeightSettings
                {
                    offset = 0f,
                    mode = TerrainHeightMode.FixedOffset
                };
                terrainHeightSettings[assetPath] = settings;
            }

            return settings;
        }

        private static void LoadTerrainHeightPreferencesIfNeeded()
        {
            if (terrainHeightPrefsLoaded)
            {
                return;
            }

            terrainHeightSettings.Clear();
            string json = EditorPrefs.GetString(PREFS_TERRAIN_HEIGHTS, string.Empty);
            if (!string.IsNullOrEmpty(json))
            {
                try
                {
                    var prefs = new TerrainHeightPreferences();
                    EditorJsonUtility.FromJsonOverwrite(json, prefs);
                    if (prefs.paths != null)
                    {
                        int count = prefs.paths.Count;
                        for (int i = 0; i < count; i++)
                        {
                            string path = prefs.paths[i];
                            if (string.IsNullOrEmpty(path))
                            {
                                continue;
                            }

                            float offset = (prefs.heights != null && i < prefs.heights.Count) ? prefs.heights[i] : 0f;
                            TerrainHeightMode mode = TerrainHeightMode.FixedOffset;
                            if (prefs.modes != null && i < prefs.modes.Count)
                            {
                                mode = (TerrainHeightMode)Mathf.Clamp(prefs.modes[i], 0, (int)TerrainHeightMode.RaycastMesh);
                            }

                            bool autoSelect = true;
                            if (prefs.autoColliderFlags != null && i < prefs.autoColliderFlags.Count)
                            {
                                autoSelect = prefs.autoColliderFlags[i] != 0;
                            }

                            string colliderPath = string.Empty;
                            if (prefs.colliderPaths != null && i < prefs.colliderPaths.Count)
                            {
                                colliderPath = prefs.colliderPaths[i] ?? string.Empty;
                            }

                            terrainHeightSettings[path] = new TerrainHeightSettings
                            {
                                offset = offset,
                                mode = mode,
                                autoSelectCollider = autoSelect,
                                colliderPath = colliderPath
                            };
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Failed to load terrain height preferences: {ex.Message}");
                    terrainHeightSettings.Clear();
                }
            }

            terrainHeightPrefsLoaded = true;
        }

        private static void SaveTerrainHeightPreferences()
        {
            var prefs = new TerrainHeightPreferences();
            foreach (var kvp in terrainHeightSettings)
            {
                prefs.paths.Add(kvp.Key);
                prefs.heights.Add(kvp.Value.offset);
                prefs.modes.Add((int)kvp.Value.mode);
                prefs.autoColliderFlags.Add(kvp.Value.autoSelectCollider ? 1 : 0);
                prefs.colliderPaths.Add(kvp.Value.colliderPath ?? string.Empty);
            }

            string json = EditorJsonUtility.ToJson(prefs);
            EditorPrefs.SetString(PREFS_TERRAIN_HEIGHTS, json);
        }

        private static void ApplyTerrainHeightForSelection()
        {
            worldOffset.y = GetHeightOffsetForTerrain(selectedTerrain);
        }

        private static void StoreCurrentTerrainHeight()
        {
            if (selectedTerrain?.Asset == null)
            {
                return;
            }

            string path = AssetDatabase.GetAssetPath(selectedTerrain.Asset);
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            TerrainHeightSettings settings = GetHeightSettingsForPath(path);
            settings.offset = worldOffset.y;
            terrainHeightSettings[path] = settings;
            SaveTerrainHeightPreferences();
        }

        private static void SetHeightModeForTerrain(TerrainAssetAdapter terrain, TerrainHeightMode mode)
        {
            if (terrain?.Asset == null)
            {
                return;
            }

            string path = AssetDatabase.GetAssetPath(terrain.Asset);
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            TerrainHeightSettings settings = GetHeightSettingsForPath(path);
            if (settings.mode == mode)
            {
                return;
            }

            settings.mode = mode;
            terrainHeightSettings[path] = settings;
            InvalidateTerrainHeightCache(terrain);
            RemoveRaycastFailureEntries(path);
            sceneColliderCache.Clear();
            SaveTerrainHeightPreferences();
        }

        private static void SetColliderSelectionForTerrain(TerrainAssetAdapter terrain, bool autoSelect, string colliderPath)
        {
            if (terrain?.Asset == null)
            {
                return;
            }

            string path = AssetDatabase.GetAssetPath(terrain.Asset);
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            TerrainHeightSettings settings = GetHeightSettingsForPath(path);
            string normalizedPath = autoSelect ? string.Empty : (colliderPath ?? string.Empty);
            if (settings.autoSelectCollider == autoSelect && string.Equals(settings.colliderPath ?? string.Empty, normalizedPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            settings.autoSelectCollider = autoSelect;
            settings.colliderPath = normalizedPath;
            terrainHeightSettings[path] = settings;
            InvalidateTerrainHeightCache(terrain);
            RemoveRaycastFailureEntries(path);
            sceneColliderCache.Clear();
            SaveTerrainHeightPreferences();
        }

        private static void OnActiveSceneChanged(Scene previousScene, Scene newScene)
        {
            InvalidateTerrainHeightCache(null);
            SceneView.RepaintAll();
            DisposToolWindow.RequestRepaintAll(repaintScene: true);
        }

        private static string GetSceneKey(Scene scene)
        {
            if (!scene.IsValid())
            {
                return string.Empty;
            }

            return !string.IsNullOrEmpty(scene.path) ? scene.path : scene.name;
        }

        private static string GetActiveSceneKey()
        {
            return GetSceneKey(SceneManager.GetActiveScene());
        }

        private static string GetColliderSelectionKey(TerrainHeightSettings settings)
        {
            if (settings == null || settings.autoSelectCollider || string.IsNullOrEmpty(settings.colliderPath))
            {
                return "<auto>";
            }

            return settings.colliderPath;
        }

        private static MeshCollider GetSceneMeshCollider(Scene scene, TerrainHeightSettings settings, TerrainAssetAdapter terrain, bool logWarnings, out string colliderPath)
        {
            colliderPath = string.Empty;
            string sceneKey = GetSceneKey(scene);
            string selectionKey = GetColliderSelectionKey(settings);
            string cacheKey = sceneKey + "|" + selectionKey;

            if (sceneColliderCache.TryGetValue(cacheKey, out MeshCollider cached) && cached != null)
            {
                colliderPath = GetTransformPath(cached.transform);
                return cached;
            }

            sceneColliderCache.Remove(cacheKey);

            MeshCollider collider = null;

            if (settings != null && !settings.autoSelectCollider && !string.IsNullOrEmpty(settings.colliderPath))
            {
                collider = FindMeshColliderByPath(scene, settings.colliderPath);
                if (collider == null && logWarnings)
                {
                    LogRaycastFailure(terrain, sceneKey, settings.colliderPath, $"MeshCollider '{settings.colliderPath}' not found; falling back to auto selection.");
                }
            }

            if (collider == null)
            {
                collider = FindDefaultMeshCollider(scene);
                if (collider == null && logWarnings)
                {
                    LogRaycastFailure(terrain, sceneKey, string.Empty, "No MeshCollider found in the active scene. Using fixed height instead.");
                }
            }

            if (collider != null)
            {
                colliderPath = GetTransformPath(collider.transform);

                bool cacheResult = true;
                if (settings != null && !settings.autoSelectCollider && !string.IsNullOrEmpty(settings.colliderPath))
                {
                    if (!string.Equals(colliderPath, settings.colliderPath, StringComparison.OrdinalIgnoreCase))
                    {
                        cacheResult = false;
                    }
                }

                if (cacheResult)
                {
                    sceneColliderCache[cacheKey] = collider;
                }
            }

            return collider;
        }

        private static MeshCollider FindMeshColliderByPath(Scene scene, string colliderPath)
        {
            if (string.IsNullOrEmpty(colliderPath))
            {
                return null;
            }

            MeshCollider[] colliders = UnityEngine.Object.FindObjectsOfType<MeshCollider>(true);
            foreach (MeshCollider collider in colliders)
            {
                if (collider == null || collider.sharedMesh == null)
                {
                    continue;
                }

                if (!collider.gameObject.scene.IsValid() || collider.gameObject.scene != scene)
                {
                    continue;
                }

                string path = GetTransformPath(collider.transform);
                if (string.Equals(path, colliderPath, StringComparison.OrdinalIgnoreCase))
                {
                    return collider;
                }
            }

            return null;
        }

        private static MeshCollider FindDefaultMeshCollider(Scene scene)
        {
            MeshCollider fallback = null;

            MeshCollider[] colliders = UnityEngine.Object.FindObjectsOfType<MeshCollider>(true);
            foreach (MeshCollider collider in colliders)
            {
                if (collider == null || collider.sharedMesh == null)
                {
                    continue;
                }

                if (!collider.gameObject.scene.IsValid() || collider.gameObject.scene != scene)
                {
                    continue;
                }

                if (!collider.enabled)
                {
                    continue;
                }

                if (IsUnderBmap(collider.transform))
                {
                    return collider;
                }

                if (fallback == null)
                {
                    fallback = collider;
                }
            }

            return fallback;
        }

        private static List<MeshCollider> GetSceneColliders(Scene scene)
        {
            MeshCollider[] colliders = UnityEngine.Object.FindObjectsOfType<MeshCollider>(true);
            List<MeshCollider> results = new List<MeshCollider>();
            foreach (MeshCollider collider in colliders)
            {
                if (collider == null || collider.sharedMesh == null)
                {
                    continue;
                }

                if (!collider.gameObject.scene.IsValid() || collider.gameObject.scene != scene)
                {
                    continue;
                }

                if (!collider.enabled)
                {
                    continue;
                }

                results.Add(collider);
            }

            results.Sort((a, b) => string.Compare(GetTransformPath(a.transform), GetTransformPath(b.transform), StringComparison.OrdinalIgnoreCase));
            return results;
        }

        private static bool IsUnderBmap(Transform transform)
        {
            Transform current = transform;
            while (current != null)
            {
                if (string.Equals(current.name, "Bmap", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                current = current.parent;
            }

            return false;
        }

        private static string GetTransformPath(Transform transform)
        {
            if (transform == null)
            {
                return string.Empty;
            }

            List<string> segments = new List<string>();
            Transform current = transform;
            while (current != null)
            {
                segments.Add(current.name);
                current = current.parent;
            }
            segments.Reverse();
            return string.Join("/", segments);
        }

        private static string BuildRaycastLogKey(TerrainAssetAdapter terrain, string sceneKey, string colliderPath)
        {
            string path = string.Empty;
            if (terrain?.Asset != null)
            {
                path = AssetDatabase.GetAssetPath(terrain.Asset);
            }

            string terrainSegment = path ?? string.Empty;
            string sceneSegment = sceneKey ?? string.Empty;
            string colliderSegment = colliderPath ?? string.Empty;
            return sceneSegment + "|" + terrainSegment + "|" + colliderSegment;
        }

        private static void LogRaycastFailure(TerrainAssetAdapter terrain, string sceneKey, string colliderPath, string message)
        {
            string key = BuildRaycastLogKey(terrain, sceneKey, colliderPath);
            if (loggedRaycastFailures.Contains(key))
            {
                return;
            }

            string terrainName = terrain?.Name ?? "<none>";
            string colliderInfo = string.IsNullOrEmpty(colliderPath) ? "<auto>" : colliderPath;
            string composedMessage = $"[Terrain Painter] {message} (Scene: {sceneKey}, Terrain: {terrainName}, Collider: {colliderInfo})";
            Debug.LogWarning(composedMessage);
            loggedRaycastFailures.Add(key);
        }

        private static bool IsHeightCacheValid(TerrainAssetAdapter terrain, TerrainHeightSettings settings, TerrainHeightCache cache)
        {
            if (terrain == null || settings == null)
            {
                return false;
            }

            if (settings.mode != TerrainHeightMode.RaycastMesh)
            {
                return false;
            }

            if (cache == null || cache.centerSamples == null || cache.cornerSamples == null)
            {
                return false;
            }

            if (cache.mode != settings.mode)
            {
                return false;
            }

            if (cache.autoSelection != settings.autoSelectCollider)
            {
                return false;
            }

            if (!cache.autoSelection)
            {
                if (!string.Equals(cache.requestedColliderPath ?? string.Empty, settings.colliderPath ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            if (cache.width != terrain.m_Width || cache.height != terrain.m_Height)
            {
                return false;
            }

            if (!Mathf.Approximately(cache.originX, terrain.m_X) || !Mathf.Approximately(cache.originZ, terrain.m_Z))
            {
                return false;
            }

            if (!Mathf.Approximately(cache.offsetX, worldOffset.x) || !Mathf.Approximately(cache.offsetZ, worldOffset.z))
            {
                return false;
            }

            if (!string.Equals(cache.sceneKey, GetActiveSceneKey(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (cache.centerSamples.Length != cache.width * cache.height)
            {
                return false;
            }

            if (cache.cornerSamples.Length != (cache.width + 1) * (cache.height + 1))
            {
                return false;
            }

            return true;
        }

        private static TerrainHeightCache GetOrBuildTerrainHeightCache(TerrainAssetAdapter terrain, TerrainHeightSettings settings)
        {
            if (terrain == null || settings == null || settings.mode != TerrainHeightMode.RaycastMesh)
            {
                return null;
            }

            if (!terrainHeightCache.TryGetValue(terrain, out TerrainHeightCache cache) || !IsHeightCacheValid(terrain, settings, cache))
            {
                cache = BuildTerrainHeightCache(terrain, settings);
                terrainHeightCache[terrain] = cache;
            }

            return cache;
        }

        private static TerrainHeightCache BuildTerrainHeightCache(TerrainAssetAdapter terrain, TerrainHeightSettings settings)
        {
            var cache = new TerrainHeightCache
            {
                mode = settings.mode,
                width = terrain.m_Width,
                height = terrain.m_Height,
                originX = terrain.m_X,
                originZ = terrain.m_Z,
                offsetX = worldOffset.x,
                offsetZ = worldOffset.z,
                sceneKey = GetActiveSceneKey()
            };

            int centerCount = Mathf.Max(0, cache.width * cache.height);
            cache.centerSamples = new float[centerCount];
            for (int i = 0; i < centerCount; i++)
            {
                cache.centerSamples[i] = float.NaN;
            }

            int cornerCount = (cache.width + 1) * (cache.height + 1);
            cache.cornerSamples = new float[cornerCount];
            for (int i = 0; i < cornerCount; i++)
            {
                cache.cornerSamples[i] = float.NaN;
            }

            Scene activeScene = SceneManager.GetActiveScene();
            string usedColliderPath;
            MeshCollider collider = GetSceneMeshCollider(activeScene, settings, terrain, true, out usedColliderPath);

            cache.autoSelection = settings?.autoSelectCollider ?? true;
            cache.requestedColliderPath = settings?.colliderPath ?? string.Empty;
            cache.colliderPath = usedColliderPath ?? string.Empty;

            if (collider == null)
            {
                return cache;
            }

            Bounds bounds = collider.bounds;
            float topY = bounds.max.y + 100f;
            float bottomY = bounds.min.y - 100f;
            float maxDistance = Mathf.Max(1f, topY - bottomY);
            float startX = terrain.m_X + worldOffset.x;
            float startZ = terrain.m_Z + worldOffset.z;
            float halfTile = TILE_SIZE * 0.5f;

            for (int row = 0; row <= cache.height; row++)
            {
                for (int col = 0; col <= cache.width; col++)
                {
                    int cornerIndex = row * (cache.width + 1) + col;
                    float cornerX = startX + col * TILE_SIZE;
                    float cornerZ = startZ + row * TILE_SIZE;

                    if (TrySampleHeight(collider, topY, bottomY, maxDistance, cornerX, cornerZ, out float cornerHeight))
                    {
                        cache.cornerSamples[cornerIndex] = cornerHeight;
                        cache.anyCornerHits = true;
                    }
                }
            }

            for (int row = 0; row < cache.height; row++)
            {
                for (int col = 0; col < cache.width; col++)
                {
                    int centerIndex = row * cache.width + col;
                    float centerX = startX + col * TILE_SIZE + halfTile;
                    float centerZ = startZ + row * TILE_SIZE + halfTile;

                    if (TrySampleHeight(collider, topY, bottomY, maxDistance, centerX, centerZ, out float centerHeight))
                    {
                        cache.centerSamples[centerIndex] = centerHeight;
                        cache.anyCenterHits = true;
                    }
                }
            }

            bool anyHits = cache.anyCenterHits || cache.anyCornerHits;
            string logKey = BuildRaycastLogKey(terrain, cache.sceneKey, cache.colliderPath);
            if (anyHits)
            {
                loggedRaycastFailures.Remove(logKey);
            }
            else
            {
                LogRaycastFailure(terrain, cache.sceneKey, cache.colliderPath, "Mesh raycasts hit nothing across the sampled terrain tiles. Using fixed height instead.");
            }

            return cache;
        }

        private static bool TrySampleHeight(MeshCollider collider, float topY, float bottomY, float maxDistance, float x, float z, out float height)
        {
            Vector3 downOrigin = new Vector3(x, topY, z);
            Ray downRay = new Ray(downOrigin, Vector3.down);
            if (collider.Raycast(downRay, out RaycastHit hit, maxDistance))
            {
                height = hit.point.y;
                return true;
            }

            Vector3 upOrigin = new Vector3(x, bottomY, z);
            Ray upRay = new Ray(upOrigin, Vector3.up);
            if (collider.Raycast(upRay, out hit, maxDistance))
            {
                height = hit.point.y;
                return true;
            }

            height = float.NaN;
            return false;
        }

        private static float ResolveTileHeight(TerrainHeightCache cache, TerrainHeightSettings settings, int col, int row)
        {
            if (settings == null)
            {
                return worldOffset.y;
            }

            if (cache != null && cache.centerSamples != null && cache.width > 0 && cache.height > 0)
            {
                if (col >= 0 && col < cache.width && row >= 0 && row < cache.height)
                {
                    float sample = cache.centerSamples[row * cache.width + col];
                    if (!float.IsNaN(sample))
                    {
                        return sample + settings.offset;
                    }
                }
            }

            return settings.offset;
        }

        private static float ResolveCornerHeight(TerrainHeightCache cache, TerrainHeightSettings settings, int col, int row)
        {
            if (settings == null)
            {
                return worldOffset.y;
            }

            if (cache != null && cache.cornerSamples != null && cache.width >= 0 && cache.height >= 0)
            {
                int maxCol = cache.width;
                int maxRow = cache.height;
                if (col >= 0 && col <= maxCol && row >= 0 && row <= maxRow)
                {
                    int index = row * (maxCol + 1) + col;
                    if (index >= 0 && index < cache.cornerSamples.Length)
                    {
                        float sample = cache.cornerSamples[index];
                        if (!float.IsNaN(sample))
                        {
                            return sample + settings.offset;
                        }
                    }
                }
            }

            int fallbackCol = cache != null ? Mathf.Clamp(col, 0, Mathf.Max(cache.width - 1, 0)) : 0;
            int fallbackRow = cache != null ? Mathf.Clamp(row, 0, Mathf.Max(cache.height - 1, 0)) : 0;
            return ResolveTileHeight(cache, settings, fallbackCol, fallbackRow);
        }

        internal static float GetTileWorldHeight(TerrainAssetAdapter terrain, int col, int row)
        {
            TerrainHeightSettings settings = GetHeightSettings(terrain);
            TerrainHeightCache cache = settings.mode == TerrainHeightMode.RaycastMesh ? GetOrBuildTerrainHeightCache(terrain, settings) : null;
            return ResolveTileHeight(cache, settings, col, row);
        }

        internal static float GetTileCornerWorldHeight(TerrainAssetAdapter terrain, int cornerCol, int cornerRow)
        {
            TerrainHeightSettings settings = GetHeightSettings(terrain);
            TerrainHeightCache cache = settings.mode == TerrainHeightMode.RaycastMesh ? GetOrBuildTerrainHeightCache(terrain, settings) : null;
            return ResolveCornerHeight(cache, settings, cornerCol, cornerRow);
        }

        private static Color GetBaseTerrainColor(string terrainId)
        {
            if (terrainDatabase == null || string.IsNullOrEmpty(terrainId))
            {
                return Color.gray;
            }

            if (!terrainColorCache.TryGetValue(terrainId, out Color baseColor))
            {
                baseColor = terrainDatabase.GetTerrainColor(terrainId, Color.gray);
                terrainColorCache[terrainId] = baseColor;
            }

            return baseColor;
        }

        private static Color GetTileFillColor(string terrainId)
        {
            Color color = GetBaseTerrainColor(terrainId);
            color.r = Mathf.Clamp01(color.r * colorBrightness);
            color.g = Mathf.Clamp01(color.g * colorBrightness);
            color.b = Mathf.Clamp01(color.b * colorBrightness);
            color.a = colorOpacity;
            return color;
        }

        private static Color GetBorderColor(string terrainId)
        {
            Color color = GetTileFillColor(terrainId);
            return new Color(color.r * 0.6f, color.g * 0.6f, color.b * 0.6f, 1f);
        }

        private static void FillQuad(Vector3[] buffer, float x, float z, float y, float size = TILE_SIZE, float yOffset = 0f)
        {
            float baseY = y + yOffset;
            buffer[0] = new Vector3(x, baseY, z);
            buffer[1] = new Vector3(x + size, baseY, z);
            buffer[2] = new Vector3(x + size, baseY, z + size);
            buffer[3] = new Vector3(x, baseY, z + size);
        }

        private static void FillQuadWithCornerHeights(Vector3[] buffer, float x, float z, float size, float h00, float h10, float h11, float h01)
        {
            buffer[0] = new Vector3(x, h00, z);
            buffer[1] = new Vector3(x + size, h10, z);
            buffer[2] = new Vector3(x + size, h11, z + size);
            buffer[3] = new Vector3(x, h01, z + size);
        }

        private static void FillTileQuad(Vector3[] buffer, float startX, float startZ, int col, int row, TerrainHeightCache cache, TerrainHeightSettings settings, float fallbackY, float yOffset = 0f)
        {
            float baseX = startX + col * TILE_SIZE;
            float baseZ = startZ + row * TILE_SIZE;

            if (settings != null && settings.mode == TerrainHeightMode.RaycastMesh && cache != null)
            {
                float h00 = ResolveCornerHeight(cache, settings, col, row) + yOffset;
                float h10 = ResolveCornerHeight(cache, settings, col + 1, row) + yOffset;
                float h11 = ResolveCornerHeight(cache, settings, col + 1, row + 1) + yOffset;
                float h01 = ResolveCornerHeight(cache, settings, col, row + 1) + yOffset;
                FillQuadWithCornerHeights(buffer, baseX, baseZ, TILE_SIZE, h00, h10, h11, h01);
                return;
            }

            FillQuad(buffer, baseX, baseZ, fallbackY, TILE_SIZE, yOffset);
        }

        // Modifier detection for sampling (support Ctrl and Cmd)
        private static bool IsSamplingModifier(Event e)
        {
            return e != null && (e.control || e.command);
        }

        private static bool IsEmptyTerrain(string terrainId) => TerrainVirtualGridCache.IsEmptyTerrain(terrainId);

        private static TerrainVirtualGrid GetVirtualGrid(TerrainAssetAdapter terrain) => TerrainVirtualGridCache.GetGrid(terrain);

        private static void InvalidateVirtualGrid(TerrainAssetAdapter terrain) => TerrainVirtualGridCache.Invalidate(terrain);

        private static void RecordTerrainUndo(TerrainAssetAdapter terrain, string label)
        {
            if (terrain?.Asset != null)
            {
                Undo.RecordObject(terrain.Asset, label);
            }
        }

        private static void MarkTerrainDirty(TerrainAssetAdapter terrain)
        {
            if (terrain?.Asset != null)
            {
                EditorUtility.SetDirty(terrain.Asset);
            }

            if (terrain != null)
            {
                islandCache.Remove(terrain);
                InvalidateVirtualGrid(terrain);
                TerrainRegionCache.Invalidate(terrain);
                InvalidateTerrainHeightCache(terrain);
                InvalidateOverlayMesh(terrain);
            }
        }

        // Per-session caches
        private static readonly Dictionary<string, Color> terrainColorCache = new Dictionary<string, Color>();
        private static readonly List<TerrainType> paintableTerrainsCache = new List<TerrainType>();
        private static bool paintableTerrainsDirty = true;
        private static readonly Dictionary<string, string> s_LabelTextCache = new Dictionary<string, string>(64);
        private static readonly HashSet<string> s_LabelFrameKeys = new HashSet<string>();
        private static readonly List<LabelNode> s_FrameLabelNodes = new List<LabelNode>(64);
        private static readonly List<string> s_LabelRemovalBuffer = new List<string>(32);
        private static readonly Vector3[] s_TileVertices = new Vector3[4];
        private static readonly Vector3[] s_TileVerticesOverlay = new Vector3[4];
        private static GUIContent s_LabelContent = new GUIContent();
        private static System.Collections.Generic.Dictionary<string,float> labelAlphaStates = new System.Collections.Generic.Dictionary<string,float>();

        [Serializable]
        private class TerrainHeightPreferences
        {
            public List<string> paths = new List<string>();
            public List<float> heights = new List<float>();
            public List<int> modes = new List<int>();
            public List<int> autoColliderFlags = new List<int>();
            public List<string> colliderPaths = new List<string>();
        }
        
        private void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            Undo.undoRedoPerformed -= OnUndoRedo;
            EditorSceneManager.activeSceneChangedInEditMode -= OnActiveSceneChanged;
            DisposeOverlayMeshes();
            DisposeGridMeshes();
            if (overlayMaterial != null)
            {
                UnityEngine.Object.DestroyImmediate(overlayMaterial);
                overlayMaterial = null;
            }
        }

        private void OnDestroy()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            Undo.undoRedoPerformed -= OnUndoRedo;
            EditorSceneManager.activeSceneChangedInEditMode -= OnActiveSceneChanged;
            DisposeOverlayMeshes();
            DisposeGridMeshes();
            if (overlayMaterial != null)
            {
                UnityEngine.Object.DestroyImmediate(overlayMaterial);
                overlayMaterial = null;
            }
        }
        
        private void LoadSettings()
        {
            LoadTerrainHeightPreferencesIfNeeded();
            visualizationEnabled = EditorPrefs.GetBool(PREFS_ENABLED, true);
            showGridLines = EditorPrefs.GetBool(PREFS_SHOW_GRID, true);
            textSize = EditorPrefs.GetFloat(PREFS_TEXT_SIZE, 1.5f);
            gridThickness = EditorPrefs.GetFloat(PREFS_GRID_THICKNESS, 1f);
            displayMode = (DisplayMode)EditorPrefs.GetInt(PREFS_DISPLAY_MODE, (int)DisplayMode.Both);
            colorOpacity = EditorPrefs.GetFloat(PREFS_COLOR_OPACITY, 0.5f);
            colorBrightness = EditorPrefs.GetFloat(PREFS_COLOR_BRIGHTNESS, 1.0f);
            
            string colorStr = EditorPrefs.GetString(PREFS_TEXT_COLOR, ColorUtility.ToHtmlStringRGBA(Color.white));
            ColorUtility.TryParseHtmlString("#" + colorStr, out textColor);
            
            colorStr = EditorPrefs.GetString(PREFS_GRID_COLOR, ColorUtility.ToHtmlStringRGBA(new Color(1f, 1f, 1f, 0.3f)));
            ColorUtility.TryParseHtmlString("#" + colorStr, out gridColor);
            
            float x = EditorPrefs.GetFloat(PREFS_WORLD_OFFSET + "_X", 0);
            float y = EditorPrefs.GetFloat(PREFS_WORLD_OFFSET + "_Y", 0);
            float z = EditorPrefs.GetFloat(PREFS_WORLD_OFFSET + "_Z", 0);
            worldOffset = new Vector3(x, y, z);
            
            string terrainPath = EditorPrefs.GetString(PREFS_SELECTED_TERRAIN, "");
            if (!string.IsNullOrEmpty(terrainPath))
            {
                selectedTerrain = TerrainAssetAdapter.Load(terrainPath);
                lastCachedTerrain = null;
                InvalidateVirtualGrid(selectedTerrain);
                TerrainRegionCache.ClearAll();
                s_LabelNodes.Clear();
                labelAlphaStates.Clear();
            }

            PruneMeshCaches(selectedTerrain);

            ApplyTerrainHeightForSelection();

            // Relaxation: committed defaults (no prefs load)
        }
        
        private void SaveSettings()
        {
            StoreCurrentTerrainHeight();
            EditorPrefs.SetBool(PREFS_ENABLED, visualizationEnabled);
            EditorPrefs.SetBool(PREFS_SHOW_GRID, showGridLines);
            EditorPrefs.SetFloat(PREFS_TEXT_SIZE, textSize);
            EditorPrefs.SetFloat(PREFS_GRID_THICKNESS, gridThickness);
            EditorPrefs.SetInt(PREFS_DISPLAY_MODE, (int)displayMode);
            EditorPrefs.SetFloat(PREFS_COLOR_OPACITY, colorOpacity);
            EditorPrefs.SetFloat(PREFS_COLOR_BRIGHTNESS, colorBrightness);
            EditorPrefs.SetString(PREFS_TEXT_COLOR, ColorUtility.ToHtmlStringRGBA(textColor));
            EditorPrefs.SetString(PREFS_GRID_COLOR, ColorUtility.ToHtmlStringRGBA(gridColor));
            EditorPrefs.SetFloat(PREFS_WORLD_OFFSET + "_X", worldOffset.x);
            EditorPrefs.SetFloat(PREFS_WORLD_OFFSET + "_Y", worldOffset.y);
            EditorPrefs.SetFloat(PREFS_WORLD_OFFSET + "_Z", worldOffset.z);
            
            if (selectedTerrain?.Asset != null)
            {
                string path = AssetDatabase.GetAssetPath(selectedTerrain.Asset);
                EditorPrefs.SetString(PREFS_SELECTED_TERRAIN, path);
            }
            else
            {
                EditorPrefs.SetString(PREFS_SELECTED_TERRAIN, "");
            }

            // Relaxation: committed defaults (no prefs save)
        }
        
        private void RefreshTerrainList()
        {
            availableTerrains.Clear();
            
            string[] guids = AssetDatabase.FindAssets("t:MapTerrain");
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                TerrainAssetAdapter terrain = TerrainAssetAdapter.Load(path);
                if (terrain != null && terrain.IsValid)
                {
                    availableTerrains.Add(terrain);
                }
            }
            
            terrainNames = new string[availableTerrains.Count];
            selectedIndex = -1;
            for (int i = 0; i < availableTerrains.Count; i++)
            {
                terrainNames[i] = availableTerrains[i].Name;
                if (selectedTerrain != null && selectedTerrain.Equals(availableTerrains[i]))
                {
                    selectedIndex = i;
                }
            }
        }
        
        private static int uiTabIndex = 0; // 0 = Main, 1 = Settings, 2 = Advanced

        private void OnGUI()
        {
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
            
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Terrain Paint Tool", EditorStyles.boldLabel);
            EditorGUILayout.Space(5);

            // Top-level tabs
            uiTabIndex = GUILayout.Toolbar(uiTabIndex, new[] { "Main", "Settings", "Advanced" });
            EditorGUILayout.Space(6);
            // MAIN PAGE header controls
            if (uiTabIndex == 0)
            {
                // Enable Visualization is always visible
                EditorGUI.BeginChangeCheck();
                visualizationEnabled = EditorGUILayout.Toggle("Enable Visualization", visualizationEnabled);
                if (EditorGUI.EndChangeCheck())
                {
                    SaveSettings();
                    SceneView.RepaintAll();
                }
                
                // Terrain Selection (always visible)
                EditorGUILayout.Space(10);
                EditorGUILayout.LabelField("Terrain Selection", EditorStyles.boldLabel);
                
                Type terrainType = TerrainAssetAdapter.MapTerrainType ?? typeof(ScriptableObject);
                ScriptableObject currentAsset = selectedTerrain?.Asset;
                EditorGUI.BeginChangeCheck();
                ScriptableObject newAsset = (ScriptableObject)EditorGUILayout.ObjectField(
                    "Selected Terrain",
                    currentAsset,
                    terrainType,
                    false);
                if (EditorGUI.EndChangeCheck())
                {
                    selectedTerrain = TerrainAssetAdapter.FromObject(newAsset);
                    PruneMeshCaches(selectedTerrain);
                    ApplyTerrainHeightForSelection();
                    lastCachedTerrain = null;
                    InvalidateVirtualGrid(selectedTerrain);
                    TerrainRegionCache.ClearAll();
                    s_LabelNodes.Clear();
                    labelAlphaStates.Clear();
                    for (int i = 0; i < availableTerrains.Count; i++)
                    {
                        if (selectedTerrain != null &&
                            availableTerrains[i]?.Asset == selectedTerrain.Asset)
                        {
                            selectedIndex = i;
                            break;
                        }
                    }
                    SaveSettings();
                    SceneView.RepaintAll();
                }
                
                EditorGUILayout.Space(5);
                
                if (GUILayout.Button("Refresh Terrain List"))
                {
                    RefreshTerrainList();
                }
                
                if (availableTerrains.Count > 0)
                {
                    EditorGUI.BeginChangeCheck();
                    selectedIndex = EditorGUILayout.Popup("Quick Select", selectedIndex, terrainNames);
                    if (EditorGUI.EndChangeCheck() && selectedIndex >= 0 && selectedIndex < availableTerrains.Count)
                    {
                        selectedTerrain = availableTerrains[selectedIndex];
                        PruneMeshCaches(selectedTerrain);
                        ApplyTerrainHeightForSelection();
                        SaveSettings();
                        SceneView.RepaintAll();
                    }
                }
                
                if (selectedTerrain != null)
                {
                    EditorGUILayout.Space(5);
                    EditorGUILayout.HelpBox($"Grid Size: {selectedTerrain.Width} x {selectedTerrain.Height}\n" +
                                           $"Origin: ({selectedTerrain.OriginX}, {selectedTerrain.OriginZ})\n" +
                                           $"Total Tiles: {selectedTerrain.Width * selectedTerrain.Height}", 
                                           MessageType.Info);

                    TerrainHeightSettings heightSettings = GetHeightSettings(selectedTerrain);

                    EditorGUI.BeginChangeCheck();
                    TerrainHeightMode newMode = (TerrainHeightMode)EditorGUILayout.EnumPopup(
                        new GUIContent("Height Mode", "Choose how overlay heights are computed: a fixed offset plane or raycasts against the active scene mesh."),
                        heightSettings.mode);
                    if (EditorGUI.EndChangeCheck())
                    {
                        SetHeightModeForTerrain(selectedTerrain, newMode);
                        SceneView.RepaintAll();
                        DisposToolWindow.RequestRepaintAll(repaintScene: true);
                        heightSettings = GetHeightSettings(selectedTerrain);
                    }

                    EditorGUI.BeginChangeCheck();
                    float newHeight = EditorGUILayout.FloatField(
                        new GUIContent("Height Offset", "Vertical offset applied after sampling. Positive values lift the overlay above the base height."),
                        heightSettings.offset);
                    if (EditorGUI.EndChangeCheck())
                    {
                        worldOffset.y = newHeight;
                        heightSettings.offset = newHeight;
                        SaveSettings();
                        SceneView.RepaintAll();
                        DisposToolWindow.RequestRepaintAll(repaintScene: true);
                    }

                    if (heightSettings.mode == TerrainHeightMode.RaycastMesh)
                    {
                        EditorGUI.indentLevel++;

                        bool autoSelect = heightSettings.autoSelectCollider;
                        EditorGUI.BeginChangeCheck();
                        bool newAutoSelect = EditorGUILayout.Toggle(new GUIContent("Auto Select Collider", "Use the default scene collider (preferring meshes under Bmap) for raycast sampling."), autoSelect);
                        if (EditorGUI.EndChangeCheck())
                        {
                            SetColliderSelectionForTerrain(selectedTerrain, newAutoSelect, heightSettings.colliderPath);
                            heightSettings = GetHeightSettings(selectedTerrain);
                            SceneView.RepaintAll();
                            DisposToolWindow.RequestRepaintAll(repaintScene: true);
                        }

                        Scene currentScene = SceneManager.GetActiveScene();
                        List<MeshCollider> sceneColliders = GetSceneColliders(currentScene);

                        if (!heightSettings.autoSelectCollider)
                        {
                            MeshCollider currentManualCollider = !string.IsNullOrEmpty(heightSettings.colliderPath)
                                ? FindMeshColliderByPath(currentScene, heightSettings.colliderPath)
                                : null;
                            GameObject currentColliderObject = currentManualCollider != null ? currentManualCollider.gameObject : null;

                            List<(string message, MessageType type)> colliderMessages = new List<(string, MessageType)>();

                            EditorGUI.BeginChangeCheck();
                            GameObject selectedColliderObject = (GameObject)EditorGUILayout.ObjectField(
                                new GUIContent("Mesh Collider Object", "Select a GameObject with a MeshCollider in the active scene."),
                                currentColliderObject,
                                typeof(GameObject),
                                true);
                            if (EditorGUI.EndChangeCheck())
                            {
                                if (selectedColliderObject == null)
                                {
                                    SetColliderSelectionForTerrain(selectedTerrain, false, string.Empty);
                                    heightSettings = GetHeightSettings(selectedTerrain);
                                    SceneView.RepaintAll();
                                    DisposToolWindow.RequestRepaintAll(repaintScene: true);
                                    currentManualCollider = null;
                                    currentColliderObject = null;
                                }
                                else if (selectedColliderObject.scene != currentScene)
                                {
                                    colliderMessages.Add(("Selected GameObject must belong to the active scene.", MessageType.Error));
                                }
                                else
                                {
                                    MeshCollider selectedCollider = selectedColliderObject.GetComponent<MeshCollider>();
                                    if (selectedCollider == null || selectedCollider.sharedMesh == null)
                                    {
                                        colliderMessages.Add(("Selected GameObject must have a MeshCollider with a valid mesh.", MessageType.Error));
                                    }
                                    else
                                    {
                                        string selectedPath = GetTransformPath(selectedCollider.transform);
                                        SetColliderSelectionForTerrain(selectedTerrain, false, selectedPath);
                                        heightSettings = GetHeightSettings(selectedTerrain);
                                        SceneView.RepaintAll();
                                        DisposToolWindow.RequestRepaintAll(repaintScene: true);
                                        currentManualCollider = selectedCollider;
                                        currentColliderObject = selectedCollider.gameObject;
                                    }
                                }
                            }

                            if (!string.IsNullOrEmpty(heightSettings.colliderPath))
                            {
                                if (currentManualCollider == null)
                                {
                                    colliderMessages.Add(($"MeshCollider '{heightSettings.colliderPath}' is not present in the active scene. Auto selection will be used as a fallback while sampling.", MessageType.Warning));
                                }
                                else
                                {
                                    EditorGUILayout.LabelField(new GUIContent("Resolved Collider", "Collider currently used for raycast sampling."), new GUIContent(GetTransformPath(currentManualCollider.transform)));
                                }
                            }
                            else
                            {
                                colliderMessages.Add(("No collider selected. Auto selection will be used as a fallback while sampling.", MessageType.Info));
                            }

                            if (sceneColliders.Count == 0)
                            {
                                colliderMessages.Add(("No MeshCollider found in the active scene. Raycast sampling will fall back to the fixed offset.", MessageType.Warning));
                            }

                            foreach (var message in colliderMessages)
                            {
                                EditorGUILayout.HelpBox(message.message, message.type);
                            }
                        }
                        else
                        {
                            MeshCollider defaultCollider = FindDefaultMeshCollider(currentScene);
                            string info = defaultCollider != null ? GetTransformPath(defaultCollider.transform) : "<none>";
                            EditorGUILayout.LabelField(new GUIContent("Auto Collider", "Collider currently selected for raycast sampling."), new GUIContent(info));
                            if (defaultCollider == null)
                            {
                                EditorGUILayout.HelpBox("No MeshCollider found in the active scene. Raycast sampling will fall back to the fixed offset.", MessageType.Warning);
                            }
                        }

                        EditorGUI.indentLevel--;
                    }
                }
            }
            
            // SETTINGS PAGE
            if (uiTabIndex == 1)
            {
                EditorGUILayout.Space(10);
                EditorGUILayout.LabelField("Settings", EditorStyles.boldLabel);
                EditorGUI.BeginChangeCheck();
                
                EditorGUILayout.LabelField("Display", EditorStyles.miniBoldLabel);
                displayMode = (DisplayMode)EditorGUILayout.EnumPopup("Display Mode", displayMode);
                // Color settings are always shown
                {
                    EditorGUILayout.LabelField("Tile Color Settings", EditorStyles.miniBoldLabel);
                    colorOpacity = EditorGUILayout.Slider("Tile Opacity", colorOpacity, 0.1f, 1f);
                    colorBrightness = EditorGUILayout.Slider("Tile Brightness", colorBrightness, 0.1f, 2f);
                    if (terrainDatabase == null)
                    {
                        EditorGUILayout.HelpBox("Terrain colors not loaded. Click 'Parse Terrain XML' to load colors.", MessageType.Warning);
                        if (GUILayout.Button("Parse Terrain XML"))
                        {
                            TerrainXMLParser.ParseTerrainXML();
                            LoadTerrainDatabase();
                        }
                    }
                }
                showGridLines = EditorGUILayout.Toggle("Show Grid Lines", showGridLines);
                gridColor = EditorGUILayout.ColorField("Grid Color", gridColor);
                gridThickness = EditorGUILayout.Slider("Grid Thickness", gridThickness, 0.5f, 50f);

                if (displayMode != DisplayMode.ColorOnly)
                {
                    EditorGUILayout.Space(5);
                    EditorGUILayout.LabelField("Text", EditorStyles.miniBoldLabel);
                    textDisplayMode = (TextDisplayMode)EditorGUILayout.EnumPopup("Text Display", textDisplayMode);
                    textSize = EditorGUILayout.Slider("Text Size", textSize, 0.1f, 3f);
                }

                if (EditorGUI.EndChangeCheck())
                {
                    SaveSettings();
                    SceneView.RepaintAll();
                }

                EditorGUILayout.Space(10);
                if (GUILayout.Button("Reset Display Settings"))
                {
                    textSize = 1.5f;
                    textColor = Color.white;
                    gridColor = new Color(1f, 1f, 1f, 0.3f);
                    gridThickness = 1f;
                    worldOffset = Vector3.zero;
                    SaveSettings();
                    SceneView.RepaintAll();
                }

                // (removed) Zoom metric debug log
            }
            
            // Brush Painting Section
            if (uiTabIndex == 0 && selectedTerrain != null)
            {
                EditorGUILayout.Space(10);
                EditorGUILayout.LabelField("Terrain Painting", EditorStyles.boldLabel);
                    
                    EditorGUI.BeginChangeCheck();
                    
                    // Disable painting controls if visualization is off
                    EditorGUI.BeginDisabledGroup(!visualizationEnabled);
                    
                    GUI.backgroundColor = paintMode ? Color.green : Color.white;
                    if (GUILayout.Button(paintMode ? "Exit Paint Mode" : "Enter Paint Mode"))
                    {
                        paintMode = !paintMode;
                        if (paintMode)
                        {
                            // Make sure we have a default terrain selected
                            if (IsEmptyTerrain(selectedBrushTerrain) && terrainDatabase != null)
                            {
                                var paintableTerrains = GetPaintableTerrains();
                                if (paintableTerrains.Count > 0)
                                {
                                    selectedBrushTerrain = paintableTerrains[0].tid;
                                }
                            }
                        }
                        SceneView.RepaintAll();
                    }
                    GUI.backgroundColor = Color.white;
                    
                    if (paintMode)
                    {
                        EditorGUILayout.Space(5);
                        // Only odd numbers for brush size (1x1, 3x3, 5x5, 7x7)
                        int brushSteps = (brushSize - 1) / 2;
                        brushSteps = EditorGUILayout.IntSlider("Brush Size", brushSteps, 0, 3);
                        brushSize = brushSteps * 2 + 1;
                        EditorGUILayout.LabelField($"Brush: {brushSize}x{brushSize}", EditorStyles.miniLabel);
                        
                        EditorGUILayout.Space(5);
                        
                    // Status: Hovering over
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Hovering over:", GUILayout.Width(100));
                    string hoveredIdForPanel = null;
                    var hoverGrid = GetVirtualGrid(selectedTerrain);
                    if (isMouseOverGrid && selectedTerrain != null && hoverGrid != null)
                    {
                        hoveredIdForPanel = hoverGrid.GetTerrainId(hoveredTile.x, hoveredTile.y);
                    }
                        if (!IsEmptyTerrain(hoveredIdForPanel))
                        {
                            if (terrainDatabase != null)
                            {
                                Color hColor = GetBaseTerrainColor(hoveredIdForPanel);
                                Rect colorRectH = GUILayoutUtility.GetRect(20, 20, GUILayout.Width(20));
                                EditorGUI.DrawRect(colorRectH, hColor);
                                EditorGUI.DrawRect(colorRectH, new Color(0, 0, 0, 0.2f));
                            }
                            string displayNameH = hoveredIdForPanel;
                            if (terrainDatabase != null)
                            {
                                var tH = terrainDatabase.GetTerrainType(hoveredIdForPanel);
                                if (tH != null && !string.IsNullOrEmpty(tH.name) && tH.name != tH.tid)
                                {
                                    displayNameH = $"{hoveredIdForPanel} ({tH.name})";
                                }
                            }
                            EditorGUILayout.LabelField(displayNameH, EditorStyles.boldLabel);
                        }
                        else
                        {
                            // Draw blank color chip to maintain consistent layout
                            Rect blankRectH = GUILayoutUtility.GetRect(20, 20, GUILayout.Width(20));
                            EditorGUI.DrawRect(blankRectH, new Color(0.3f, 0.3f, 0.3f, 0.2f));
                            EditorGUI.DrawRect(blankRectH, new Color(0, 0, 0, 0.2f));
                            EditorGUILayout.LabelField("None", EditorStyles.boldLabel);
                        }
                    EditorGUILayout.EndHorizontal();

                    EditorGUILayout.Space(2);

                    // Painting with (selected brush) with color chip
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Painting with:", GUILayout.Width(100));
                    
                    if (!IsEmptyTerrain(selectedBrushTerrain))
                    {
                        // Draw color chip
                        if (terrainDatabase != null)
                        {
                            Color terrainColor = GetBaseTerrainColor(selectedBrushTerrain);
                            Rect colorRect = GUILayoutUtility.GetRect(20, 20, GUILayout.Width(20));
                            EditorGUI.DrawRect(colorRect, terrainColor);
                            EditorGUI.DrawRect(colorRect, new Color(0, 0, 0, 0.2f)); // Border
                        }
                        
                        // Show terrain ID and name
                        string displayName = selectedBrushTerrain;
                        if (terrainDatabase != null)
                        {
                            var terrain = terrainDatabase.GetTerrainType(selectedBrushTerrain);
                            if (terrain != null && !string.IsNullOrEmpty(terrain.name) && terrain.name != terrain.tid)
                            {
                                displayName = $"{selectedBrushTerrain} ({terrain.name})";
                            }
                        }
                        EditorGUILayout.LabelField(displayName, EditorStyles.boldLabel);
                    }
                    else
                    {
                        // Draw blank color chip to maintain consistent layout
                        Rect blankRect = GUILayoutUtility.GetRect(20, 20, GUILayout.Width(20));
                        EditorGUI.DrawRect(blankRect, new Color(0.3f, 0.3f, 0.3f, 0.2f));
                        EditorGUI.DrawRect(blankRect, new Color(0, 0, 0, 0.2f));
                        EditorGUILayout.LabelField("None", EditorStyles.boldLabel);
                    }
                    EditorGUILayout.EndHorizontal();
                    
                    if (terrainDatabase != null)
                    {
                        EditorGUILayout.Space(5);
                        EditorGUILayout.LabelField("Terrain Palette", EditorStyles.miniBoldLabel);
                        
                        // Get terrains used in current map
                        HashSet<string> usedTerrains = new HashSet<string>();
                        if (selectedTerrain != null && selectedTerrain.m_Terrains != null)
                        {
                            foreach (string tid in selectedTerrain.m_Terrains)
                            {
                                if (!IsEmptyTerrain(tid))
                                {
                                    usedTerrains.Add(tid);
                                }
                            }
                        }
                        
                        var allTypes = GetPaintableTerrains();

                        // Used Terrains Section
                        if (usedTerrains.Count > 0)
                        {
                            EditorGUILayout.LabelField($"★ Used in Map ({usedTerrains.Count})", EditorStyles.miniBoldLabel);
                            
                            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                            
                            var usedTerrainsList = allTypes.Where(t => usedTerrains.Contains(t.tid)).ToList();
                            usedTerrainsList.Sort((a, b) => string.Compare(a.tid, b.tid));
                            
                            foreach (var terrain in usedTerrainsList)
                            {
                                DrawTerrainButton(terrain, true);
                            }
                            
                            EditorGUILayout.EndVertical();
                            EditorGUILayout.Space(5);
                        }
                        
                        // All Terrains Section
                        EditorGUILayout.LabelField("All Terrains", EditorStyles.miniBoldLabel);
                        terrainSearchFilter = EditorGUILayout.TextField("Search", terrainSearchFilter);
                        
                        paletteScrollPosition = EditorGUILayout.BeginScrollView(paletteScrollPosition, GUILayout.Height(150));
                        
                        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                        
                        foreach (var terrain in allTypes)
                        {
                            if (!string.IsNullOrEmpty(terrainSearchFilter) && 
                                !terrain.tid.ToLower().Contains(terrainSearchFilter.ToLower()) &&
                                !terrain.name.ToLower().Contains(terrainSearchFilter.ToLower()))
                            {
                                continue;
                            }

                            DrawTerrainButton(terrain, usedTerrains.Contains(terrain.tid));
                        }
                        
                        EditorGUILayout.EndVertical();
                        EditorGUILayout.EndScrollView();
                    }
                    
                    EditorGUILayout.HelpBox("Left Click: Paint | Ctrl/Cmd+Click: Sample/Pick", MessageType.Info);
                    }
                    
                    EditorGUI.EndDisabledGroup(); // End disable group for visualization check
                    
                    if (EditorGUI.EndChangeCheck())
                    {
                        SceneView.RepaintAll();
                    }
                }
            
            // ADVANCED PAGE
            if (uiTabIndex == 2)
            {
                EditorGUILayout.Space(10);
                EditorGUILayout.LabelField("Advanced Tools", EditorStyles.boldLabel);
                
                if (selectedTerrain == null)
                {
                    EditorGUILayout.HelpBox("Please select a terrain first in the Main tab.", MessageType.Info);
                }
                else
                {
                    DrawAdvancedTab();
                }
            }
            
            EditorGUILayout.EndScrollView();
        }

        private static float GetCameraDistance(SceneView sceneView, float terrainCenterX, float terrainCenterZ, float terrainY)
        {
            if (sceneView == null || sceneView.camera == null)
                return 50f; // Default medium distance
            
            Vector3 terrainCenter = new Vector3(terrainCenterX, terrainY, terrainCenterZ);
            Vector3 cameraPos = sceneView.camera.transform.position;
            return Vector3.Distance(cameraPos, terrainCenter);
        }

        private static float CalculateWorldThickness(SceneView sceneView, Vector3 worldPosition, float pixelThickness)
        {
            if (sceneView == null || sceneView.camera == null || pixelThickness <= 0f)
            {
                return 0f;
            }

            Camera camera = sceneView.camera;

            if (camera.orthographic)
            {
                float pixelSize = (camera.orthographicSize * 2f) / Mathf.Max(1f, camera.pixelHeight);
                return pixelThickness * pixelSize;
            }

            Vector3 guiPoint = HandleUtility.WorldToGUIPoint(worldPosition);
            Vector3 guiOffset = guiPoint + new Vector3(pixelThickness, 0f, 0f);
            Ray ray = HandleUtility.GUIPointToWorldRay(guiPoint);
            Ray rayOffset = HandleUtility.GUIPointToWorldRay(guiOffset);
            Plane plane = new Plane(Vector3.up, worldPosition);
            if (plane.Raycast(ray, out float enter) && plane.Raycast(rayOffset, out float enterOffset))
            {
                Vector3 p0 = ray.GetPoint(enter);
                Vector3 p1 = rayOffset.GetPoint(enterOffset);
                float projected = (p1 - p0).magnitude;
                if (projected > 1e-6f)
                {
                    return projected;
                }
            }

            float distance = Vector3.Distance(camera.transform.position, worldPosition);
            float fov = camera.fieldOfView * Mathf.Deg2Rad;
            float pixelWorldSize = 2f * distance * Mathf.Tan(fov * 0.5f) / Mathf.Max(1f, camera.pixelHeight);
            return pixelThickness * pixelWorldSize;
        }

        private static void OnSceneGUI(SceneView sceneView)
        {
            if (!visualizationEnabled || selectedTerrain == null)
            {
                PruneMeshCaches(selectedTerrain);
                return;
            }

            PruneMeshCaches(selectedTerrain);

            TerrainVirtualGrid currentGrid = GetVirtualGrid(selectedTerrain);
            if (currentGrid == null)
                return;

            int width = selectedTerrain.m_Width;
            int height = selectedTerrain.m_Height;
            float startX = selectedTerrain.m_X + worldOffset.x;
            float startZ = selectedTerrain.m_Z + worldOffset.z;

            TerrainHeightSettings heightSettings = GetHeightSettings(selectedTerrain);
            TerrainHeightCache heightCache = heightSettings.mode == TerrainHeightMode.RaycastMesh ? GetOrBuildTerrainHeightCache(selectedTerrain, heightSettings) : null;
            float basePlaneY = heightSettings.offset;

            int lastColIndex = Mathf.Max(width - 1, 0);
            int lastRowIndex = Mathf.Max(height - 1, 0);
            int centerCol = width > 0 ? Mathf.Clamp(width / 2, 0, lastColIndex) : 0;
            int centerRow = height > 0 ? Mathf.Clamp(height / 2, 0, lastRowIndex) : 0;
            float centerHeight = ResolveTileHeight(heightCache, heightSettings, centerCol, centerRow);

            float terrainCenterX = startX + (width * TILE_SIZE) / 2f;
            float terrainCenterZ = startZ + (height * TILE_SIZE) / 2f;
            float cameraDistance = GetCameraDistance(sceneView, terrainCenterX, terrainCenterZ, centerHeight);
            Vector3 gridReferencePosition = new Vector3(terrainCenterX, centerHeight + 0.02f, terrainCenterZ);
            currentGridThicknessWorld = showGridLines ? Mathf.Max(0.0005f, CalculateWorldThickness(sceneView, gridReferencePosition, gridThickness)) : 0f;
            
            float currentTime = (float)EditorApplication.timeSinceStartup;
            float deltaTime = Mathf.Min(currentTime - lastFrameTime, 0.1f);
            lastFrameTime = currentTime;
            
            if (sceneView.camera != null)
            {
                Vector3 currentCamPos = sceneView.camera.transform.position;
                Quaternion currentCamRot = sceneView.camera.transform.rotation;
                float currentFOV = sceneView.camera.fieldOfView;
                
                if (Vector3.Distance(currentCamPos, lastCameraPosition) > 0.01f ||
                    Quaternion.Angle(currentCamRot, lastCameraRotation) > 0.1f ||
                    Mathf.Abs(currentFOV - lastCameraFOV) > 0.1f)
                {
                    cameraIsMoving = true;
                    cameraStillTime = 0f;
                }
                else
                {
                    cameraStillTime += deltaTime;
                    if (cameraStillTime > cameraStillThreshold)
                    {
                        cameraIsMoving = false;
                    }
                }
                
                lastCameraPosition = currentCamPos;
                lastCameraRotation = currentCamRot;
                lastCameraFOV = currentFOV;
            }
            
            HandleMouseInput(width, height, startX, startZ, basePlaneY, heightSettings);
            
            bool isRepaint = Event.current.type == EventType.Repaint;

            justStartedMoving = (!wasCameraMoving && cameraIsMoving);
            wasCameraMoving = cameraIsMoving;

            if (isRepaint && terrainDatabase != null)
            {
                Mesh overlayMesh = GetOverlayMesh(selectedTerrain, currentGrid, heightCache, heightSettings);
                if (overlayMesh != null)
                {
                    EnsureOverlayMaterial();
                    overlayMaterial.SetPass(0);
                    Graphics.DrawMeshNow(overlayMesh, Matrix4x4.identity);
                }
            }
            
            if (isRepaint && showGridLines && width > 0 && height > 0)
            {
                Mesh gridMesh = GetGridMesh(selectedTerrain, currentGrid, heightCache, heightSettings);
                if (gridMesh != null && gridMesh.vertexCount > 0)
                {
                    EnsureOverlayMaterial();
                    overlayMaterial.SetPass(0);
                    Graphics.DrawMeshNow(gridMesh, Matrix4x4.identity);
                }
            }
            
            if (isRepaint && terrainDatabase != null)
            {
                List<TerrainIsland> islands = GetOrCreateIslands(selectedTerrain, cameraDistance);
                foreach (var island in islands)
                {
                    if (IsEmptyTerrain(island.terrainId))
                        continue;
                    Color borderColor = GetBorderColor(island.terrainId);
                    DrawIslandBorders(island, width, height, startX, startZ, heightCache, heightSettings, borderColor);
                }
            }
            
            HashSet<Vector2Int> currentHighlightRegion = null;
            string highlightTerrainId = null;
            if (isMouseOverGrid && hoveredTile.x >= 0 && hoveredTile.y >= 0)
            {
                string hoveredTerrainIdForHighlight = currentGrid.GetTerrainId(hoveredTile.x, hoveredTile.y);
                if (!IsEmptyTerrain(hoveredTerrainIdForHighlight))
                {
                    bool isSampling = IsSamplingModifier(Event.current);
                    if (paintMode && !isSampling && !IsEmptyTerrain(selectedBrushTerrain))
                    {
                        if (hoveredTerrainIdForHighlight == selectedBrushTerrain)
                        {
                            currentHighlightRegion = GetHoverConnectedRegion(selectedTerrain, currentGrid, hoveredTile, width, height);
                            highlightTerrainId = selectedBrushTerrain;
                        }
                        else
                        {
                            var adjacent = FindAdjacentIsland(hoveredTile, selectedBrushTerrain, width, height);
                            var tilesToHighlight = new HashSet<Vector2Int>(adjacent);
                            int brushHalf = (brushSize - 1) / 2;
                            for (int dx = -brushHalf; dx <= brushHalf; dx++)
                            {
                                for (int dz = -brushHalf; dz <= brushHalf; dz++)
                                {
                                    int x = hoveredTile.x + dx;
                                    int z = hoveredTile.y + dz;
                                    if (x >= 0 && x < width && z >= 0 && z < height)
                                        tilesToHighlight.Remove(new Vector2Int(x, z));
                                }
                            }
                            currentHighlightRegion = tilesToHighlight;
                            highlightTerrainId = selectedBrushTerrain;
                        }
                    }
                    else if (paintMode && isSampling && !IsEmptyTerrain(hoveredTerrainIdForHighlight))
                    {
                        currentHighlightRegion = new HashSet<Vector2Int> { hoveredTile };
                        highlightTerrainId = hoveredTerrainIdForHighlight;
                    }
                    else if (!paintMode && !IsEmptyTerrain(hoveredTerrainIdForHighlight))
                    {
                        currentHighlightRegion = GetHoverConnectedRegion(selectedTerrain, currentGrid, hoveredTile, width, height);
                        highlightTerrainId = hoveredTerrainIdForHighlight;
                    }
                }
            }
            
            if (isRepaint && currentHighlightRegion != null && currentHighlightRegion.Count > 0)
            {
                DrawRegionHighlight(currentHighlightRegion, startX, startZ, heightCache, heightSettings, highlightTerrainId);
            }

            bool allowAnyLabels = displayMode != DisplayMode.ColorOnly;
            bool showHoverLabel = false;
            string hoveredTerrainId = "";
            if (isMouseOverGrid && hoveredTile.x >= 0 && hoveredTile.y >= 0)
            {
                hoveredTerrainId = currentGrid.GetTerrainId(hoveredTile.x, hoveredTile.y);
                if (!IsEmptyTerrain(hoveredTerrainId))
                {
                    showHoverLabel = true;
                }
            }

            if (isRepaint)
            {
                if (s_LabelStyle == null) s_LabelStyle = new GUIStyle();
                if (s_LabelStyleSmall == null) s_LabelStyleSmall = new GUIStyle();
                if (s_LabelStyleHover == null) s_LabelStyleHover = new GUIStyle();
                int baseFont = Mathf.RoundToInt(12 * textSize);
                int smallFont = Mathf.Max(8, Mathf.RoundToInt(baseFont * 0.85f));
                int hoverFont = Mathf.RoundToInt(14 * textSize);
                s_LabelStyle.alignment = TextAnchor.MiddleCenter;
                s_LabelStyle.fontStyle = FontStyle.Bold;
                s_LabelStyle.fontSize = baseFont;
                s_LabelStyleSmall.alignment = TextAnchor.MiddleCenter;
                s_LabelStyleSmall.fontStyle = FontStyle.Bold;
                s_LabelStyleSmall.fontSize = smallFont;
                s_LabelStyleHover.alignment = TextAnchor.MiddleLeft;
                s_LabelStyleHover.fontStyle = FontStyle.Bold;
                s_LabelStyleHover.fontSize = hoverFont;

                s_LabelTextCache.Clear();
                s_LabelFrameKeys.Clear();
                s_FrameLabelNodes.Clear();
                s_LabelRemovalBuffer.Clear();

                if (allowAnyLabels)
                {
                    List<TerrainIsland> islands = GetOrCreateIslands(selectedTerrain, cameraDistance);
                    Handles.BeginGUI();

                    foreach (var island in islands)
                    {
                        if (IsEmptyTerrain(island.terrainId))
                            continue;
                        if (showHoverLabel && island.ContainsTile(hoveredTile))
                            continue;

                        foreach (var labelPos in island.labelPositions)
                        {
                            float centerX = startX + labelPos.x * TILE_SIZE + TILE_SIZE * 0.5f;
                            float centerZ = startZ + labelPos.y * TILE_SIZE + TILE_SIZE * 0.5f;
                            int labelCol = Mathf.Clamp(Mathf.RoundToInt(labelPos.x), 0, lastColIndex);
                            int labelRow = Mathf.Clamp(Mathf.RoundToInt(labelPos.y), 0, lastRowIndex);
                            float labelHeight = ResolveTileHeight(heightCache, heightSettings, labelCol, labelRow) + 0.02f;
                            Vector3 worldPos = new Vector3(centerX, labelHeight, centerZ);

                            string textKey = island.terrainId + "|" + textDisplayMode;
                            if (!s_LabelTextCache.TryGetValue(textKey, out string displayText))
                            {
                                displayText = GetTerrainDisplayText(island.terrainId);
                                s_LabelTextCache[textKey] = displayText;
                            }

                            Color labelColor = ResolveLabelColor(island.terrainId);
                            if (displayMode == DisplayMode.ColorOnly) continue;
                            GUIStyle styleRef = s_LabelStyle;
                            styleRef.normal.textColor = labelColor;

                            Vector2 anchorGui = HandleUtility.WorldToGUIPoint(worldPos);
                            s_LabelContent.text = displayText;
                            Vector2 size = styleRef.CalcSize(s_LabelContent);
                            float totalWidth = size.x + LABEL_ICON_SIZE + LABEL_ICON_PADDING * 2f;
                            float totalHeight = size.y;

                            string nodeKey = island.terrainId + "|" + Mathf.RoundToInt(labelPos.x) + "x" + Mathf.RoundToInt(labelPos.y) + "|" + (int)textDisplayMode;
                            s_LabelFrameKeys.Add(nodeKey);
                            bool nodeExisted = s_LabelNodes.TryGetValue(nodeKey, out var node);
                            if (!nodeExisted)
                            {
                                node = new LabelNode { key = nodeKey, posGui = anchorGui, preservedOffset = Vector2.zero };
                                s_LabelNodes[nodeKey] = node;
                            }
                            Vector2 prevOffset = nodeExisted ? (node.posGui - node.anchorGui) : Vector2.zero;
                            node.anchorGui = anchorGui;
                            node.width = totalWidth;
                            node.height = totalHeight;
                            node.seenThisFrame = true;

                            node.posGui = anchorGui;
                            if (relaxEnabled)
                            {
                                node.priority = island.tiles.Count >= relaxLargeIslandTiles ? relaxPriorityLarge : 1f;
                                node.preservedOffset = prevOffset;
                                s_FrameLabelNodes.Add(node);
                            }
                        }
                    }

                    if (relaxEnabled)
                    {
                        RelaxLabelPositions(s_FrameLabelNodes, deltaTime, justStartedMoving, cameraIsMoving, cameraDistance, width, height);
                    }

                    foreach (var island in islands)
                    {
                        if (IsEmptyTerrain(island.terrainId)) continue;
                        if (showHoverLabel && island.ContainsTile(hoveredTile)) continue;

                        foreach (var labelPos in island.labelPositions)
                        {
                            float centerX = startX + labelPos.x * TILE_SIZE + TILE_SIZE * 0.5f;
                            float centerZ = startZ + labelPos.y * TILE_SIZE + TILE_SIZE * 0.5f;
                            int labelCol = Mathf.Clamp(Mathf.RoundToInt(labelPos.x), 0, lastColIndex);
                            int labelRow = Mathf.Clamp(Mathf.RoundToInt(labelPos.y), 0, lastRowIndex);
                            float labelHeight = ResolveTileHeight(heightCache, heightSettings, labelCol, labelRow) + 0.02f;
                            Vector3 worldPos = new Vector3(centerX, labelHeight, centerZ);

                            string textKey = island.terrainId + "|" + textDisplayMode;
                            if (!s_LabelTextCache.TryGetValue(textKey, out string displayText))
                            {
                                displayText = GetTerrainDisplayText(island.terrainId);
                                s_LabelTextCache[textKey] = displayText;
                            }
                            Color labelColor = ResolveLabelColor(island.terrainId);
                            if (displayMode == DisplayMode.ColorOnly) continue;
                            GUIStyle styleRef = s_LabelStyle;
                            styleRef.normal.textColor = labelColor;

                            string nodeKey = island.terrainId + "|" + Mathf.RoundToInt(labelPos.x) + "x" + Mathf.RoundToInt(labelPos.y) + "|" + (int)textDisplayMode;
                            if (s_LabelNodes.TryGetValue(nodeKey, out var node))
                            {
                                DrawLabelWithColoredIconAtGui(worldPos, node.posGui, displayText, island.terrainId, styleRef, labelColor, 1f);
                            }
                            else
                            {
                                Vector2 anchorGui = HandleUtility.WorldToGUIPoint(worldPos);
                                DrawLabelWithColoredIconAtGui(worldPos, anchorGui, displayText, island.terrainId, styleRef, labelColor, 1f);
                            }
                        }
                    }

                    foreach (var kv in s_LabelNodes)
                    {
                        if (!s_LabelFrameKeys.Contains(kv.Key))
                        {
                            s_LabelRemovalBuffer.Add(kv.Key);
                        }
                        else
                        {
                            kv.Value.seenThisFrame = false;
                        }
                    }

                    for (int i = 0; i < s_LabelRemovalBuffer.Count; i++)
                    {
                        s_LabelNodes.Remove(s_LabelRemovalBuffer[i]);
                    }
                    s_LabelFrameKeys.Clear();
                    s_FrameLabelNodes.Clear();
                    s_LabelRemovalBuffer.Clear();

                    Handles.EndGUI();
                }
            }

            if (isRepaint && showHoverLabel && !paintMode)
            {
                float hoverX = startX + hoveredTile.x * TILE_SIZE + TILE_SIZE * 0.5f;
                float hoverZ = startZ + hoveredTile.y * TILE_SIZE + TILE_SIZE * 0.5f;
                int hoverCol = Mathf.Clamp(hoveredTile.x, 0, lastColIndex);
                int hoverRow = Mathf.Clamp(hoveredTile.y, 0, lastRowIndex);
                float hoverHeight = ResolveTileHeight(heightCache, heightSettings, hoverCol, hoverRow) + 0.05f;
                Vector3 hoverPos = new Vector3(hoverX, hoverHeight, hoverZ);
                string hoverDisplayText = GetTerrainDisplayText(hoveredTerrainId);
                Color hoverLabelColor = ResolveLabelColor(hoveredTerrainId, false);
                if (s_LabelStyleHover == null) s_LabelStyleHover = new GUIStyle();
                GUIStyle hoverStyle = s_LabelStyleHover;
                hoverStyle.alignment = TextAnchor.MiddleLeft;
                hoverStyle.fontStyle = FontStyle.Bold;
                hoverStyle.fontSize = Mathf.RoundToInt(14 * textSize);
                hoverStyle.normal.textColor = hoverLabelColor;
                Handles.BeginGUI();
                DrawLabelWithColoredIcon(hoverPos, hoverDisplayText, hoveredTerrainId, hoverStyle, hoverLabelColor);
                Handles.EndGUI();
            }

            if (paintMode && isMouseOverGrid)
            {
                DrawBrushPreview(hoveredTile, width, height, startX, startZ, heightCache, heightSettings);
            }

            if (uiTabIndex == 2 && selectedTerrain != null)
            {
                if (newTerrainWidth != width || newTerrainHeight != height)
                {
                    DrawResizePreview(width, height, startX, startZ, heightCache, heightSettings);
                }

                if (previewTerrains != null && (mirrorPreviewMode != MirrorMode.None || shiftPreviewMode != ShiftDirection.None))
                {
                    DrawAdvancedOperationPreview(width, height, startX, startZ, heightCache, heightSettings);
                }
            }

            if (cameraIsMoving || cameraStillTime < 1f)
            {
                sceneView.Repaint();
            }
        }

        private static bool isPaintingStroke = false;
        private static int paintUndoGroup = -1;
        private static HashSet<int> paintedIndicesThisDrag = new HashSet<int>();

        private static HashSet<Vector2Int> GetHoverConnectedRegion(
            TerrainAssetAdapter terrain,
            TerrainVirtualGrid grid,
            Vector2Int tile,
            int width,
            int height)
        {
            if (terrain == null || grid == null)
            {
                return new HashSet<Vector2Int>();
            }

            return TerrainRegionCache.GetSameTerrainRegion(terrain, grid, tile, width, height, useCache: true);
        }

        private static void RelaxLabelPositions(
            List<LabelNode> nodes,
            float deltaTime,
            bool justStartedMoving,
            bool cameraIsMoving,
            float cameraDistance,
            int terrainWidth,
            int terrainHeight)
        {
            if (nodes == null || nodes.Count == 0)
            {
                return;
            }

            foreach (var node in nodes)
            {
                if (relaxFreezeWhileMoving && cameraIsMoving && !justStartedMoving)
                {
                    node.posGui = node.anchorGui + node.preservedOffset;
                }
                else
                {
                    node.posGui = Vector2.Lerp(node.posGui, node.anchorGui, relaxAnchorK);
                }
            }
        }

        private static void BeginPaintStroke()
        {
            if (isPaintingStroke || selectedTerrain == null) return;
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Paint Terrain");
            paintUndoGroup = Undo.GetCurrentGroup();
            if (selectedTerrain.Asset != null)
            {
                Undo.RegisterCompleteObjectUndo(selectedTerrain.Asset, "Paint Terrain");
            }
            paintedIndicesThisDrag.Clear();
            isPaintingStroke = true;
        }

        private static void EndPaintStroke()
        {
            if (!isPaintingStroke) return;
            if (paintUndoGroup >= 0)
            {
                Undo.CollapseUndoOperations(paintUndoGroup);
                paintUndoGroup = -1;
            }
            isPaintingStroke = false;
            paintedIndicesThisDrag.Clear();
        }

        private static void HandleMouseInput(int width, int height, float startX, float startZ, float basePlaneY, TerrainHeightSettings heightSettings)
        {
            Event currentEvent = Event.current;
            if (externalInteractionLock)
            {
                isMouseOverGrid = false;
                hoveredTile = new Vector2Int(-1, -1);
                if (isPaintingStroke)
                {
                    EndPaintStroke();
                }
                return;
            }
            Vector2Int prevHovered = hoveredTile;
            bool prevOver = isMouseOverGrid;

            Ray ray = HandleUtility.GUIPointToWorldRay(currentEvent.mousePosition);

            Vector3? intersection = null;
            bool useMeshRaycast = heightSettings != null && heightSettings.mode == TerrainHeightMode.RaycastMesh;
            if (useMeshRaycast)
            {
                string hoverColliderPath;
                MeshCollider collider = GetSceneMeshCollider(SceneManager.GetActiveScene(), heightSettings, selectedTerrain, false, out hoverColliderPath);
                if (collider != null && collider.Raycast(ray, out RaycastHit meshHit, 10000f))
                {
                    intersection = meshHit.point;
                }
            }

            if (!intersection.HasValue)
            {
                float denom = ray.direction.y;
                if (Mathf.Approximately(denom, 0f))
                {
                    isMouseOverGrid = false;
                    return;
                }

                float distance = (basePlaneY - ray.origin.y) / denom;
                if (distance < 0f)
                {
                    isMouseOverGrid = false;
                    return;
                }

                intersection = ray.origin + ray.direction * distance;
            }

            Vector3 hitPoint = intersection.Value;
            int gridX = Mathf.FloorToInt((hitPoint.x - startX) / TILE_SIZE);
            int gridZ = Mathf.FloorToInt((hitPoint.z - startZ) / TILE_SIZE);

            if (gridX < 0 || gridX >= width || gridZ < 0 || gridZ >= height)
            {
                isMouseOverGrid = false;
                if (isPaintingStroke)
                {
                    EndPaintStroke();
                }
                return;
            }

            hoveredTile = new Vector2Int(gridX, gridZ);
            isMouseOverGrid = true;

            if (paintMode && !externalInteractionLock)
            {
                if (currentEvent.type == EventType.MouseDown || currentEvent.type == EventType.MouseDrag || currentEvent.type == EventType.MouseUp)
                {
                    if (currentEvent.button == 0)
                    {
                        if (IsSamplingModifier(currentEvent) && currentEvent.type == EventType.MouseDown)
                        {
                            PickTerrain(hoveredTile, width);
                        }
                        else
                        {
                            if (currentEvent.type == EventType.MouseDown)
                            {
                                BeginPaintStroke();
                                PaintTerrainDedup(hoveredTile, width, height);
                            }
                            else if (currentEvent.type == EventType.MouseDrag && isPaintingStroke)
                            {
                                PaintTerrainDedup(hoveredTile, width, height);
                            }
                            else if (currentEvent.type == EventType.MouseUp && isPaintingStroke)
                            {
                                EndPaintStroke();
                            }
                        }
                        currentEvent.Use();
                    }
                }

                if (currentEvent.type == EventType.Layout && currentEvent.button == 0)
                {
                    HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
                }
            }

            if (isMouseOverGrid != prevOver || hoveredTile != prevHovered)
            {
                SceneView.RepaintAll();
                if (instance != null)
                {
                    instance.Repaint();
                }
            }
        }

        private static void PaintTerrainDedup(Vector2Int centerTile, int width, int height)
        {
            if (IsEmptyTerrain(selectedBrushTerrain) || selectedTerrain == null) return;
            TerrainVirtualGrid grid = GetVirtualGrid(selectedTerrain);
            if (grid == null) return;
            var tiles = selectedTerrain.m_Terrains;
            if (tiles == null) return;

            int halfSize = (brushSize - 1) / 2;
            bool modified = false;

            for (int dx = -halfSize; dx <= halfSize; dx++)
            {
                for (int dz = -halfSize; dz <= halfSize; dz++)
                {
                    int tileX = centerTile.x + dx;
                    int tileZ = centerTile.y + dz;
                    if (tileX >= 0 && tileX < width && tileZ >= 0 && tileZ < height)
                    {
                        int actualIndex = grid.GetActualIndex(tileX, tileZ);
                        if (actualIndex < 0 || actualIndex >= tiles.Length)
                        {
                            continue;
                        }

                        if (!paintedIndicesThisDrag.Add(actualIndex))
                        {
                            continue;
                        }

                        if (IsEmptyTerrain(tiles[actualIndex]))
                        {
                            continue;
                        }

                        tiles[actualIndex] = selectedBrushTerrain;
                        modified = true;
                    }
                }
            }
            if (modified)
            {
                selectedTerrain.m_Terrains = tiles;
                MarkTerrainDirty(selectedTerrain);
                SceneView.RepaintAll();
            }
        }

        private static HashSet<Vector2Int> FindAdjacentIsland(Vector2Int centerTile, string targetTerrain, int width, int height)
        {
            HashSet<Vector2Int> island = new HashSet<Vector2Int>();
            if (selectedTerrain == null || selectedTerrain.m_Terrains == null)
                return island;

            TerrainVirtualGrid grid = GetVirtualGrid(selectedTerrain);
            if (grid == null)
                return island;

            if (IsEmptyTerrain(targetTerrain))
                return island;

            int halfSize = (brushSize - 1) / 2;
            HashSet<Vector2Int> brushArea = new HashSet<Vector2Int>();

            for (int dx = -halfSize; dx <= halfSize; dx++)
            {
                for (int dz = -halfSize; dz <= halfSize; dz++)
                {
                    int x = centerTile.x + dx;
                    int z = centerTile.y + dz;
                    if (x >= 0 && x < width && z >= 0 && z < height)
                    {
                        brushArea.Add(new Vector2Int(x, z));
                    }
                }
            }

            HashSet<Vector2Int> processedStarts = new HashSet<Vector2Int>();
            foreach (var brushTile in brushArea)
            {
                foreach (var dir in Directions4)
                {
                    Vector2Int neighbor = brushTile + dir;
                    if (brushArea.Contains(neighbor) ||
                        neighbor.x < 0 || neighbor.x >= width ||
                        neighbor.y < 0 || neighbor.y >= height)
                    {
                        continue;
                    }

                    if (!processedStarts.Add(neighbor))
                    {
                        continue;
                    }

                    string tid = grid.GetTerrainId(neighbor.x, neighbor.y);
                    if (IsEmptyTerrain(tid) || tid != targetTerrain)
                    {
                        continue;
                    }

                    var neighborRegion = TerrainRegionCache.GetSameTerrainRegion(selectedTerrain, grid, neighbor, width, height, useCache: true);
                    foreach (var pos in neighborRegion)
                    {
                        if (!brushArea.Contains(pos))
                        {
                            island.Add(pos);
                        }
                    }
                }
            }

            return island;
        }
        
        private static void DrawBrushPreview(Vector2Int centerTile, int width, int height, float startX, float startZ, TerrainHeightCache heightCache, TerrainHeightSettings heightSettings)
        {
            Event currentEvent = Event.current;
            bool isSampling = IsSamplingModifier(currentEvent);
            
            if (isSampling)
            {
                // Draw sampling indicator - single tile showing the color that would be sampled
                float worldX = startX + centerTile.x * TILE_SIZE;
                float worldZ = startZ + centerTile.y * TILE_SIZE;

                float tileHeight = ResolveTileHeight(heightCache, heightSettings, centerTile.x, centerTile.y);
                FillTileQuad(s_TileVerticesOverlay, startX, startZ, centerTile.x, centerTile.y, heightCache, heightSettings, tileHeight, 0.05f);

                // Get the actual terrain color at the hovered tile
                // This should match EXACTLY how the tiles are displayed on the map (with brightness adjustment)
                Color sampleColor = Color.gray;
                Color sampleOutline = Color.gray;
                if (selectedTerrain != null && terrainDatabase != null)
                {
                    TerrainVirtualGrid grid = GetVirtualGrid(selectedTerrain);
                    if (grid != null)
                    {
                        int actualIndex = grid.GetActualIndex(centerTile.x, centerTile.y);
                        if (actualIndex >= 0 && actualIndex < selectedTerrain.m_Terrains.Length)
                        {
                            string terrainToSample = selectedTerrain.m_Terrains[actualIndex];
                            if (!IsEmptyTerrain(terrainToSample))
                            {
                                // Get base color from database
                                Color terrainColor = GetBaseTerrainColor(terrainToSample);

                                // Apply brightness adjustment (same as actual tile rendering)
                                terrainColor.r = Mathf.Clamp01(terrainColor.r * colorBrightness);
                                terrainColor.g = Mathf.Clamp01(terrainColor.g * colorBrightness);
                                terrainColor.b = Mathf.Clamp01(terrainColor.b * colorBrightness);

                                // Now create preview colors
                                sampleColor = new Color(terrainColor.r, terrainColor.g, terrainColor.b, 0.4f);
                                // Darken the adjusted color for the outline
                                sampleOutline = new Color(
                                    terrainColor.r * 0.6f,
                                    terrainColor.g * 0.6f,
                                    terrainColor.b * 0.6f,
                                    0.8f
                                );
                            }
                        }
                    }
                }
                Handles.DrawSolidRectangleWithOutline(s_TileVerticesOverlay, sampleColor, sampleOutline);
                
                // Draw "Sample" text over the tile
                Vector3 tileCenter = new Vector3(worldX + TILE_SIZE * 0.5f, tileHeight + 0.1f, worldZ + TILE_SIZE * 0.5f);
                Vector2 guiPos = HandleUtility.WorldToGUIPoint(tileCenter);
                
                Handles.BeginGUI();
                GUIStyle sampleStyle = new GUIStyle(EditorStyles.boldLabel);
                sampleStyle.alignment = TextAnchor.MiddleCenter;
                sampleStyle.normal.textColor = Color.white;
                sampleStyle.fontSize = 12;
                
                // Draw text with black outline for visibility
                Rect textRect = new Rect(guiPos.x - 30, guiPos.y - 10, 60, 20);
                
                // Draw outline
                sampleStyle.normal.textColor = Color.black;
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        if (dx != 0 || dy != 0)
                        {
                            GUI.Label(new Rect(textRect.x + dx, textRect.y + dy, textRect.width, textRect.height), "Sample", sampleStyle);
                        }
                    }
                }
                
                // Draw main text
                sampleStyle.normal.textColor = Color.white;
                GUI.Label(textRect, "Sample", sampleStyle);
                Handles.EndGUI();
            }
            else
            {
                // Paint preview with actual terrain color
                if (IsEmptyTerrain(selectedBrushTerrain) || terrainDatabase == null)
                {
                    // Fallback to yellow if no terrain selected
                    Color previewColor = new Color(1f, 1f, 0f, 0.3f);
                    DrawBrushTiles(centerTile, width, height, startX, startZ, heightCache, heightSettings, previewColor, Color.yellow);
                }
                else
                {
                    // Get the actual color of the terrain we're painting
                    Color adjustedColor = GetTileFillColor(selectedBrushTerrain);

                    // Make it semi-transparent for preview
                    Color previewColor = new Color(adjustedColor.r, adjustedColor.g, adjustedColor.b, 0.4f);

                    // Darken the adjusted color for the outline
                    Color outlineColor = new Color(
                        adjustedColor.r * 0.6f,
                        adjustedColor.g * 0.6f,
                        adjustedColor.b * 0.6f,
                        0.8f
                    );

                    // Draw the preview tiles
                    DrawBrushTiles(centerTile, width, height, startX, startZ, heightCache, heightSettings, previewColor, outlineColor);

                    // Draw preview borders for the new terrain
                    DrawPreviewBorders(centerTile, width, height, startX, startZ, heightCache, heightSettings, outlineColor);
                }
            }
        }
        
        private static void DrawBrushTiles(Vector2Int centerTile, int width, int height, float startX, float startZ, TerrainHeightCache heightCache, TerrainHeightSettings heightSettings, Color fillColor, Color outlineColor)
        {
            int halfSize = (brushSize - 1) / 2;
            TerrainVirtualGrid grid = GetVirtualGrid(selectedTerrain);
            
            for (int dx = -halfSize; dx <= halfSize; dx++)
            {
                for (int dz = -halfSize; dz <= halfSize; dz++)
                {
                    int tileX = centerTile.x + dx;
                    int tileZ = centerTile.y + dz;
                    
                    if (tileX >= 0 && tileX < width && tileZ >= 0 && tileZ < height)
                    {
                        if (grid != null)
                        {
                            int actualIndex = grid.GetActualIndex(tileX, tileZ);
                            if (actualIndex < 0 || actualIndex >= (selectedTerrain.m_Terrains?.Length ?? 0))
                            {
                                continue;
                            }

                            string tid = selectedTerrain.m_Terrains[actualIndex];
                            if (IsEmptyTerrain(tid))
                            {
                                continue;
                            }
                        }

                        float tileHeight = ResolveTileHeight(heightCache, heightSettings, tileX, tileZ);
                        FillTileQuad(s_TileVerticesOverlay, startX, startZ, tileX, tileZ, heightCache, heightSettings, tileHeight, 0.05f);

                        Handles.DrawSolidRectangleWithOutline(s_TileVerticesOverlay, fillColor, outlineColor);
                    }
                }
            }
        }
        
        private static void DrawPreviewBorders(Vector2Int centerTile, int width, int height, float startX, float startZ, TerrainHeightCache heightCache, TerrainHeightSettings heightSettings, Color borderColor)
        {
            // Collect all tiles that would be painted
            HashSet<Vector2Int> paintedTiles = new HashSet<Vector2Int>();
            int halfSize = (brushSize - 1) / 2;
            TerrainVirtualGrid grid = GetVirtualGrid(selectedTerrain);
            
            for (int dx = -halfSize; dx <= halfSize; dx++)
            {
                for (int dz = -halfSize; dz <= halfSize; dz++)
                {
                    int tileX = centerTile.x + dx;
                    int tileZ = centerTile.y + dz;

                    if (tileX >= 0 && tileX < width && tileZ >= 0 && tileZ < height)
                    {
                        if (grid != null)
                        {
                            int actualIndex = grid.GetActualIndex(tileX, tileZ);
                            if (actualIndex < 0 || actualIndex >= (selectedTerrain.m_Terrains?.Length ?? 0))
                            {
                                continue;
                            }

                            string tid = selectedTerrain.m_Terrains[actualIndex];
                            if (IsEmptyTerrain(tid))
                            {
                                continue;
                            }
                        }

                        paintedTiles.Add(new Vector2Int(tileX, tileZ));
                    }
                }
            }
            
            // Draw borders around the painted area
            Handles.color = borderColor;
            float borderThickness = 3f;
            
            foreach (var tile in paintedTiles)
            {
                float tileX = startX + tile.x * TILE_SIZE;
                float tileZ = startZ + tile.y * TILE_SIZE;

                // Check each edge to see if it's a border
                // Top edge
                if (!paintedTiles.Contains(new Vector2Int(tile.x, tile.y + 1)))
                {
                    float hStart = ResolveCornerHeight(heightCache, heightSettings, tile.x, tile.y + 1) + 0.06f;
                    float hEnd = ResolveCornerHeight(heightCache, heightSettings, tile.x + 1, tile.y + 1) + 0.06f;
                    Vector3 lineStart = new Vector3(tileX, hStart, tileZ + TILE_SIZE);
                    Vector3 lineEnd = new Vector3(tileX + TILE_SIZE, hEnd, tileZ + TILE_SIZE);
                    Handles.DrawLine(lineStart, lineEnd, borderThickness);
                }

                // Right edge
                if (!paintedTiles.Contains(new Vector2Int(tile.x + 1, tile.y)))
                {
                    float hStart = ResolveCornerHeight(heightCache, heightSettings, tile.x + 1, tile.y) + 0.06f;
                    float hEnd = ResolveCornerHeight(heightCache, heightSettings, tile.x + 1, tile.y + 1) + 0.06f;
                    Vector3 lineStart = new Vector3(tileX + TILE_SIZE, hStart, tileZ);
                    Vector3 lineEnd = new Vector3(tileX + TILE_SIZE, hEnd, tileZ + TILE_SIZE);
                    Handles.DrawLine(lineStart, lineEnd, borderThickness);
                }

                // Bottom edge
                if (!paintedTiles.Contains(new Vector2Int(tile.x, tile.y - 1)))
                {
                    float hStart = ResolveCornerHeight(heightCache, heightSettings, tile.x, tile.y) + 0.06f;
                    float hEnd = ResolveCornerHeight(heightCache, heightSettings, tile.x + 1, tile.y) + 0.06f;
                    Vector3 lineStart = new Vector3(tileX, hStart, tileZ);
                    Vector3 lineEnd = new Vector3(tileX + TILE_SIZE, hEnd, tileZ);
                    Handles.DrawLine(lineStart, lineEnd, borderThickness);
                }

                // Left edge
                if (!paintedTiles.Contains(new Vector2Int(tile.x - 1, tile.y)))
                {
                    float hStart = ResolveCornerHeight(heightCache, heightSettings, tile.x, tile.y) + 0.06f;
                    float hEnd = ResolveCornerHeight(heightCache, heightSettings, tile.x, tile.y + 1) + 0.06f;
                    Vector3 lineStart = new Vector3(tileX, hStart, tileZ);
                    Vector3 lineEnd = new Vector3(tileX, hEnd, tileZ + TILE_SIZE);
                    Handles.DrawLine(lineStart, lineEnd, borderThickness);
                }
            }
        }
        
        private static void PickTerrain(Vector2Int tile, int width)
        {
            if (selectedTerrain == null)
                return;
            
            TerrainVirtualGrid grid = GetVirtualGrid(selectedTerrain);
            if (grid == null)
                return;

            int actualIndex = grid.GetActualIndex(tile.x, tile.y);
            if (actualIndex >= 0 && actualIndex < selectedTerrain.m_Terrains.Length)
            {
                string sampledTid = selectedTerrain.m_Terrains[actualIndex];
                if (!IsEmptyTerrain(sampledTid))
                {
                    selectedBrushTerrain = sampledTid;

                    // Force UI refresh to show the newly selected terrain
                    if (instance != null)
                    {
                        instance.Repaint();
                    }
                }
            }
        }
        
        
        private static HashSet<Vector2Int> FindConnectedRegion(TerrainAssetAdapter terrain, Vector2Int startTile, int width, int height)
        {
            TerrainVirtualGrid grid = GetVirtualGrid(terrain);
            if (grid == null)
            {
                return new HashSet<Vector2Int>();
            }

            return TerrainRegionCache.GetSameTerrainRegion(terrain, grid, startTile, width, height, useCache: false);
        }
        
        private static void DrawRegionHighlight(HashSet<Vector2Int> region, float startX, float startZ, TerrainHeightCache heightCache, TerrainHeightSettings heightSettings, string terrainId)
        {
            if (region.Count == 0) return;
            
            // Only draw borders - no tile overlay to reduce visual noise
            // Build set of border edges
            HashSet<(Vector2Int, Vector2Int)> edges = new HashSet<(Vector2Int, Vector2Int)>();
            
            foreach (var tile in region)
            {
                // Check each edge
                Vector2Int[] neighbors = new Vector2Int[]
                {
                    tile + new Vector2Int(0, 1),   // top
                    tile + new Vector2Int(1, 0),   // right
                    tile + new Vector2Int(0, -1),  // bottom
                    tile + new Vector2Int(-1, 0)   // left
                };
                
                // Top edge
                if (!region.Contains(neighbors[0]))
                {
                    var edge = (new Vector2Int(tile.x, tile.y + 1), new Vector2Int(tile.x + 1, tile.y + 1));
                    edges.Add(edge);
                }
                // Right edge
                if (!region.Contains(neighbors[1]))
                {
                    var edge = (new Vector2Int(tile.x + 1, tile.y), new Vector2Int(tile.x + 1, tile.y + 1));
                    edges.Add(edge);
                }
                // Bottom edge
                if (!region.Contains(neighbors[2]))
                {
                    var edge = (new Vector2Int(tile.x, tile.y), new Vector2Int(tile.x + 1, tile.y));
                    edges.Add(edge);
                }
                // Left edge
                if (!region.Contains(neighbors[3]))
                {
                    var edge = (new Vector2Int(tile.x, tile.y), new Vector2Int(tile.x, tile.y + 1));
                    edges.Add(edge);
                }
            }
            
            // Calculate colors based on terrain brightness for contrast
            Color baseColor = Color.gray;
            bool isDarkTerrain = false;
            if (!string.IsNullOrEmpty(terrainId) && terrainDatabase != null)
            {
                baseColor = GetBaseTerrainColor(terrainId);
                // Calculate perceived brightness
                float brightness = baseColor.r * 0.299f + baseColor.g * 0.587f + baseColor.b * 0.114f;
                isDarkTerrain = brightness < 0.4f;
            }
            
            // Draw multi-pass border with soft blur effect
            // Contrasting base color (black for light terrains, white for dark terrains)
            Color outlineColor = isDarkTerrain ? 
                new Color(1f, 1f, 1f, 1f) :  // White for dark terrains
                new Color(0f, 0f, 0f, 1f);    // Black for light terrains
            
            // Calculate the main border color
            Color borderColor;
            if (!string.IsNullOrEmpty(terrainId) && terrainDatabase != null)
            {
                // Use a brightened version of the terrain color
                float boost = isDarkTerrain ? 1.5f : 1.2f;
                borderColor = new Color(
                    Mathf.Min(1f, baseColor.r * boost), 
                    Mathf.Min(1f, baseColor.g * boost), 
                    Mathf.Min(1f, baseColor.b * boost), 
                    1f
                );
            }
            else
            {
                borderColor = new Color(1f, 1f, 1f, 1f);
            }
            
            // Draw multiple passes to create soft blur effect
            // Outer glow (widest, most transparent)
            Color glowColor = Color.Lerp(outlineColor, borderColor, 0.3f);
            glowColor.a = 0.2f;
            Handles.color = glowColor;
            foreach (var edge in edges)
            {
                float hStart = ResolveCornerHeight(heightCache, heightSettings, edge.Item1.x, edge.Item1.y) + 0.028f;
                float hEnd = ResolveCornerHeight(heightCache, heightSettings, edge.Item2.x, edge.Item2.y) + 0.028f;
                Vector3 start = new Vector3(startX + edge.Item1.x * TILE_SIZE, hStart, startZ + edge.Item1.y * TILE_SIZE);
                Vector3 end = new Vector3(startX + edge.Item2.x * TILE_SIZE, hEnd, startZ + edge.Item2.y * TILE_SIZE);
                Handles.DrawLine(start, end, 4f);
            }

            // Middle layer (medium width, medium opacity)
            Color midColor = Color.Lerp(outlineColor, borderColor, 0.5f);
            midColor.a = 0.4f;
            Handles.color = midColor;
            foreach (var edge in edges)
            {
                float hStart = ResolveCornerHeight(heightCache, heightSettings, edge.Item1.x, edge.Item1.y) + 0.031f;
                float hEnd = ResolveCornerHeight(heightCache, heightSettings, edge.Item2.x, edge.Item2.y) + 0.031f;
                Vector3 start = new Vector3(startX + edge.Item1.x * TILE_SIZE, hStart, startZ + edge.Item1.y * TILE_SIZE);
                Vector3 end = new Vector3(startX + edge.Item2.x * TILE_SIZE, hEnd, startZ + edge.Item2.y * TILE_SIZE);
                Handles.DrawLine(start, end, 3f);
            }

            // Core border (thinnest, most opaque)
            Color coreColor = borderColor;
            coreColor.a = 0.8f;
            Handles.color = coreColor;
            foreach (var edge in edges)
            {
                float hStart = ResolveCornerHeight(heightCache, heightSettings, edge.Item1.x, edge.Item1.y) + 0.034f;
                float hEnd = ResolveCornerHeight(heightCache, heightSettings, edge.Item2.x, edge.Item2.y) + 0.034f;
                Vector3 start = new Vector3(startX + edge.Item1.x * TILE_SIZE, hStart, startZ + edge.Item1.y * TILE_SIZE);
                Vector3 end = new Vector3(startX + edge.Item2.x * TILE_SIZE, hEnd, startZ + edge.Item2.y * TILE_SIZE);
                Handles.DrawLine(start, end, 2f);
            }
        }
        
        private static void DrawLabelWithColoredIcon(Vector3 position, string text, string terrainId, GUIStyle textStyle, Color textColor, float extraAlpha = 1f)
        {
            // Convert world position to GUI position
            Vector2 guiPos = HandleUtility.WorldToGUIPoint(position);
            
            // Calculate text dimensions
            s_LabelContent.text = text;
            Vector2 textSize = textStyle.CalcSize(s_LabelContent);
            
            // Add padding for the colored icon
            float iconSize = 8f;
            float iconPadding = 3f;
            float totalWidth = textSize.x + iconSize + iconPadding * 2;
            
            // Position for the whole label (centered)
            Rect labelRect = new Rect(guiPos.x - totalWidth / 2, guiPos.y - textSize.y / 2, totalWidth, textSize.y);

            // Clamp into view and compute an anchor-based edge fade (so labels fade out as anchor leaves)
            float alphaMul = 1f;
            var sv = SceneView.currentDrawingSceneView;
            if (sv != null)
            {
                float viewW = sv.position.width;
                float viewH = sv.position.height;
                float pad = 8f;
                // Distance of anchor from viewport (0 if inside)
                float dx = (guiPos.x < 0) ? -guiPos.x : (guiPos.x > viewW ? guiPos.x - viewW : 0f);
                float dy = (guiPos.y < 0) ? -guiPos.y : (guiPos.y > viewH ? guiPos.y - viewH : 0f);
                float d = Mathf.Max(dx, dy);
                float band = 24f;
                alphaMul = Mathf.Clamp01(1f - d / band);
                // Clamp label rect to stay readable while fading
                labelRect.x = Mathf.Clamp(labelRect.x, pad, viewW - labelRect.width - pad);
                labelRect.y = Mathf.Clamp(labelRect.y, pad, viewH - labelRect.height - pad);
                // If fully outside beyond band, skip
                if (alphaMul <= 0.001f) return;
            }
            
            // Always draw an outline for better readability
            // Draw multiple outline passes for stronger effect
            GUIStyle outlineStyle = new GUIStyle(textStyle);
            Color outlineColor = (textColor == Color.black) ? Color.white : Color.black;
            outlineStyle.normal.textColor = new Color(outlineColor.r, outlineColor.g, outlineColor.b, 0.8f * alphaMul * extraAlpha);
            
            // Draw outline in 8 directions for better coverage
            Vector2[] outlineOffsets = new Vector2[]
            {
                new Vector2(-1, -1), new Vector2(0, -1), new Vector2(1, -1),
                new Vector2(-1, 0),                      new Vector2(1, 0),
                new Vector2(-1, 1),  new Vector2(0, 1),  new Vector2(1, 1)
            };
            
            foreach (var offset in outlineOffsets)
            {
                Rect outlineTextRect = new Rect(
                    labelRect.x + iconSize + iconPadding * 2 + offset.x, 
                    labelRect.y + offset.y, 
                    textSize.x, 
                    labelRect.height
                );
                GUI.Label(outlineTextRect, s_LabelContent, outlineStyle);
                
                // Draw outline for icon too
                if (terrainDatabase != null)
                {
                    Rect outlineIconRect = new Rect(
                        labelRect.x + iconPadding + offset.x, 
                        labelRect.y + (labelRect.height - iconSize) / 2 + offset.y, 
                        iconSize, 
                        iconSize
                    );
                    EditorGUI.DrawRect(outlineIconRect, new Color(outlineColor.r, outlineColor.g, outlineColor.b, 0.3f));
                }
            }
            
            // Draw colored icon
            if (terrainDatabase != null)
            {
                Color terrainColor = GetBaseTerrainColor(terrainId);
                Rect iconRect = new Rect(labelRect.x + iconPadding, 
                    labelRect.y + (labelRect.height - iconSize) / 2, iconSize, iconSize);
                
                // Draw icon background
                Color iconFill = terrainColor; iconFill.a *= (alphaMul * extraAlpha);
                EditorGUI.DrawRect(iconRect, iconFill);
                
                // Draw icon border for clarity
                Color borderColor = (textColor == Color.black) ? Color.black : Color.white;
                borderColor.a *= (alphaMul * extraAlpha);
                Handles.DrawBezier(
                    new Vector3(iconRect.x, iconRect.y, 0),
                    new Vector3(iconRect.x + iconSize, iconRect.y, 0),
                    new Vector3(iconRect.x, iconRect.y, 0),
                    new Vector3(iconRect.x + iconSize, iconRect.y, 0),
                    borderColor, null, 1f
                );
            }
            
            // Draw the text on top
            textStyle.normal.textColor = new Color(textColor.r, textColor.g, textColor.b, textColor.a * alphaMul * extraAlpha);
            Rect textRect = new Rect(labelRect.x + iconSize + iconPadding * 2, labelRect.y, 
                textSize.x, labelRect.height);
            GUI.Label(textRect, s_LabelContent, textStyle);
        }

        // Draw label at an explicit GUI position, but compute edge-fade from the anchor world position
        private static void DrawLabelWithColoredIconAtGui(Vector3 anchorWorld, Vector2 guiPos, string text, string terrainId, GUIStyle textStyle, Color textColor, float extraAlpha = 1f)
        {
            // Compute anchor GUI for edge fade
            Vector2 anchorGui = HandleUtility.WorldToGUIPoint(anchorWorld);

            // Calculate text dimensions
            s_LabelContent.text = text;
            Vector2 textSize = textStyle.CalcSize(s_LabelContent);

            float iconSize = 8f;
            float iconPadding = 3f;
            float totalWidth = textSize.x + iconSize + iconPadding * 2f;

            // Position rect around provided GUI position
            Rect labelRect = new Rect(guiPos.x - totalWidth / 2f, guiPos.y - textSize.y / 2f, totalWidth, textSize.y);

            // Edge fade computed from anchor position, clamp rect into view to stay readable while fading
            float alphaMul = 1f;
            var sv = SceneView.currentDrawingSceneView;
            if (sv != null)
            {
                float viewW = sv.position.width;
                float viewH = sv.position.height;
                float pad = 8f;
                float dx = (anchorGui.x < 0) ? -anchorGui.x : (anchorGui.x > viewW ? anchorGui.x - viewW : 0f);
                float dy = (anchorGui.y < 0) ? -anchorGui.y : (anchorGui.y > viewH ? anchorGui.y - viewH : 0f);
                float d = Mathf.Max(dx, dy);
                float band = 24f;
                alphaMul = Mathf.Clamp01(1f - d / band);
                // Clamp visual rect
                labelRect.x = Mathf.Clamp(labelRect.x, pad, viewW - labelRect.width - pad);
                labelRect.y = Mathf.Clamp(labelRect.y, pad, viewH - labelRect.height - pad);
                if (alphaMul <= 0.001f) return;
            }

            // Outline
            GUIStyle outlineStyle = new GUIStyle(textStyle);
            Color outlineColor = (textColor == Color.black) ? Color.white : Color.black;
            outlineStyle.normal.textColor = new Color(outlineColor.r, outlineColor.g, outlineColor.b, 0.8f * alphaMul * extraAlpha);
            Vector2[] outlineOffsets = new Vector2[]
            {
                new Vector2(-1, -1), new Vector2(0, -1), new Vector2(1, -1),
                new Vector2(-1, 0),                      new Vector2(1, 0),
                new Vector2(-1, 1),  new Vector2(0, 1),  new Vector2(1, 1)
            };
            foreach (var offset in outlineOffsets)
            {
                Rect outlineTextRect = new Rect(labelRect.x + iconSize + iconPadding * 2 + offset.x,
                                                labelRect.y + offset.y,
                                                textSize.x,
                                                labelRect.height);
                GUI.Label(outlineTextRect, s_LabelContent, outlineStyle);

                if (terrainDatabase != null)
                {
                    Rect outlineIconRect = new Rect(labelRect.x + iconPadding + offset.x,
                        labelRect.y + (labelRect.height - iconSize) / 2 + offset.y,
                        iconSize, iconSize);
                    EditorGUI.DrawRect(outlineIconRect, new Color(outlineColor.r, outlineColor.g, outlineColor.b, 0.3f));
                }
            }

            // Icon
            if (terrainDatabase != null)
            {
                Color terrainColor = GetBaseTerrainColor(terrainId);
                Rect iconRect = new Rect(labelRect.x + iconPadding,
                                         labelRect.y + (labelRect.height - iconSize) / 2,
                                         iconSize, iconSize);

                Color fill = terrainColor; fill.a *= (alphaMul * extraAlpha);
                EditorGUI.DrawRect(iconRect, fill);
                Color borderColor = (textColor == Color.black) ? Color.black : Color.white;
                borderColor.a *= (alphaMul * extraAlpha);
                Handles.DrawBezier(new Vector3(iconRect.x, iconRect.y, 0),
                                   new Vector3(iconRect.x + iconSize, iconRect.y, 0),
                                   new Vector3(iconRect.x, iconRect.y, 0),
                                   new Vector3(iconRect.x + iconSize, iconRect.y, 0),
                                   borderColor, null, 1f);
            }

            // Text
            textStyle.normal.textColor = new Color(textColor.r, textColor.g, textColor.b, textColor.a * alphaMul * extraAlpha);
            Rect textRect = new Rect(labelRect.x + iconSize + iconPadding * 2, labelRect.y, textSize.x, labelRect.height);
            GUI.Label(textRect, s_LabelContent, textStyle);
        }
        
        private static void DrawIslandBorders(TerrainIsland island, int mapWidth, int mapHeight, float startX, float startZ, TerrainHeightCache heightCache, TerrainHeightSettings heightSettings, Color borderColor)
        {
            Handles.color = borderColor;
            float borderThickness = 3f;
            var islandTiles = island.TileSet;

            // Check each tile in the island for border edges
            foreach (var tile in island.tiles)
            {
                float tileX = startX + tile.x * TILE_SIZE;
                float tileZ = startZ + tile.y * TILE_SIZE;
                // Check all 4 edges
                // Top edge (z+)
                if (tile.y >= mapHeight - 1 || !islandTiles.Contains(new Vector2Int(tile.x, tile.y + 1)))
                {
                    float hStart = ResolveCornerHeight(heightCache, heightSettings, tile.x, tile.y + 1) + 0.02f;
                    float hEnd = ResolveCornerHeight(heightCache, heightSettings, tile.x + 1, tile.y + 1) + 0.02f;
                    Vector3 lineStart = new Vector3(tileX, hStart, tileZ + TILE_SIZE);
                    Vector3 lineEnd = new Vector3(tileX + TILE_SIZE, hEnd, tileZ + TILE_SIZE);
                    Handles.DrawLine(lineStart, lineEnd, borderThickness);
                }
                
                // Right edge (x+)
                if (tile.x >= mapWidth - 1 || !islandTiles.Contains(new Vector2Int(tile.x + 1, tile.y)))
                {
                    float hStart = ResolveCornerHeight(heightCache, heightSettings, tile.x + 1, tile.y) + 0.02f;
                    float hEnd = ResolveCornerHeight(heightCache, heightSettings, tile.x + 1, tile.y + 1) + 0.02f;
                    Vector3 lineStart = new Vector3(tileX + TILE_SIZE, hStart, tileZ);
                    Vector3 lineEnd = new Vector3(tileX + TILE_SIZE, hEnd, tileZ + TILE_SIZE);
                    Handles.DrawLine(lineStart, lineEnd, borderThickness);
                }
                
                // Bottom edge (z-)
                if (tile.y <= 0 || !islandTiles.Contains(new Vector2Int(tile.x, tile.y - 1)))
                {
                    float hStart = ResolveCornerHeight(heightCache, heightSettings, tile.x, tile.y) + 0.02f;
                    float hEnd = ResolveCornerHeight(heightCache, heightSettings, tile.x + 1, tile.y) + 0.02f;
                    Vector3 lineStart = new Vector3(tileX, hStart, tileZ);
                    Vector3 lineEnd = new Vector3(tileX + TILE_SIZE, hEnd, tileZ);
                    Handles.DrawLine(lineStart, lineEnd, borderThickness);
                }
                
                // Left edge (x-)
                if (tile.x <= 0 || !islandTiles.Contains(new Vector2Int(tile.x - 1, tile.y)))
                {
                    float hStart = ResolveCornerHeight(heightCache, heightSettings, tile.x, tile.y) + 0.02f;
                    float hEnd = ResolveCornerHeight(heightCache, heightSettings, tile.x, tile.y + 1) + 0.02f;
                    Vector3 lineStart = new Vector3(tileX, hStart, tileZ);
                    Vector3 lineEnd = new Vector3(tileX, hEnd, tileZ + TILE_SIZE);
                    Handles.DrawLine(lineStart, lineEnd, borderThickness);
                }
            }
        }
        
        private static List<TerrainIsland> GetOrCreateIslands(TerrainAssetAdapter terrain, float cameraDistance)
        {
            // Check if terrain changed or we need to rebuild
            if (terrain != lastCachedTerrain || !islandCache.ContainsKey(terrain))
            {
                // Terrain changed, rebuild islands
                islandCache[terrain] = FindTerrainIslands(terrain, cameraDistance);
                lastCachedTerrain = terrain;
                lastIslandCameraDistance = cameraDistance;
            }
            else
            {
                var islands = islandCache[terrain];
                
                // Only recalculate positions when camera has stopped moving
                if (!cameraIsMoving)
                {
                    // Check if we need to update based on significant distance change
                    float distanceChange = Mathf.Abs(cameraDistance - lastIslandCameraDistance);
                    if (distanceChange > 5f) // Only update if zoom changed significantly
                    {
                        foreach (var island in islands)
                        {
                            island.CalculateLabelPositions(cameraDistance);
                        }
                        lastIslandCameraDistance = cameraDistance;
                    }
                }
            }
            
            return islandCache[terrain];
        }
        
        private static List<TerrainIsland> FindTerrainIslands(TerrainAssetAdapter terrain, float cameraDistance)
        {
            if (terrain == null || terrain.m_Terrains == null)
                return new List<TerrainIsland>();
            
            int width = terrain.m_Width;
            int height = terrain.m_Height;
            bool[,] visited = new bool[width, height];
            List<TerrainIsland> islands = new List<TerrainIsland>();

            TerrainVirtualGrid grid = GetVirtualGrid(terrain);
            if (grid == null)
            {
                return islands;
            }
            
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (!visited[x, y])
                    {
                        string terrainId = grid.GetTerrainId(x, y);
                        if (IsEmptyTerrain(terrainId))
                        {
                            visited[x, y] = true;
                            continue;
                        }
                        
                        // Start flood fill for this island
                        TerrainIsland island = new TerrainIsland(terrainId);
                        Queue<Vector2Int> queue = new Queue<Vector2Int>();
                        queue.Enqueue(new Vector2Int(x, y));
                        visited[x, y] = true;
                        
                        while (queue.Count > 0)
                        {
                            Vector2Int current = queue.Dequeue();
                            island.AddTile(current);
                            
                            // Check 4 neighbors
                            foreach (var dir in Directions4)
                            {
                                int nx = current.x + dir.x;
                                int ny = current.y + dir.y;
                                
                                // Check bounds
                                if (nx >= 0 && nx < width && ny >= 0 && ny < height && !visited[nx, ny])
                                {
                                    string neighborTerrain = grid.GetTerrainId(nx, ny);

                                    // If same terrain type, add to queue
                                    if (!IsEmptyTerrain(neighborTerrain) && neighborTerrain == terrainId)
                                    {
                                        visited[nx, ny] = true;
                                        queue.Enqueue(new Vector2Int(nx, ny));
                                    }
                                }
                            }
                        }
                        
                        island.CalculateLabelPositions(cameraDistance);
                        islands.Add(island);
                    }
                }
            }
            
            return islands;
        }
        
        private static string GetTerrainDisplayText(string terrainId)
        {
            if (IsEmptyTerrain(terrainId))
            {
                return string.Empty;
            }

            string tid = terrainId.Replace("TID_", "");
            string name = null;
            
            if (terrainDatabase != null)
            {
                var terrain = terrainDatabase.GetTerrainType(terrainId);
                if (terrain != null && !string.IsNullOrEmpty(terrain.name))
                {
                    name = terrain.name;
                    if (name.StartsWith("MTID_"))
                        name = name.Substring(5);
                }
            }
            
            switch (textDisplayMode)
            {
                case TextDisplayMode.ShowTID:
                    return tid;
                    
                case TextDisplayMode.ShowName:
                    return name ?? tid; // Fallback to TID if no name
                    
                case TextDisplayMode.ShowBoth:
                    if (name != null && name != terrainId)
                    {
                        return tid + "\n" + name;
                    }
                    return tid;
                    
                default:
                    return tid;
            }
        }
        
        private static void DrawResizePreview(int currentWidth, int currentHeight, float startX, float startZ, TerrainHeightCache heightCache, TerrainHeightSettings heightSettings)
        {
            if (selectedTerrain == null) return;
            
            int widthChange = newTerrainWidth - currentWidth;
            int heightChange = newTerrainHeight - currentHeight;
            
            // Show preview for both expansion and shrinking
            if (widthChange == 0 && heightChange == 0) return;
            
            // Draw expansion areas in green
            if (widthChange > 0)
            {
                Color expandColor = new Color(0f, 1f, 0f, 0.3f);
                // Expand to the right only
                for (int row = 0; row < currentHeight; row++)
                {
                    for (int col = currentWidth; col < newTerrainWidth; col++)
                    {
                        float tileX = startX + col * TILE_SIZE;
                        float tileZ = startZ + row * TILE_SIZE;
                        int sampleCol = Mathf.Clamp(col, 0, Mathf.Max(currentWidth - 1, 0));
                        int sampleRow = Mathf.Clamp(row, 0, Mathf.Max(currentHeight - 1, 0));
                        float tileHeight = ResolveTileHeight(heightCache, heightSettings, sampleCol, sampleRow);
                        FillTileQuad(s_TileVerticesOverlay, startX, startZ, col, row, heightCache, heightSettings, tileHeight, 0.05f);
                        Handles.DrawSolidRectangleWithOutline(s_TileVerticesOverlay, expandColor, Color.green);
                    }
                }
            }
            
            if (heightChange > 0)
            {
                Color expandColor = new Color(0f, 1f, 0f, 0.3f);
                // Expand to the bottom only
                for (int row = currentHeight; row < newTerrainHeight; row++)
                {
                    for (int col = 0; col < newTerrainWidth; col++)
                    {
                        // Don't double-draw the corner if both width and height are expanding
                        if (widthChange > 0 && col >= currentWidth) continue;
                        
                        float tileX = startX + col * TILE_SIZE;
                        float tileZ = startZ + row * TILE_SIZE;
                        int sampleCol = Mathf.Clamp(col, 0, Mathf.Max(currentWidth - 1, 0));
                        int sampleRow = Mathf.Clamp(row, 0, Mathf.Max(currentHeight - 1, 0));
                        float tileHeight = ResolveTileHeight(heightCache, heightSettings, sampleCol, sampleRow);
                        FillTileQuad(s_TileVerticesOverlay, startX, startZ, col, row, heightCache, heightSettings, tileHeight, 0.05f);
                        Handles.DrawSolidRectangleWithOutline(s_TileVerticesOverlay, expandColor, Color.green);
                    }
                }
            }
            
            // Continue with shrinking preview
            if (widthChange >= 0 && heightChange >= 0) return;
            
            // Calculate which tiles will be removed
            int removeLeft = 0, removeRight = 0, removeTop = 0, removeBottom = 0;
            
            if (widthChange < 0) // Shrinking width
            {
                int totalRemove = -widthChange;
                switch (shrinkHorizontal)
                {
                    case ShrinkDirection.Left:
                        removeLeft = totalRemove;
                        break;
                    case ShrinkDirection.Right:
                        removeRight = totalRemove;
                        break;
                    case ShrinkDirection.Center:
                        removeLeft = totalRemove / 2;
                        removeRight = totalRemove - removeLeft;
                        break;
                }
            }
            
            if (heightChange < 0) // Shrinking height
            {
                int totalRemove = -heightChange;
                switch (shrinkVertical)
                {
                    case ShrinkDirectionVertical.Top:
                        removeTop = totalRemove;
                        break;
                    case ShrinkDirectionVertical.Bottom:
                        removeBottom = totalRemove;
                        break;
                    case ShrinkDirectionVertical.Center:
                        removeBottom = totalRemove / 2;
                        removeTop = totalRemove - removeBottom;
                        break;
                }
            }
            
            // Draw red overlay on tiles that will be removed
            Color removeColor = new Color(1f, 0f, 0f, 0.3f);
            Color removeBorder = new Color(1f, 0f, 0f, 0.8f);
            
            // Draw left removal area
            if (removeLeft > 0)
            {
                for (int row = 0; row < currentHeight; row++)
                {
                    for (int col = 0; col < removeLeft; col++)
                    {
                        DrawRemovalTile(col, row, startX, startZ, heightCache, heightSettings, removeColor);
                    }
                }
            }
            
            // Draw right removal area
            if (removeRight > 0)
            {
                for (int row = 0; row < currentHeight; row++)
                {
                    for (int col = currentWidth - removeRight; col < currentWidth; col++)
                    {
                        DrawRemovalTile(col, row, startX, startZ, heightCache, heightSettings, removeColor);
                    }
                }
            }
            
            // Draw bottom removal area
            if (removeBottom > 0)
            {
                for (int row = 0; row < removeBottom; row++)
                {
                    for (int col = removeLeft; col < currentWidth - removeRight; col++)
                    {
                        DrawRemovalTile(col, row, startX, startZ, heightCache, heightSettings, removeColor);
                    }
                }
            }
            
            // Draw top removal area
            if (removeTop > 0)
            {
                for (int row = currentHeight - removeTop; row < currentHeight; row++)
                {
                    for (int col = removeLeft; col < currentWidth - removeRight; col++)
                    {
                        DrawRemovalTile(col, row, startX, startZ, heightCache, heightSettings, removeColor);
                    }
                }
            }
            
            // Draw border around removal areas
            Handles.color = removeBorder;
            float borderY = heightSettings.offset + 0.08f;
            
            // Left border
            if (removeLeft > 0)
            {
                Vector3 start = new Vector3(startX + removeLeft * TILE_SIZE, borderY, startZ);
                Vector3 end = new Vector3(startX + removeLeft * TILE_SIZE, borderY, startZ + currentHeight * TILE_SIZE);
                Handles.DrawLine(start, end, 3f);
            }
            
            // Right border
            if (removeRight > 0)
            {
                Vector3 start = new Vector3(startX + (currentWidth - removeRight) * TILE_SIZE, borderY, startZ);
                Vector3 end = new Vector3(startX + (currentWidth - removeRight) * TILE_SIZE, borderY, startZ + currentHeight * TILE_SIZE);
                Handles.DrawLine(start, end, 3f);
            }
            
            // Bottom border
            if (removeBottom > 0)
            {
                Vector3 start = new Vector3(startX, borderY, startZ + removeBottom * TILE_SIZE);
                Vector3 end = new Vector3(startX + currentWidth * TILE_SIZE, borderY, startZ + removeBottom * TILE_SIZE);
                Handles.DrawLine(start, end, 3f);
            }
            
            // Top border
            if (removeTop > 0)
            {
                Vector3 start = new Vector3(startX, borderY, startZ + (currentHeight - removeTop) * TILE_SIZE);
                Vector3 end = new Vector3(startX + currentWidth * TILE_SIZE, borderY, startZ + (currentHeight - removeTop) * TILE_SIZE);
                Handles.DrawLine(start, end, 3f);
            }
        }
        
        private static void DrawRemovalTile(int col, int row, float startX, float startZ, TerrainHeightCache heightCache, TerrainHeightSettings heightSettings, Color color)
        {
            float tileHeight = ResolveTileHeight(heightCache, heightSettings, col, row);
            FillTileQuad(s_TileVerticesOverlay, startX, startZ, col, row, heightCache, heightSettings, tileHeight, 0.07f);

            Handles.DrawSolidRectangleWithOutline(s_TileVerticesOverlay, color, Color.clear);
            
            // Draw X pattern
            Handles.color = new Color(1f, 0f, 0f, 0.5f);
            Handles.DrawLine(
                new Vector3(startX + col * TILE_SIZE, tileHeight + 0.08f, startZ + row * TILE_SIZE),
                new Vector3(startX + (col + 1) * TILE_SIZE, tileHeight + 0.08f, startZ + (row + 1) * TILE_SIZE), 2f
            );
            Handles.DrawLine(
                new Vector3(startX + (col + 1) * TILE_SIZE, tileHeight + 0.08f, startZ + row * TILE_SIZE),
                new Vector3(startX + col * TILE_SIZE, tileHeight + 0.08f, startZ + (row + 1) * TILE_SIZE), 2f
            );
        }
        
        private static void DrawTerrainButton(TerrainType terrain, bool isUsed)
        {
            EditorGUILayout.BeginHorizontal();
            
            // Draw color swatch
            Rect colorRect = GUILayoutUtility.GetRect(20, 20, GUILayout.Width(20));
            EditorGUI.DrawRect(colorRect, terrain.color);
            EditorGUI.DrawRect(colorRect, new Color(0, 0, 0, 0.2f)); // Border
            
            // Star indicator for used terrains
            if (isUsed)
            {
                GUIStyle starStyle = new GUIStyle(EditorStyles.label);
                starStyle.normal.textColor = Color.yellow;
                starStyle.fontStyle = FontStyle.Bold;
                GUILayout.Label("★", starStyle, GUILayout.Width(20));
            }
            else
            {
                GUILayout.Label("", GUILayout.Width(20));
            }
            
            // Terrain button
            GUI.backgroundColor = terrain.tid == selectedBrushTerrain ? Color.cyan : Color.white;
            
            string displayName = terrain.tid.Replace("TID_", "");
            if (!string.IsNullOrEmpty(terrain.name) && terrain.name != terrain.tid)
            {
                displayName += $" ({terrain.name})";
            }
            
            if (GUILayout.Button(displayName, EditorStyles.toolbarButton))
            {
                selectedBrushTerrain = terrain.tid;
                SceneView.RepaintAll();
            }
            
            GUI.backgroundColor = Color.white;
            
            EditorGUILayout.EndHorizontal();
        }

        private static List<TerrainType> GetPaintableTerrains()
        {
            if (terrainDatabase == null)
            {
                paintableTerrainsCache.Clear();
                paintableTerrainsDirty = true;
                return paintableTerrainsCache;
            }

            if (paintableTerrainsDirty)
            {
                paintableTerrainsCache.Clear();
                var allTypes = terrainDatabase.GetAllTerrainTypes();
                for (int i = 0; i < allTypes.Count; i++)
                {
                    var terrain = allTypes[i];
                    if (!IsEmptyTerrain(terrain.tid))
                    {
                        paintableTerrainsCache.Add(terrain);
                    }
                }
                paintableTerrainsDirty = false;
            }

            return paintableTerrainsCache;
        }

        // Reusable GUI styles
        private static GUIStyle s_LabelStyle;
        private static GUIStyle s_LabelStyleSmall;
        private static GUIStyle s_LabelStyleHover;
    }

}
