using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

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
        
        public TerrainIsland(string id)
        {
            terrainId = id;
            tiles = new List<Vector2Int>();
            labelPositions = new List<Vector2>();
        }
        
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
            bool centerIsInTiles = tiles.Contains(centerInt);
            
            // Also check nearby tiles in case of rounding issues
            if (!centerIsInTiles)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        Vector2Int checkPos = new Vector2Int(centerInt.x + dx, centerInt.y + dy);
                        if (tiles.Contains(checkPos))
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
                    if (!tiles.Contains(candidate)) continue;
                    
                    // Count tiles in a 5x5 area around this candidate
                    int neighborCount = 0;
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        for (int dy = -2; dy <= 2; dy++)
                        {
                            Vector2Int checkPos = new Vector2Int(candidate.x + dx, candidate.y + dy);
                            if (tiles.Contains(checkPos))
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
    
    public class TerrainPaintToolWindow : EditorWindow
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
        private static Vector3 worldOffset = Vector3.zero;
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

        private static readonly HashSet<string> EmptyTerrainIds = new HashSet<string>(StringComparer.Ordinal)
        {
            "TID_無し"
        };

        private class TerrainVirtualGrid
        {
            public TerrainAssetAdapter Adapter { get; }
            public int Width { get; }
            public int Height { get; }

            private readonly int[] actualIndices;
            private readonly string[] terrainIds;

            public TerrainVirtualGrid(TerrainAssetAdapter adapter)
            {
                Adapter = adapter;
                Width = adapter.Width;
                Height = adapter.Height;

                int expectedCount = Mathf.Max(0, Width * Height);
                actualIndices = new int[expectedCount];
                terrainIds = new string[expectedCount];
                for (int i = 0; i < expectedCount; i++)
                {
                    actualIndices[i] = -1;
                    terrainIds[i] = string.Empty;
                }

                var raw = adapter.m_Terrains ?? Array.Empty<string>();
                int fill = 0;
                bool overflow = false;
                for (int rawIndex = 0; rawIndex < raw.Length; rawIndex++)
                {
                    string tid = raw[rawIndex];
                    if (IsEmptyTerrain(tid))
                    {
                        continue;
                    }

                    if (fill < expectedCount)
                    {
                        actualIndices[fill] = rawIndex;
                        terrainIds[fill] = tid;
                        fill++;
                    }
                    else
                    {
                        overflow = true;
                        break;
                    }
                }

                string assetKey = adapter.Asset != null ? AssetDatabase.GetAssetPath(adapter.Asset) : adapter.Name;

                if (fill < expectedCount)
                {
                    if (s_LoggedInsufficientTiles.Add(assetKey))
                    {
                        Debug.LogWarning($"Terrain '{adapter.Name}' only provided {fill} non-empty tiles but expected {expectedCount}. Remaining slots will be empty.");
                    }
                }
                else if (overflow)
                {
                    if (s_LoggedOverflowTiles.Add(assetKey))
                    {
                        Debug.LogWarning($"Terrain '{adapter.Name}' has more non-empty tiles than expected ({expectedCount}). Extra tiles will be ignored in the virtual view.");
                    }
                }
            }

            public string GetTerrainId(int x, int y)
            {
                int virtualIndex = GetVirtualIndex(x, y);
                if (virtualIndex < 0)
                {
                    return string.Empty;
                }
                return terrainIds[virtualIndex];
            }

            public int GetActualIndex(int x, int y)
            {
                int virtualIndex = GetVirtualIndex(x, y);
                if (virtualIndex < 0)
                {
                    return -1;
                }
                return actualIndices[virtualIndex];
            }

            private int GetVirtualIndex(int x, int y)
            {
                if (x < 0 || y < 0 || x >= Width || y >= Height)
                {
                    return -1;
                }
                return y * Width + x;
            }
        }

        private static readonly Dictionary<TerrainAssetAdapter, TerrainVirtualGrid> s_VirtualGrids =
            new Dictionary<TerrainAssetAdapter, TerrainVirtualGrid>();
        private static readonly HashSet<string> s_LoggedInsufficientTiles = new HashSet<string>();
        private static readonly HashSet<string> s_LoggedOverflowTiles = new HashSet<string>();
        
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
        
        // Per-frame hover connected-region cache
        private static HashSet<Vector2Int> cachedHoverRegion = null;
        private static Vector2Int cachedHoverTileForRegion = new Vector2Int(-1, -1);
        private static int cachedRegionWidth = -1;
        private static int cachedRegionHeight = -1;
        private static TerrainAssetAdapter cachedRegionTerrain = null;
        private const string PREFS_ENABLED = PREFS_PREFIX + "Enabled";
        private const string PREFS_SHOW_GRID = PREFS_PREFIX + "ShowGrid";
        private const string PREFS_TEXT_SIZE = PREFS_PREFIX + "TextSize";
        private const string PREFS_TEXT_COLOR = PREFS_PREFIX + "TextColor";
        private const string PREFS_GRID_COLOR = PREFS_PREFIX + "GridColor";
        private const string PREFS_GRID_THICKNESS = PREFS_PREFIX + "GridThickness";
        private const string PREFS_WORLD_OFFSET = PREFS_PREFIX + "WorldOffset";
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
        
        // Advanced tab variables for resize
        private static int newTerrainWidth = 50;
        private static int newTerrainHeight = 50;
        
        // For shrinking, use enums to track which side to remove from
        private enum ShrinkDirection
        {
            Left,
            Right,
            Center
        }
        private static ShrinkDirection shrinkHorizontal = ShrinkDirection.Right;
        private static int shiftAmount = 1;
        
        private enum ShrinkDirectionVertical
        {
            Top,
            Bottom,
            Center
        }
        private static ShrinkDirectionVertical shrinkVertical = ShrinkDirectionVertical.Bottom;
        
        // Advanced operation preview modes
        private enum MirrorMode
        {
            None,
            Horizontal,
            Vertical
        }
        private static MirrorMode mirrorPreviewMode = MirrorMode.None;
        
        private enum ShiftDirection
        {
            None,
            Left,
            Right,
            Up,
            Down
        }
        private static ShiftDirection shiftPreviewMode = ShiftDirection.None;
        private static string[] previewTerrains = null;
        
        // PNG Export variables
        private static int exportPixelsPerTile = 20;
        private static bool exportIncludeGrid = true;
        private static Color exportGridColor = new Color(0.3f, 0.3f, 0.3f, 1f);
        private static int exportGridThickness = 1;
        private static string exportPath = "";
        
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
            cachedHoverRegion = null;
            s_LabelNodes.Clear();
            SceneView.RepaintAll();
        }
        
        private void LoadTerrainDatabase()
        {
            terrainDatabase = TerrainTypeDatabase.Instance;
            if (terrainDatabase == null)
            {
                Debug.LogWarning("TerrainTypeDatabase not found. Run 'Tools/Parse Terrain XML' to create it.");
            }
            else
            {
                terrainColorCache.Clear();
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
                if (!terrainColorCache.TryGetValue(terrainId, out Color tileColor))
                {
                    tileColor = terrainDatabase.GetTerrainColor(terrainId, Color.gray);
                    terrainColorCache[terrainId] = tileColor;
                }
                return GetContrastColor(tileColor);
            }
            return textColor;
        }

        // Modifier detection for sampling (support Ctrl and Cmd)
        private static bool IsSamplingModifier(Event e)
        {
            return e != null && (e.control || e.command);
        }

        private static bool IsEmptyTerrain(string terrainId)
        {
            if (string.IsNullOrEmpty(terrainId))
            {
                return true;
            }

            return EmptyTerrainIds.Contains(terrainId);
        }

        private static TerrainVirtualGrid GetVirtualGrid(TerrainAssetAdapter terrain)
        {
            if (terrain == null)
            {
                return null;
            }

            if (!s_VirtualGrids.TryGetValue(terrain, out TerrainVirtualGrid grid))
            {
                grid = new TerrainVirtualGrid(terrain);
                s_VirtualGrids[terrain] = grid;
            }

            return grid;
        }

        private static void InvalidateVirtualGrid(TerrainAssetAdapter terrain)
        {
            if (terrain != null)
            {
                s_VirtualGrids.Remove(terrain);
            }
        }

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
                if (cachedRegionTerrain == terrain)
                {
                    cachedRegionTerrain = null;
                    cachedHoverRegion = null;
                }

                string assetKey = terrain.Asset != null ? AssetDatabase.GetAssetPath(terrain.Asset) : terrain.Name;
                s_LoggedInsufficientTiles.Remove(assetKey);
                s_LoggedOverflowTiles.Remove(assetKey);
            }
        }

        // Per-session caches
        private static readonly Dictionary<string, Color> terrainColorCache = new Dictionary<string, Color>();
        private static GUIContent s_LabelContent = new GUIContent();
        private static System.Collections.Generic.Dictionary<string,float> labelAlphaStates = new System.Collections.Generic.Dictionary<string,float>();
        
        private void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            Undo.undoRedoPerformed -= OnUndoRedo;
        }
        
        private void OnDestroy()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            Undo.undoRedoPerformed -= OnUndoRedo;
        }
        
        private void LoadSettings()
        {
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
                cachedHoverRegion = null;
                cachedRegionTerrain = null;
                lastCachedTerrain = null;
                InvalidateVirtualGrid(selectedTerrain);
                s_LabelNodes.Clear();
                labelAlphaStates.Clear();
            }
            
            // Relaxation: committed defaults (no prefs load)
        }
        
        private void SaveSettings()
        {
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
                    cachedHoverRegion = null;
                    cachedRegionTerrain = null;
                    lastCachedTerrain = null;
                    InvalidateVirtualGrid(selectedTerrain);
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
                                var allTypes = terrainDatabase.GetAllTerrainTypes();
                                if (allTypes.Count > 0)
                                {
                                    selectedBrushTerrain = allTypes[0].tid;
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
                                Color hColor = terrainDatabase.GetTerrainColor(hoveredIdForPanel, Color.gray);
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
                            Color terrainColor = terrainDatabase.GetTerrainColor(selectedBrushTerrain, Color.gray);
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
                        
                        var allTypes = terrainDatabase.GetAllTerrainTypes();
                        
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
        
        private void DrawAdvancedTab()
        {
            // Terrain Resize Section
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Terrain Resize", EditorStyles.boldLabel);
            
            // Initialize values from current terrain if they haven't been set
            if (newTerrainWidth == 50 || newTerrainHeight == 50) // Default values
            {
                newTerrainWidth = selectedTerrain.m_Width;
                newTerrainHeight = selectedTerrain.m_Height;
            }
            
            // Current dimensions info
            EditorGUILayout.HelpBox(
                $"Current Size: {selectedTerrain.m_Width} x {selectedTerrain.m_Height}\n" +
                $"Total Tiles: {selectedTerrain.m_Width * selectedTerrain.m_Height}",
                MessageType.None);
            
            EditorGUILayout.Space(5);
            
            // New dimensions
            EditorGUILayout.LabelField("New Dimensions", EditorStyles.miniBoldLabel);
            EditorGUI.BeginChangeCheck();
            newTerrainWidth = EditorGUILayout.IntField("New Width", Mathf.Max(1, newTerrainWidth));
            newTerrainHeight = EditorGUILayout.IntField("New Height", Mathf.Max(1, newTerrainHeight));
            bool resizeChanged = EditorGUI.EndChangeCheck();
            
            // Calculate size change
            int widthChange = newTerrainWidth - selectedTerrain.m_Width;
            int heightChange = newTerrainHeight - selectedTerrain.m_Height;
            
            if (widthChange != 0 || heightChange != 0)
            {
                EditorGUILayout.Space(5);
                
                // Width changes - only expand right
                if (widthChange > 0)
                {
                    EditorGUILayout.HelpBox($"Will add {widthChange} columns to the right", MessageType.Info);
                }
                else if (widthChange < 0)
                {
                    EditorGUILayout.HelpBox($"Shrinking by {-widthChange} columns will permanently lose tile data!", MessageType.Warning);
                    EditorGUI.BeginChangeCheck();
                    shrinkHorizontal = (ShrinkDirection)EditorGUILayout.EnumPopup("Remove from", shrinkHorizontal);
                    if (EditorGUI.EndChangeCheck())
                    {
                        SceneView.RepaintAll();
                    }
                }
                
                // Height changes - only expand bottom
                if (heightChange > 0)
                {
                    EditorGUILayout.HelpBox($"Will add {heightChange} rows to the bottom", MessageType.Info);
                }
                else if (heightChange < 0)
                {
                    EditorGUILayout.HelpBox($"Shrinking by {-heightChange} rows will permanently lose tile data!", MessageType.Warning);
                    EditorGUI.BeginChangeCheck();
                    shrinkVertical = (ShrinkDirectionVertical)EditorGUILayout.EnumPopup("Remove from", shrinkVertical);
                    if (EditorGUI.EndChangeCheck())
                    {
                        SceneView.RepaintAll();
                    }
                }
                
                EditorGUILayout.Space(5);
                
                // Always generate preview when dimensions change
                if (resizeChanged)
                {
                    SceneView.RepaintAll();
                }
                
                EditorGUILayout.Space(10);
                
                if (GUILayout.Button("Apply Resize", GUILayout.Height(30)))
                {
                    bool proceed = true;
                    
                    // Warn if shrinking
                    if (widthChange < 0 || heightChange < 0)
                    {
                        proceed = EditorUtility.DisplayDialog(
                            "Terrain Resize Warning",
                            "Shrinking the terrain will permanently lose tile data that cannot be recovered by re-expanding.\n\n" +
                            "This action can be undone with Ctrl+Z.\n\n" +
                            "Continue?",
                            "Yes, Resize",
                            "Cancel"
                        );
                    }
                    
                    if (proceed)
                    {
                        ResizeTerrain();
                        previewTerrains = null;
                    }
                }
            }
            else
            {
                EditorGUILayout.HelpBox("Adjust width or height to enable resize options.", MessageType.Info);
                previewTerrains = null;
            }
            
            EditorGUILayout.Space(20);
            
            // Mirror/Flip Tools
            EditorGUILayout.LabelField("Mirror/Flip Tools", EditorStyles.boldLabel);
            
            EditorGUI.BeginChangeCheck();
            mirrorPreviewMode = (MirrorMode)EditorGUILayout.EnumPopup("Mirror Mode", mirrorPreviewMode);
            if (EditorGUI.EndChangeCheck())
            {
                if (mirrorPreviewMode != MirrorMode.None)
                {
                    GenerateMirrorPreview();
                }
                else
                {
                    previewTerrains = null;
                }
                SceneView.RepaintAll();
            }
            
            if (mirrorPreviewMode != MirrorMode.None)
            {
                EditorGUILayout.HelpBox("Preview is shown in Scene view. Green = current, Blue = preview", MessageType.Info);
                
                if (GUILayout.Button($"Apply {mirrorPreviewMode} Mirror", GUILayout.Height(25)))
                {
                    if (mirrorPreviewMode == MirrorMode.Horizontal)
                    {
                        MirrorTerrainHorizontal();
                    }
                    else if (mirrorPreviewMode == MirrorMode.Vertical)
                    {
                        MirrorTerrainVertical();
                    }
                    mirrorPreviewMode = MirrorMode.None;
                    previewTerrains = null;
                    SceneView.RepaintAll();
                }
                
                if (GUILayout.Button("Cancel", GUILayout.Height(20)))
                {
                    mirrorPreviewMode = MirrorMode.None;
                    previewTerrains = null;
                    SceneView.RepaintAll();
                }
            }
            
            // Shift Operations
            EditorGUILayout.Space(20);
            EditorGUILayout.LabelField("Shift Operations", EditorStyles.boldLabel);
            
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Shift Amount:", GUILayout.Width(80));
            EditorGUI.BeginChangeCheck();
            shiftAmount = EditorGUILayout.IntSlider(shiftAmount, 1, Mathf.Max(selectedTerrain.m_Width, selectedTerrain.m_Height));
            bool shiftAmountChanged = EditorGUI.EndChangeCheck();
            EditorGUILayout.EndHorizontal();
            
            EditorGUI.BeginChangeCheck();
            shiftPreviewMode = (ShiftDirection)EditorGUILayout.EnumPopup("Shift Direction", shiftPreviewMode);
            bool shiftModeChanged = EditorGUI.EndChangeCheck();
            
            if ((shiftModeChanged || shiftAmountChanged) && shiftPreviewMode != ShiftDirection.None)
            {
                GenerateShiftPreview();
                SceneView.RepaintAll();
            }
            
            if (shiftPreviewMode != ShiftDirection.None)
            {
                EditorGUILayout.HelpBox("Preview is shown in Scene view. Green = current, Blue = preview\nData shifted out of bounds will be replaced with MTID_Nothing", MessageType.Info);
                
                if (GUILayout.Button($"Apply Shift {shiftPreviewMode}", GUILayout.Height(25)))
                {
                    switch (shiftPreviewMode)
                    {
                        case ShiftDirection.Left:
                            ShiftTerrainHorizontal(-shiftAmount);
                            break;
                        case ShiftDirection.Right:
                            ShiftTerrainHorizontal(shiftAmount);
                            break;
                        case ShiftDirection.Up:
                            ShiftTerrainVertical(-shiftAmount);
                            break;
                        case ShiftDirection.Down:
                            ShiftTerrainVertical(shiftAmount);
                            break;
                    }
                    shiftPreviewMode = ShiftDirection.None;
                    previewTerrains = null;
                    SceneView.RepaintAll();
                }
                
                if (GUILayout.Button("Cancel", GUILayout.Height(20)))
                {
                    shiftPreviewMode = ShiftDirection.None;
                    previewTerrains = null;
                    SceneView.RepaintAll();
                }
            }
            
            // PNG Export Section
            EditorGUILayout.Space(20);
            EditorGUILayout.LabelField("Export to PNG", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Export the terrain visualization as a PNG image file.", MessageType.Info);
            
            EditorGUILayout.Space(5);
            
            // Pixels per tile
            EditorGUILayout.LabelField("Image Settings", EditorStyles.miniBoldLabel);
            exportPixelsPerTile = EditorGUILayout.IntSlider("Pixels Per Tile", exportPixelsPerTile, 5, 100);
            
            // Calculate and show output size
            int outputWidth = selectedTerrain.m_Width * exportPixelsPerTile;
            int outputHeight = selectedTerrain.m_Height * exportPixelsPerTile;
            if (exportIncludeGrid)
            {
                outputWidth += (selectedTerrain.m_Width + 1) * exportGridThickness;
                outputHeight += (selectedTerrain.m_Height + 1) * exportGridThickness;
            }
            EditorGUILayout.LabelField($"Output Size: {outputWidth} x {outputHeight} pixels", EditorStyles.miniLabel);
            
            EditorGUILayout.Space(5);
            
            // Grid options
            EditorGUILayout.LabelField("Grid Options", EditorStyles.miniBoldLabel);
            exportIncludeGrid = EditorGUILayout.Toggle("Include Grid", exportIncludeGrid);
            
            if (exportIncludeGrid)
            {
                EditorGUI.indentLevel++;
                exportGridColor = EditorGUILayout.ColorField("Grid Color", exportGridColor);
                exportGridThickness = EditorGUILayout.IntSlider("Grid Thickness", exportGridThickness, 1, 5);
                EditorGUI.indentLevel--;
            }
            
            EditorGUILayout.Space(5);
            
            // Export button
            if (GUILayout.Button("Export to PNG...", GUILayout.Height(30)))
            {
                // Open save file dialog
                string defaultPath = TerrainPNGExporter.GetDefaultExportPath(selectedTerrain);
                string path = EditorUtility.SaveFilePanel(
                    "Export Terrain as PNG",
                    System.IO.Path.GetDirectoryName(defaultPath),
                    System.IO.Path.GetFileName(defaultPath),
                    "png");
                
                if (!string.IsNullOrEmpty(path))
                {
                    TerrainPNGExporter.ExportToPNG(
                        selectedTerrain,
                        terrainDatabase,
                        exportPixelsPerTile,
                        exportIncludeGrid,
                        exportGridColor,
                        exportGridThickness,
                        colorBrightness,
                        path);
                }
            }
        }
        
        private static float GetCameraDistance(SceneView sceneView, float terrainCenterX, float terrainCenterZ, float terrainY)
        {
            if (sceneView == null || sceneView.camera == null)
                return 50f; // Default medium distance
            
            Vector3 terrainCenter = new Vector3(terrainCenterX, terrainY, terrainCenterZ);
            Vector3 cameraPos = sceneView.camera.transform.position;
            return Vector3.Distance(cameraPos, terrainCenter);
        }

        
        private static void OnSceneGUI(SceneView sceneView)
        {
            if (!visualizationEnabled || selectedTerrain == null)
                return;

            TerrainVirtualGrid currentGrid = GetVirtualGrid(selectedTerrain);
            if (currentGrid == null)
                return;

            int width = selectedTerrain.m_Width;
            int height = selectedTerrain.m_Height;
            float startX = selectedTerrain.m_X + worldOffset.x;
            float startZ = selectedTerrain.m_Z + worldOffset.z;
            float y = worldOffset.y;
            
            // Calculate camera distance for zoom-aware rendering
            float terrainCenterX = startX + (width * TILE_SIZE) / 2f;
            float terrainCenterZ = startZ + (height * TILE_SIZE) / 2f;
            float cameraDistance = GetCameraDistance(sceneView, terrainCenterX, terrainCenterZ, y);
            
            // Calculate frame delta time for smooth interpolation
            float currentTime = (float)EditorApplication.timeSinceStartup;
            float deltaTime = Mathf.Min(currentTime - lastFrameTime, 0.1f); // Cap at 100ms
            lastFrameTime = currentTime;
            
            // Detect camera movement
            // Track camera movement and stillness
            if (sceneView.camera != null)
            {
                Vector3 currentCamPos = sceneView.camera.transform.position;
                Quaternion currentCamRot = sceneView.camera.transform.rotation;
                float currentFOV = sceneView.camera.fieldOfView;
                
                // Check if camera has moved
                if (Vector3.Distance(currentCamPos, lastCameraPosition) > 0.01f ||
                    Quaternion.Angle(currentCamRot, lastCameraRotation) > 0.1f ||
                    Mathf.Abs(currentFOV - lastCameraFOV) > 0.1f)
                {
                    cameraIsMoving = true;
                    cameraStillTime = 0f;
                }
                else
                {
                    // Camera hasn't moved this frame
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
            
            // Handle mouse input for hover detection and painting
            HandleMouseInput(width, height, startX, startZ, y);
            
            bool isRepaint = Event.current.type == EventType.Repaint;

            // Track movement transitions for label layout behavior
            justStartedMoving = (!wasCameraMoving && cameraIsMoving);
            wasCameraMoving = cameraIsMoving;

            // Draw colored tiles if in color mode
            if (isRepaint && terrainDatabase != null)
            {
                for (int row = 0; row < height; row++)
                {
                    for (int col = 0; col < width; col++)
                    {
                        string terrainId = currentGrid.GetTerrainId(col, row);
                        if (IsEmptyTerrain(terrainId))
                            continue;

                        float tileX = startX + col * TILE_SIZE;
                        float tileZ = startZ + row * TILE_SIZE;

                        Vector3[] verts = new Vector3[]
                        {
                            new Vector3(tileX, y, tileZ),
                            new Vector3(tileX + TILE_SIZE, y, tileZ),
                            new Vector3(tileX + TILE_SIZE, y, tileZ + TILE_SIZE),
                            new Vector3(tileX, y, tileZ + TILE_SIZE)
                        };

                        Color tileColor = terrainDatabase.GetTerrainColor(terrainId, Color.gray);

                        // Apply brightness adjustment
                        tileColor.r = Mathf.Clamp01(tileColor.r * colorBrightness);
                        tileColor.g = Mathf.Clamp01(tileColor.g * colorBrightness);
                        tileColor.b = Mathf.Clamp01(tileColor.b * colorBrightness);
                        tileColor.a = colorOpacity;

                        Handles.DrawSolidRectangleWithOutline(verts, tileColor, Color.clear);
                    }
                }
            }
            
            // Draw grid lines with solid rendering
            if (isRepaint && showGridLines)
            {
                Handles.color = gridColor;
                
                // Draw horizontal lines using DrawLine for solid appearance
                for (int row = 0; row <= height; row++)
                {
                    Vector3 start = new Vector3(startX, y + 0.01f, startZ + row * TILE_SIZE);
                    Vector3 end = new Vector3(startX + width * TILE_SIZE, y + 0.01f, startZ + row * TILE_SIZE);
                    
                    // DrawLine uses solid lines without anti-aliasing
                    Handles.DrawLine(start, end, gridThickness);
                }
                
                // Draw vertical lines
                for (int col = 0; col <= width; col++)
                {
                    Vector3 start = new Vector3(startX + col * TILE_SIZE, y + 0.01f, startZ);
                    Vector3 end = new Vector3(startX + col * TILE_SIZE, y + 0.01f, startZ + height * TILE_SIZE);
                    
                    Handles.DrawLine(start, end, gridThickness);
                }
            }
            
            // Draw island borders (grouping is always islands)
            if (isRepaint && terrainDatabase != null)
            {
                // Get cached islands or create new ones
                List<TerrainIsland> islands = GetOrCreateIslands(selectedTerrain, cameraDistance);
                
                foreach (var island in islands)
                {
                    if (IsEmptyTerrain(island.terrainId))
                        continue;
                    
                    // Get the base color for this terrain and darken it for the border
                    Color baseColor = terrainDatabase.GetTerrainColor(island.terrainId, Color.gray);
                    Color borderColor = new Color(
                        baseColor.r * 0.6f,
                        baseColor.g * 0.6f,
                        baseColor.b * 0.6f,
                        1f
                    );
                    
                    // Draw borders around the island
                    DrawIslandBorders(island, width, height, startX, startZ, y, borderColor);
                }
            }
            
            // Compute current highlight region (normal and paint modes) and draw immediately (no fade).
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
                            // Highlight current island (already selected terrain)
                            currentHighlightRegion = GetHoverConnectedRegion(selectedTerrain, hoveredTile, width, height);
                            highlightTerrainId = selectedBrushTerrain;
                        }
                        else
                        {
                            // Highlight adjacent same-brush islands but exclude brush area
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
                        // While sampling in paint mode, highlight the hovered tile itself
                        // so it looks like the tile we'd paint with if picked.
                        currentHighlightRegion = new HashSet<Vector2Int> { hoveredTile };
                        highlightTerrainId = hoveredTerrainIdForHighlight;
                    }
                    else if (!paintMode && !IsEmptyTerrain(hoveredTerrainIdForHighlight))
                    {
                        currentHighlightRegion = GetHoverConnectedRegion(selectedTerrain, hoveredTile, width, height);
                        highlightTerrainId = hoveredTerrainIdForHighlight;
                    }
                }
            }
            
            if (isRepaint && currentHighlightRegion != null && currentHighlightRegion.Count > 0)
            {
                DrawRegionHighlight(currentHighlightRegion, startX, startZ, y, highlightTerrainId);
            }
            

            // Draw labels (only when not in ColorOnly mode)
            bool allowAnyLabels = displayMode != DisplayMode.ColorOnly;
            
            // Check if we're hovering over a tile and should show hover label (moved outside for scope)
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
            
            // Initialize styles even in ColorOnly mode for hover labels
            if (isRepaint)
            {
                // Prepare reusable styles for this frame
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
                
                // Determine LOD detail based on zoom (use global)
                
                // Per-frame caches
                var frameTextCache = new Dictionary<string, string>(64);

                // Only draw regular labels when not in ColorOnly mode
                if (allowAnyLabels)
                {
                    // Always use island grouping for labels
                    // Get cached islands (already retrieved above for borders)
                    List<TerrainIsland> islands = GetOrCreateIslands(selectedTerrain, cameraDistance);
                    
                    // Batch GUI for island labels
                    Handles.BeginGUI();

                    // Build frame label nodes list
                    var usedKeys = new HashSet<string>();
                    var frameNodes = new List<LabelNode>(64);

                    foreach (var island in islands)
                    {
                        if (IsEmptyTerrain(island.terrainId))
                            continue;
                        if (showHoverLabel && island.tiles.Contains(hoveredTile))
                            continue;

                        foreach (var labelPos in island.labelPositions)
                        {
                            float centerX = startX + labelPos.x * TILE_SIZE + TILE_SIZE * 0.5f;
                            float centerZ = startZ + labelPos.y * TILE_SIZE + TILE_SIZE * 0.5f;
                            Vector3 worldPos = new Vector3(centerX, y, centerZ);

                            string textKey = island.terrainId + "|" + textDisplayMode;
                            if (!frameTextCache.TryGetValue(textKey, out string displayText))
                            {
                                displayText = GetTerrainDisplayText(island.terrainId);
                                frameTextCache[textKey] = displayText;
                            }

                            Color labelColor = ResolveLabelColor(island.terrainId);
                            bool wantText = (displayMode != DisplayMode.ColorOnly);
                            if (!wantText) continue;

                            // Compute GUI anchor and label size for layout
                            Vector2 anchorGui = HandleUtility.WorldToGUIPoint(worldPos);
                            GUIStyle styleRef = s_LabelStyle;
                            styleRef.normal.textColor = labelColor;
                            s_LabelContent.text = displayText;
                            Vector2 size = styleRef.CalcSize(s_LabelContent);
                            float totalWidth = size.x + LABEL_ICON_SIZE + LABEL_ICON_PADDING * 2f;
                            float totalHeight = size.y;

                            // Node key: terrain + label tile
                            string nodeKey = island.terrainId + "|" + Mathf.RoundToInt(labelPos.x) + "x" + Mathf.RoundToInt(labelPos.y) + "|" + (int)textDisplayMode;
                            usedKeys.Add(nodeKey);
                            bool nodeExisted = s_LabelNodes.TryGetValue(nodeKey, out var node);
                            if (!nodeExisted)
                            {
                                node = new LabelNode { key = nodeKey, posGui = anchorGui, preservedOffset = Vector2.zero };
                                s_LabelNodes[nodeKey] = node;
                            }
                            // Compute previous offset before updating anchor for movement preservation (only meaningful if node existed)
                            Vector2 prevOffset = nodeExisted ? (node.posGui - node.anchorGui) : Vector2.zero;
                            node.anchorGui = anchorGui;
                            node.width = totalWidth;
                            node.height = totalHeight;
                            node.seenThisFrame = true;
                            // Priority: large islands get boost; hovered handled separately
                            float pr = 1f;
                            if (island.tiles != null && island.tiles.Count >= relaxLargeIslandTiles) pr *= relaxPriorityLarge;
                            node.priority = pr;
                            // If movement just started this frame, preserve current offset so we don't undo repulsion while moving
                            if (justStartedMoving)
                            {
                                node.preservedOffset = prevOffset;
                            }

                            frameNodes.Add(node);
                        }
                    }

                    // Relax layout in screen-space (no leaders)
                    if (relaxEnabled && frameNodes.Count > 0)
                    {
                        // Radius shrinks with zoom-in to keep labels tight (based on camera distance)
                        // Closer camera => smaller radius; farther => larger radius
                        float tZoom = Mathf.InverseLerp(60f, 300f, cameraDistance);
                        float radiusScale = Mathf.Lerp(0.4f, 1.0f, tZoom);
                        float maxRadius = relaxRadiusPxBase * radiusScale;
                        var sv = SceneView.currentDrawingSceneView;
                        float viewW = sv != null ? sv.position.width : Screen.width;
                        float viewH = sv != null ? sv.position.height : Screen.height;

                        if (cameraIsMoving && relaxFreezeWhileMoving)
                        {
                            // Preserve repulsion offset while moving
                            foreach (var n in frameNodes)
                            {
                                n.posGui = n.anchorGui + n.preservedOffset;
                            }
                            // Clamp to radius and viewport
                            foreach (var n in frameNodes)
                            {
                                Vector2 d = n.posGui - n.anchorGui;
                                float md = d.magnitude;
                                if (md > maxRadius)
                                {
                                    n.posGui = n.anchorGui + d * (maxRadius / md);
                                }
                                float pad = relaxViewportPad;
                                n.posGui.x = Mathf.Clamp(n.posGui.x, pad + n.width * 0.5f, viewW - pad - n.width * 0.5f);
                                n.posGui.y = Mathf.Clamp(n.posGui.y, pad + n.height * 0.5f, viewH - pad - n.height * 0.5f);
                            }
                        }
                        else
                        {
                            // Normal relaxation (when still, or when moving with freeze disabled)
                            float anchorK = relaxAnchorK;
                            int iters = relaxIterations;
                            if (cameraIsMoving && !relaxFreezeWhileMoving)
                            {
                                // Slight boost while moving to reduce visible lag
                                anchorK = Mathf.Max(relaxAnchorK, 0.28f);
                                iters = Mathf.Max(relaxIterations, 4);
                            }
                            for (int iter = 0; iter < Mathf.Max(1, iters); iter++)
                            {
                                // Anchor spring
                                foreach (var n in frameNodes)
                                {
                                    n.posGui += (n.anchorGui - n.posGui) * Mathf.Clamp01(anchorK);
                                }
                                // Repulsion
                                for (int i = 0; i < frameNodes.Count; i++)
                                {
                                    var a = frameNodes[i];
                                    for (int j = i + 1; j < frameNodes.Count; j++)
                                    {
                                        var b = frameNodes[j];
                                        float ax = a.posGui.x, ay = a.posGui.y;
                                        float bx = b.posGui.x, by = b.posGui.y;
                                        float halfW = (a.width + b.width) * 0.5f;
                                        float halfH = (a.height + b.height) * 0.5f;
                                        float dx = ax - bx;
                                        float dy = ay - by;
                                        float ox = halfW - Mathf.Abs(dx);
                                        float oy = halfH - Mathf.Abs(dy);
                                        if (ox > 0 && oy > 0)
                                        {
                                            Vector2 push;
                                            if (ox < oy)
                                            {
                                                push = new Vector2(Mathf.Sign(dx) * Mathf.Min(ox, relaxMaxStepPx), 0f);
                                            }
                                            else
                                            {
                                                push = new Vector2(0f, Mathf.Sign(dy) * Mathf.Min(oy, relaxMaxStepPx));
                                            }
                                            float pa = Mathf.Max(0.001f, a.priority);
                                            float pb = Mathf.Max(0.001f, b.priority);
                                            float sum = pa + pb;
                                            a.posGui += push * (pb / sum);
                                            b.posGui -= push * (pa / sum);
                                        }
                                    }
                                }
                                // Clamp to radius and viewport
                                foreach (var n in frameNodes)
                                {
                                    Vector2 d = n.posGui - n.anchorGui;
                                    float md = d.magnitude;
                                    if (md > maxRadius)
                                    {
                                        n.posGui = n.anchorGui + d * (maxRadius / md);
                                    }
                                    float pad = relaxViewportPad;
                                    n.posGui.x = Mathf.Clamp(n.posGui.x, pad + n.width * 0.5f, viewW - pad - n.width * 0.5f);
                                    n.posGui.y = Mathf.Clamp(n.posGui.y, pad + n.height * 0.5f, viewH - pad - n.height * 0.5f);
                                }
                            }
                            // After relaxing, update preserved offsets for next move start (only if not moving)
                            if (!cameraIsMoving)
                            {
                                foreach (var n in frameNodes)
                                {
                                    n.preservedOffset = n.posGui - n.anchorGui;
                                }
                            }
                        }
                    }

                    // Draw labels at relaxed positions, with sticky alpha-up smoothing
                    foreach (var island in islands)
                    {
                        if (IsEmptyTerrain(island.terrainId)) continue;
                        if (showHoverLabel && island.tiles.Contains(hoveredTile)) continue;

                        foreach (var labelPos in island.labelPositions)
                        {
                            float centerX = startX + labelPos.x * TILE_SIZE + TILE_SIZE * 0.5f;
                            float centerZ = startZ + labelPos.y * TILE_SIZE + TILE_SIZE * 0.5f;
                            Vector3 worldPos = new Vector3(centerX, y, centerZ);

                            string textKey = island.terrainId + "|" + textDisplayMode;
                            if (!frameTextCache.TryGetValue(textKey, out string displayText))
                            {
                                displayText = GetTerrainDisplayText(island.terrainId);
                                frameTextCache[textKey] = displayText;
                            }
                            Color labelColor = ResolveLabelColor(island.terrainId);
                            bool wantText = (displayMode != DisplayMode.ColorOnly);
                            if (!wantText) continue;
                            GUIStyle styleRef = s_LabelStyle;
                            styleRef.normal.textColor = labelColor;

                            string k = island.terrainId + "|" + Mathf.RoundToInt(labelPos.x) + "x" + Mathf.RoundToInt(labelPos.y);
                            float prev = 0f; labelAlphaStates.TryGetValue(k, out prev);
                            float target = 1f;
                            if (cameraIsMoving) target = Mathf.Max(prev, target);
                            float dt = deltaTime;
                            float newAlpha = Mathf.MoveTowards(prev, target, 10f * dt);
                            labelAlphaStates[k] = newAlpha;
                            if (newAlpha <= 0.02f) continue;

                            // Fetch node position
                            string nodeKey = island.terrainId + "|" + Mathf.RoundToInt(labelPos.x) + "x" + Mathf.RoundToInt(labelPos.y) + "|" + (int)textDisplayMode;
                            if (s_LabelNodes.TryGetValue(nodeKey, out var node))
                            {
                                DrawLabelWithColoredIconAtGui(worldPos, node.posGui, displayText, island.terrainId, styleRef, labelColor, newAlpha);
                            }
                            else
                            {
                                // Fallback to anchor if cache missed
                                Vector2 anchorGui = HandleUtility.WorldToGUIPoint(worldPos);
                                DrawLabelWithColoredIconAtGui(worldPos, anchorGui, displayText, island.terrainId, styleRef, labelColor, newAlpha);
                            }
                        }
                    }

                    // Cleanup cache entries not used this frame (mark-and-sweep)
                    foreach (var kv in s_LabelNodes.ToList())
                    {
                        if (!usedKeys.Contains(kv.Key))
                            s_LabelNodes.Remove(kv.Key);
                        else
                            kv.Value.seenThisFrame = false;
                    }

                    Handles.EndGUI();
                }
            }
            
            // Draw hover label over the hovered tile (disabled during paint mode) - works in all display modes
            if (isRepaint && showHoverLabel && !paintMode)
            {
                float hoverX = startX + hoveredTile.x * TILE_SIZE + TILE_SIZE * 0.5f;
                float hoverZ = startZ + hoveredTile.y * TILE_SIZE + TILE_SIZE * 0.5f;
                Vector3 hoverPos = new Vector3(hoverX, y, hoverZ);
                // Normal hover label
                string hoverDisplayText = GetTerrainDisplayText(hoveredTerrainId);
                
                Color hoverLabelColor = ResolveLabelColor(hoveredTerrainId, false);
                
                // Ensure hover style is initialized
                if (s_LabelStyleHover == null) s_LabelStyleHover = new GUIStyle();
                GUIStyle hoverStyle = s_LabelStyleHover;
                hoverStyle.alignment = TextAnchor.MiddleLeft;
                hoverStyle.fontStyle = FontStyle.Bold;
                hoverStyle.fontSize = Mathf.RoundToInt(14 * textSize);
                hoverStyle.normal.textColor = hoverLabelColor;
                
                // Batch begin for single hover label
                Handles.BeginGUI();
                DrawLabelWithColoredIcon(hoverPos, hoverDisplayText, hoveredTerrainId, hoverStyle, hoverLabelColor);
                Handles.EndGUI();
            }
            
            // Draw brush preview when in paint mode
            if (paintMode && isMouseOverGrid)
            {
                DrawBrushPreview(hoveredTile, width, height, startX, startZ, y);
            }
            
            // Draw previews when in Advanced tab
            if (uiTabIndex == 2 && selectedTerrain != null)
            {
                // Always show resize preview when dimensions are different
                if (newTerrainWidth != width || newTerrainHeight != height)
                {
                    DrawResizePreview(width, height, startX, startZ, y);
                }
                
                if (previewTerrains != null && (mirrorPreviewMode != MirrorMode.None || shiftPreviewMode != ShiftDirection.None))
                {
                    DrawAdvancedOperationPreview(width, height, startX, startZ, y);
                }
            }
            
            // Repaint when camera is moving or has just stopped
            if (cameraIsMoving || cameraStillTime < 1f)
            {
                sceneView.Repaint();
            }
        }
        
        private static bool isPaintingStroke = false;
        private static int paintUndoGroup = -1;
        private static HashSet<int> paintedIndicesThisDrag = new HashSet<int>();

        private static void BeginPaintStroke()
        {
            if (isPaintingStroke || selectedTerrain == null) return;
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Paint Terrain");
            paintUndoGroup = Undo.GetCurrentGroup();
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

        private static void HandleMouseInput(int width, int height, float startX, float startZ, float y)
        {
            Event currentEvent = Event.current;
            if (externalInteractionLock)
            {
                // Suppress hover and painting while another tool (Dispos) is active
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
            
            // Get mouse position in world space
            Ray ray = HandleUtility.GUIPointToWorldRay(currentEvent.mousePosition);
            
            // Calculate intersection with grid plane
            float distance = (y - ray.origin.y) / ray.direction.y;
            if (distance < 0)
            {
                isMouseOverGrid = false;
                return;
            }
            
            Vector3 hitPoint = ray.origin + ray.direction * distance;
            
            // Convert world position to grid coordinates
            int gridX = Mathf.FloorToInt((hitPoint.x - startX) / TILE_SIZE);
            int gridZ = Mathf.FloorToInt((hitPoint.z - startZ) / TILE_SIZE);
            
            // Check if mouse is over valid grid tile
            if (gridX >= 0 && gridX < width && gridZ >= 0 && gridZ < height)
            {
                hoveredTile = new Vector2Int(gridX, gridZ);
                isMouseOverGrid = true;
                
                // Only handle painting clicks when in paint mode
                if (paintMode && !externalInteractionLock)
                {
                    // Handle mouse clicks
                    if (currentEvent.type == EventType.MouseDown || currentEvent.type == EventType.MouseDrag || currentEvent.type == EventType.MouseUp)
                    {
                        if (currentEvent.button == 0) // Left click
                        {
                            // Check for modifier keys
                            if (IsSamplingModifier(currentEvent) && currentEvent.type == EventType.MouseDown) // Ctrl/Cmd + Left click - pick/sample
                            {
                                PickTerrain(hoveredTile, width);
                            }
                            else // Normal left click - paint
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
                    
                    // Block scene navigation only for left mouse button when painting
                    if (currentEvent.type == EventType.Layout && currentEvent.button == 0)
                    {
                        HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
                    }
                }
            }
            else
            {
                isMouseOverGrid = false;
                if (isPaintingStroke)
                {
                    EndPaintStroke();
                }
            }

            // Trigger a repaint when hover state changes to keep non-paint mode responsive
            if (isMouseOverGrid != prevOver || hoveredTile != prevHovered)
            {
                cachedHoverRegion = null;
                SceneView.RepaintAll();
                // Force the inspector window to repaint to update the hover information
                if (instance != null)
                {
                    instance.Repaint();
                }
            }
        }

        private static HashSet<Vector2Int> GetHoverConnectedRegion(TerrainAssetAdapter terrain, Vector2Int tile, int width, int height)
        {
            if (terrain == null) return null;
            if (cachedHoverRegion != null && cachedRegionTerrain == terrain &&
                cachedHoverTileForRegion == tile && cachedRegionWidth == width && cachedRegionHeight == height)
            {
                return cachedHoverRegion;
            }
            var region = FindConnectedRegion(terrain, tile, width, height);
            cachedHoverRegion = region;
            cachedRegionTerrain = terrain;
            cachedHoverTileForRegion = tile;
            cachedRegionWidth = width;
            cachedRegionHeight = height;
            return region;
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
            
            // Record undo state for each paint operation during the stroke
            // Unity needs a record for each modification to properly track array changes
            if (isPaintingStroke)
            {
                RecordTerrainUndo(selectedTerrain, "Paint Terrain");
            }
            
            // Now actually paint
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
                cachedHoverRegion = null;
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
            
            // Get brush area
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
            
            // Check tiles adjacent to brush area
            HashSet<Vector2Int> visited = new HashSet<Vector2Int>();
            Queue<Vector2Int> toCheck = new Queue<Vector2Int>();
            
            // Start with tiles adjacent to brush area
            foreach (var brushTile in brushArea)
            {
                foreach (var dir in Directions4)
                {
                    var neighbor = brushTile + dir;
                    if (!brushArea.Contains(neighbor) &&
                        neighbor.x >= 0 && neighbor.x < width &&
                        neighbor.y >= 0 && neighbor.y < height &&
                        !visited.Contains(neighbor))
                    {
                        string tid = grid.GetTerrainId(neighbor.x, neighbor.y);
                        if (!IsEmptyTerrain(tid) && tid == targetTerrain)
                        {
                            visited.Add(neighbor);
                            toCheck.Enqueue(neighbor);
                        }
                    }
                }
            }
            
            // Flood fill to find connected island
            while (toCheck.Count > 0)
            {
                var current = toCheck.Dequeue();
                island.Add(current);
                
                foreach (var dir in Directions4)
                {
                    var neighbor = current + dir;
                    if (neighbor.x >= 0 && neighbor.x < width &&
                        neighbor.y >= 0 && neighbor.y < height &&
                        !visited.Contains(neighbor))
                    {
                        string tid = grid.GetTerrainId(neighbor.x, neighbor.y);
                        if (!IsEmptyTerrain(tid) && tid == targetTerrain)
                        {
                            visited.Add(neighbor);
                            toCheck.Enqueue(neighbor);
                        }
                    }
                }
            }
            
            return island;
        }
        
        private static void DrawBrushPreview(Vector2Int centerTile, int width, int height, float startX, float startZ, float y)
        {
            Event currentEvent = Event.current;
            bool isSampling = IsSamplingModifier(currentEvent);
            
            if (isSampling)
            {
                // Draw sampling indicator - single tile showing the color that would be sampled
                float worldX = startX + centerTile.x * TILE_SIZE;
                float worldZ = startZ + centerTile.y * TILE_SIZE;
                
                Vector3[] verts = new Vector3[]
                {
                    new Vector3(worldX, y + 0.05f, worldZ),
                    new Vector3(worldX + TILE_SIZE, y + 0.05f, worldZ),
                    new Vector3(worldX + TILE_SIZE, y + 0.05f, worldZ + TILE_SIZE),
                    new Vector3(worldX, y + 0.05f, worldZ + TILE_SIZE)
                };
                
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
                                Color terrainColor = terrainDatabase.GetTerrainColor(terrainToSample, Color.gray);

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
                Handles.DrawSolidRectangleWithOutline(verts, sampleColor, sampleOutline);
                
                // Draw "Sample" text over the tile
                Vector3 tileCenter = new Vector3(worldX + TILE_SIZE * 0.5f, y + 0.1f, worldZ + TILE_SIZE * 0.5f);
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
                    DrawBrushTiles(centerTile, width, height, startX, startZ, y, previewColor, Color.yellow);
                }
                else
                {
                    // Get the actual color of the terrain we're painting
                    Color terrainColor = terrainDatabase.GetTerrainColor(selectedBrushTerrain, Color.gray);
                    
                    // Apply brightness adjustment (same as actual tile rendering)
                    terrainColor.r = Mathf.Clamp01(terrainColor.r * colorBrightness);
                    terrainColor.g = Mathf.Clamp01(terrainColor.g * colorBrightness);
                    terrainColor.b = Mathf.Clamp01(terrainColor.b * colorBrightness);
                    
                    // Make it semi-transparent for preview
                    Color previewColor = new Color(terrainColor.r, terrainColor.g, terrainColor.b, 0.4f);
                    
                    // Darken the adjusted color for the outline
                    Color outlineColor = new Color(
                        terrainColor.r * 0.6f,
                        terrainColor.g * 0.6f,
                        terrainColor.b * 0.6f,
                        0.8f
                    );
                    
                    // Draw the preview tiles
                    DrawBrushTiles(centerTile, width, height, startX, startZ, y, previewColor, outlineColor);
                    
                    // Draw preview borders for the new terrain
                    DrawPreviewBorders(centerTile, width, height, startX, startZ, y, outlineColor);
                }
            }
        }
        
        private static void DrawBrushTiles(Vector2Int centerTile, int width, int height, float startX, float startZ, float y, Color fillColor, Color outlineColor)
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

                        float worldX = startX + tileX * TILE_SIZE;
                        float worldZ = startZ + tileZ * TILE_SIZE;
                        
                        Vector3[] verts = new Vector3[]
                        {
                            new Vector3(worldX, y + 0.05f, worldZ),
                            new Vector3(worldX + TILE_SIZE, y + 0.05f, worldZ),
                            new Vector3(worldX + TILE_SIZE, y + 0.05f, worldZ + TILE_SIZE),
                            new Vector3(worldX, y + 0.05f, worldZ + TILE_SIZE)
                        };
                        
                        Handles.DrawSolidRectangleWithOutline(verts, fillColor, outlineColor);
                    }
                }
            }
        }
        
        private static void DrawPreviewBorders(Vector2Int centerTile, int width, int height, float startX, float startZ, float y, Color borderColor)
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
                    Vector3 lineStart = new Vector3(tileX, y + 0.06f, tileZ + TILE_SIZE);
                    Vector3 lineEnd = new Vector3(tileX + TILE_SIZE, y + 0.06f, tileZ + TILE_SIZE);
                    Handles.DrawLine(lineStart, lineEnd, borderThickness);
                }
                
                // Right edge
                if (!paintedTiles.Contains(new Vector2Int(tile.x + 1, tile.y)))
                {
                    Vector3 lineStart = new Vector3(tileX + TILE_SIZE, y + 0.06f, tileZ);
                    Vector3 lineEnd = new Vector3(tileX + TILE_SIZE, y + 0.06f, tileZ + TILE_SIZE);
                    Handles.DrawLine(lineStart, lineEnd, borderThickness);
                }
                
                // Bottom edge
                if (!paintedTiles.Contains(new Vector2Int(tile.x, tile.y - 1)))
                {
                    Vector3 lineStart = new Vector3(tileX, y + 0.06f, tileZ);
                    Vector3 lineEnd = new Vector3(tileX + TILE_SIZE, y + 0.06f, tileZ);
                    Handles.DrawLine(lineStart, lineEnd, borderThickness);
                }
                
                // Left edge
                if (!paintedTiles.Contains(new Vector2Int(tile.x - 1, tile.y)))
                {
                    Vector3 lineStart = new Vector3(tileX, y + 0.06f, tileZ);
                    Vector3 lineEnd = new Vector3(tileX, y + 0.06f, tileZ + TILE_SIZE);
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
            HashSet<Vector2Int> region = new HashSet<Vector2Int>();

            TerrainVirtualGrid grid = GetVirtualGrid(terrain);
            if (grid == null)
                return region;

            string targetTerrain = grid.GetTerrainId(startTile.x, startTile.y);
            if (IsEmptyTerrain(targetTerrain))
                return region;
            
            Queue<Vector2Int> toVisit = new Queue<Vector2Int>();
            HashSet<Vector2Int> visited = new HashSet<Vector2Int>();
            
            toVisit.Enqueue(startTile);
            visited.Add(startTile);
            
            while (toVisit.Count > 0)
            {
                Vector2Int current = toVisit.Dequeue();
                region.Add(current);
                
                foreach (var dir in Directions4)
                {
                    Vector2Int neighbor = current + dir;
                    
                    if (neighbor.x >= 0 && neighbor.x < width &&
                        neighbor.y >= 0 && neighbor.y < height &&
                        !visited.Contains(neighbor))
                    {
                        string neighborTerrain = grid.GetTerrainId(neighbor.x, neighbor.y);
                        if (!IsEmptyTerrain(neighborTerrain) && neighborTerrain == targetTerrain)
                        {
                            visited.Add(neighbor);
                            toVisit.Enqueue(neighbor);
                        }
                    }
                }
            }
            
            return region;
        }
        
        private static void DrawRegionHighlight(HashSet<Vector2Int> region, float startX, float startZ, float y, string terrainId)
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
                    edges.Add((new Vector2Int(tile.x, tile.y + 1), new Vector2Int(tile.x + 1, tile.y + 1)));
                }
                // Right edge
                if (!region.Contains(neighbors[1]))
                {
                    edges.Add((new Vector2Int(tile.x + 1, tile.y), new Vector2Int(tile.x + 1, tile.y + 1)));
                }
                // Bottom edge
                if (!region.Contains(neighbors[2]))
                {
                    edges.Add((new Vector2Int(tile.x, tile.y), new Vector2Int(tile.x + 1, tile.y)));
                }
                // Left edge
                if (!region.Contains(neighbors[3]))
                {
                    edges.Add((new Vector2Int(tile.x, tile.y), new Vector2Int(tile.x, tile.y + 1)));
                }
            }
            
            // Calculate colors based on terrain brightness for contrast
            Color baseColor = Color.gray;
            bool isDarkTerrain = false;
            if (!string.IsNullOrEmpty(terrainId) && terrainDatabase != null)
            {
                baseColor = terrainDatabase.GetTerrainColor(terrainId, Color.gray);
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
                Vector3 start = new Vector3(
                    startX + edge.Item1.x * TILE_SIZE,
                    y + 0.028f,
                    startZ + edge.Item1.y * TILE_SIZE
                );
                Vector3 end = new Vector3(
                    startX + edge.Item2.x * TILE_SIZE,
                    y + 0.028f,
                    startZ + edge.Item2.y * TILE_SIZE
                );
                Handles.DrawLine(start, end, 4f);
            }
            
            // Middle layer (medium width, medium opacity)
            Color midColor = Color.Lerp(outlineColor, borderColor, 0.5f);
            midColor.a = 0.4f;
            Handles.color = midColor;
            foreach (var edge in edges)
            {
                Vector3 start = new Vector3(
                    startX + edge.Item1.x * TILE_SIZE,
                    y + 0.031f,
                    startZ + edge.Item1.y * TILE_SIZE
                );
                Vector3 end = new Vector3(
                    startX + edge.Item2.x * TILE_SIZE,
                    y + 0.031f,
                    startZ + edge.Item2.y * TILE_SIZE
                );
                Handles.DrawLine(start, end, 3f);
            }
            
            // Core border (thinnest, most opaque)
            Color coreColor = borderColor;
            coreColor.a = 0.8f;
            Handles.color = coreColor;
            foreach (var edge in edges)
            {
                Vector3 start = new Vector3(
                    startX + edge.Item1.x * TILE_SIZE,
                    y + 0.034f,
                    startZ + edge.Item1.y * TILE_SIZE
                );
                Vector3 end = new Vector3(
                    startX + edge.Item2.x * TILE_SIZE,
                    y + 0.034f,
                    startZ + edge.Item2.y * TILE_SIZE
                );
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
                Color terrainColor = terrainDatabase.GetTerrainColor(terrainId, Color.gray);
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
                Color terrainColor = terrainDatabase.GetTerrainColor(terrainId, Color.gray);
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
        
        private static void DrawIslandBorders(TerrainIsland island, int mapWidth, int mapHeight, float startX, float startZ, float y, Color borderColor)
        {
            Handles.color = borderColor;
            float borderThickness = 3f;
            
            // Create a set for quick lookup
            HashSet<Vector2Int> islandTiles = new HashSet<Vector2Int>(island.tiles);
            
            // Check each tile in the island for border edges
            foreach (var tile in island.tiles)
            {
                float tileX = startX + tile.x * TILE_SIZE;
                float tileZ = startZ + tile.y * TILE_SIZE;
                
                // Check all 4 edges
                // Top edge (z+)
                if (tile.y >= mapHeight - 1 || !islandTiles.Contains(new Vector2Int(tile.x, tile.y + 1)))
                {
                    Vector3 lineStart = new Vector3(tileX, y + 0.02f, tileZ + TILE_SIZE);
                    Vector3 lineEnd = new Vector3(tileX + TILE_SIZE, y + 0.02f, tileZ + TILE_SIZE);
                    Handles.DrawLine(lineStart, lineEnd, borderThickness);
                }
                
                // Right edge (x+)
                if (tile.x >= mapWidth - 1 || !islandTiles.Contains(new Vector2Int(tile.x + 1, tile.y)))
                {
                    Vector3 lineStart = new Vector3(tileX + TILE_SIZE, y + 0.02f, tileZ);
                    Vector3 lineEnd = new Vector3(tileX + TILE_SIZE, y + 0.02f, tileZ + TILE_SIZE);
                    Handles.DrawLine(lineStart, lineEnd, borderThickness);
                }
                
                // Bottom edge (z-)
                if (tile.y <= 0 || !islandTiles.Contains(new Vector2Int(tile.x, tile.y - 1)))
                {
                    Vector3 lineStart = new Vector3(tileX, y + 0.02f, tileZ);
                    Vector3 lineEnd = new Vector3(tileX + TILE_SIZE, y + 0.02f, tileZ);
                    Handles.DrawLine(lineStart, lineEnd, borderThickness);
                }
                
                // Left edge (x-)
                if (tile.x <= 0 || !islandTiles.Contains(new Vector2Int(tile.x - 1, tile.y)))
                {
                    Vector3 lineStart = new Vector3(tileX, y + 0.02f, tileZ);
                    Vector3 lineEnd = new Vector3(tileX, y + 0.02f, tileZ + TILE_SIZE);
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
                            island.tiles.Add(current);
                            
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
        
        private static void DrawResizePreview(int currentWidth, int currentHeight, float startX, float startZ, float y)
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
                        
                        Vector3[] verts = new Vector3[]
                        {
                            new Vector3(tileX, y + 0.05f, tileZ),
                            new Vector3(tileX + TILE_SIZE, y + 0.05f, tileZ),
                            new Vector3(tileX + TILE_SIZE, y + 0.05f, tileZ + TILE_SIZE),
                            new Vector3(tileX, y + 0.05f, tileZ + TILE_SIZE)
                        };
                        
                        Handles.DrawSolidRectangleWithOutline(verts, expandColor, Color.green);
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
                        
                        Vector3[] verts = new Vector3[]
                        {
                            new Vector3(tileX, y + 0.05f, tileZ),
                            new Vector3(tileX + TILE_SIZE, y + 0.05f, tileZ),
                            new Vector3(tileX + TILE_SIZE, y + 0.05f, tileZ + TILE_SIZE),
                            new Vector3(tileX, y + 0.05f, tileZ + TILE_SIZE)
                        };
                        
                        Handles.DrawSolidRectangleWithOutline(verts, expandColor, Color.green);
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
                        DrawRemovalTile(col, row, startX, startZ, y, removeColor);
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
                        DrawRemovalTile(col, row, startX, startZ, y, removeColor);
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
                        DrawRemovalTile(col, row, startX, startZ, y, removeColor);
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
                        DrawRemovalTile(col, row, startX, startZ, y, removeColor);
                    }
                }
            }
            
            // Draw border around removal areas
            Handles.color = removeBorder;
            float borderY = y + 0.08f;
            
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
        
        private static void DrawRemovalTile(int col, int row, float startX, float startZ, float y, Color color)
        {
            float tileX = startX + col * TILE_SIZE;
            float tileZ = startZ + row * TILE_SIZE;
            
            Vector3[] verts = new Vector3[]
            {
                new Vector3(tileX, y + 0.07f, tileZ),
                new Vector3(tileX + TILE_SIZE, y + 0.07f, tileZ),
                new Vector3(tileX + TILE_SIZE, y + 0.07f, tileZ + TILE_SIZE),
                new Vector3(tileX, y + 0.07f, tileZ + TILE_SIZE)
            };
            
            Handles.DrawSolidRectangleWithOutline(verts, color, Color.clear);
            
            // Draw X pattern
            Handles.color = new Color(1f, 0f, 0f, 0.5f);
            Handles.DrawLine(
                new Vector3(tileX, y + 0.08f, tileZ),
                new Vector3(tileX + TILE_SIZE, y + 0.08f, tileZ + TILE_SIZE), 2f
            );
            Handles.DrawLine(
                new Vector3(tileX + TILE_SIZE, y + 0.08f, tileZ),
                new Vector3(tileX, y + 0.08f, tileZ + TILE_SIZE), 2f
            );
        }
        
        private void ResizeTerrain()
        {
            if (selectedTerrain == null) return;
            
            RecordTerrainUndo(selectedTerrain, "Resize Terrain");
            
            int oldWidth = selectedTerrain.m_Width;
            int oldHeight = selectedTerrain.m_Height;
            string[] oldTerrains = selectedTerrain.m_Terrains;
            
            // Create 2D representation of old data
            string[,] oldGrid = new string[oldHeight, oldWidth];
            for (int y = 0; y < oldHeight; y++)
            {
                for (int x = 0; x < oldWidth; x++)
                {
                    int index = y * oldWidth + x;
                    if (index < oldTerrains.Length)
                    {
                        oldGrid[y, x] = oldTerrains[index];
                    }
                }
            }
            
            // Create new grid with MTID_Nothing as default
            string[,] newGrid = new string[newTerrainHeight, newTerrainWidth];
            for (int y = 0; y < newTerrainHeight; y++)
            {
                for (int x = 0; x < newTerrainWidth; x++)
                {
                    newGrid[y, x] = "MTID_Nothing";
                }
            }
            
            // Calculate offsets based on expand/shrink direction
            int offsetX = 0;
            int offsetY = 0;
            
            int widthChange = newTerrainWidth - oldWidth;
            int heightChange = newTerrainHeight - oldHeight;
            
            // Calculate X offset
            if (widthChange > 0) // Expanding - always to the right
            {
                offsetX = 0; // Expand right means existing data stays at same position
            }
            else if (widthChange < 0) // Shrinking
            {
                switch (shrinkHorizontal)
                {
                    case ShrinkDirection.Left:
                        offsetX = widthChange;
                        break;
                    case ShrinkDirection.Right:
                        offsetX = 0;
                        break;
                    case ShrinkDirection.Center:
                        offsetX = widthChange / 2;
                        break;
                }
            }
            
            // Calculate Y offset
            if (heightChange > 0) // Expanding - always to the bottom
            {
                offsetY = 0; // Expand bottom means existing data stays at same position
            }
            else if (heightChange < 0) // Shrinking
            {
                switch (shrinkVertical)
                {
                    case ShrinkDirectionVertical.Top:
                        offsetY = heightChange;
                        break;
                    case ShrinkDirectionVertical.Bottom:
                        offsetY = 0;
                        break;
                    case ShrinkDirectionVertical.Center:
                        offsetY = heightChange / 2;
                        break;
                }
            }
            
            // Copy old data to new grid with offset
            for (int y = 0; y < oldHeight; y++)
            {
                for (int x = 0; x < oldWidth; x++)
                {
                    int newX = x + offsetX;
                    int newY = y + offsetY;
                    
                    if (newX >= 0 && newX < newTerrainWidth && 
                        newY >= 0 && newY < newTerrainHeight)
                    {
                        newGrid[newY, newX] = oldGrid[y, x];
                    }
                }
            }
            
            // Convert back to 1D array
            string[] newTerrains = new string[newTerrainWidth * newTerrainHeight];
            for (int y = 0; y < newTerrainHeight; y++)
            {
                for (int x = 0; x < newTerrainWidth; x++)
                {
                    newTerrains[y * newTerrainWidth + x] = newGrid[y, x];
                }
            }
            
            // Apply changes to terrain
            selectedTerrain.m_Width = newTerrainWidth;
            selectedTerrain.m_Height = newTerrainHeight;
            selectedTerrain.m_Terrains = newTerrains;
            
            MarkTerrainDirty(selectedTerrain);
            
            // Clear caches
            islandCache.Clear();
            lastCachedTerrain = null;
            cachedHoverRegion = null;
            s_LabelNodes.Clear();
            
            SceneView.RepaintAll();
            Debug.Log($"Terrain resized from {oldWidth}x{oldHeight} to {newTerrainWidth}x{newTerrainHeight}");
        }
        
        private static void MirrorTerrainHorizontal()
        {
            if (selectedTerrain == null) return;
            
            RecordTerrainUndo(selectedTerrain, "Mirror Terrain Horizontal");
            
            int width = selectedTerrain.m_Width;
            int height = selectedTerrain.m_Height;
            string[] terrains = selectedTerrain.m_Terrains;
            
            // Create mirrored array
            string[] mirrored = new string[terrains.Length];
            
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int srcIndex = y * width + x;
                    int destIndex = y * width + (width - 1 - x);
                    
                    if (srcIndex < terrains.Length)
                    {
                        mirrored[destIndex] = terrains[srcIndex];
                    }
                }
            }
            
            selectedTerrain.m_Terrains = mirrored;
            MarkTerrainDirty(selectedTerrain);
            
            // Clear caches
            islandCache.Clear();
            lastCachedTerrain = null;
            cachedHoverRegion = null;
            s_LabelNodes.Clear();
            
            SceneView.RepaintAll();
            Debug.Log("Terrain mirrored horizontally");
        }
        
        private static void MirrorTerrainVertical()
        {
            if (selectedTerrain == null) return;
            
            RecordTerrainUndo(selectedTerrain, "Mirror Terrain Vertical");
            
            int width = selectedTerrain.m_Width;
            int height = selectedTerrain.m_Height;
            string[] terrains = selectedTerrain.m_Terrains;
            
            // Create mirrored array
            string[] mirrored = new string[terrains.Length];
            
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int srcIndex = y * width + x;
                    int destIndex = (height - 1 - y) * width + x;
                    
                    if (srcIndex < terrains.Length)
                    {
                        mirrored[destIndex] = terrains[srcIndex];
                    }
                }
            }
            
            selectedTerrain.m_Terrains = mirrored;
            MarkTerrainDirty(selectedTerrain);
            
            // Clear caches
            islandCache.Clear();
            lastCachedTerrain = null;
            cachedHoverRegion = null;
            s_LabelNodes.Clear();
            
            SceneView.RepaintAll();
            Debug.Log("Terrain mirrored vertically");
        }
        
        private static void ShiftTerrainHorizontal(int amount)
        {
            if (selectedTerrain == null) return;
            
            RecordTerrainUndo(selectedTerrain, $"Shift Terrain {(amount > 0 ? "Right" : "Left")}");
            
            int width = selectedTerrain.m_Width;
            int height = selectedTerrain.m_Height;
            string[] terrains = selectedTerrain.m_Terrains;
            
            // Create shifted array, fill with MTID_Nothing by default
            string[] shifted = new string[terrains.Length];
            for (int i = 0; i < shifted.Length; i++)
            {
                shifted[i] = "MTID_Nothing";
            }
            
            // Copy data to shifted positions
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int srcIndex = y * width + x;
                    int newX = x + amount;
                    
                    // Only copy if the new position is within bounds
                    if (newX >= 0 && newX < width && srcIndex < terrains.Length)
                    {
                        int destIndex = y * width + newX;
                        shifted[destIndex] = terrains[srcIndex];
                    }
                }
            }
            
            selectedTerrain.m_Terrains = shifted;
            MarkTerrainDirty(selectedTerrain);
            
            // Clear caches
            islandCache.Clear();
            lastCachedTerrain = null;
            cachedHoverRegion = null;
            s_LabelNodes.Clear();
            
            SceneView.RepaintAll();
            Debug.Log($"Terrain shifted horizontally by {amount}");
        }
        
        private static void ShiftTerrainVertical(int amount)
        {
            if (selectedTerrain == null) return;
            
            RecordTerrainUndo(selectedTerrain, $"Shift Terrain {(amount > 0 ? "Down" : "Up")}");
            
            int width = selectedTerrain.m_Width;
            int height = selectedTerrain.m_Height;
            string[] terrains = selectedTerrain.m_Terrains;
            
            // Create shifted array, fill with MTID_Nothing by default
            string[] shifted = new string[terrains.Length];
            for (int i = 0; i < shifted.Length; i++)
            {
                shifted[i] = "MTID_Nothing";
            }
            
            // Copy data to shifted positions
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int srcIndex = y * width + x;
                    int newY = y + amount;
                    
                    // Only copy if the new position is within bounds
                    if (newY >= 0 && newY < height && srcIndex < terrains.Length)
                    {
                        int destIndex = newY * width + x;
                        shifted[destIndex] = terrains[srcIndex];
                    }
                }
            }
            
            selectedTerrain.m_Terrains = shifted;
            MarkTerrainDirty(selectedTerrain);
            
            // Clear caches
            islandCache.Clear();
            lastCachedTerrain = null;
            cachedHoverRegion = null;
            s_LabelNodes.Clear();
            
            SceneView.RepaintAll();
            Debug.Log($"Terrain shifted vertically by {amount}");
        }
        
        private static void GenerateResizePreview()
        {
            if (selectedTerrain == null) return;
            
            int oldWidth = selectedTerrain.m_Width;
            int oldHeight = selectedTerrain.m_Height;
            
            // For expand preview, just show current terrain
            // The scene drawing will show the new areas in green
            previewTerrains = (string[])selectedTerrain.m_Terrains.Clone();
        }
        
        private static void GenerateMirrorPreview()
        {
            if (selectedTerrain == null) return;
            
            int width = selectedTerrain.m_Width;
            int height = selectedTerrain.m_Height;
            string[] terrains = selectedTerrain.m_Terrains;
            
            previewTerrains = new string[terrains.Length];
            
            if (mirrorPreviewMode == MirrorMode.Horizontal)
            {
                // Mirror horizontally
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int srcIndex = y * width + x;
                        int destIndex = y * width + (width - 1 - x);
                        
                        if (srcIndex < terrains.Length)
                        {
                            previewTerrains[destIndex] = terrains[srcIndex];
                        }
                    }
                }
            }
            else if (mirrorPreviewMode == MirrorMode.Vertical)
            {
                // Mirror vertically
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int srcIndex = y * width + x;
                        int destIndex = (height - 1 - y) * width + x;
                        
                        if (srcIndex < terrains.Length)
                        {
                            previewTerrains[destIndex] = terrains[srcIndex];
                        }
                    }
                }
            }
        }
        
        private static void GenerateShiftPreview()
        {
            if (selectedTerrain == null) return;
            
            int width = selectedTerrain.m_Width;
            int height = selectedTerrain.m_Height;
            string[] terrains = selectedTerrain.m_Terrains;
            
            // Initialize with MTID_Nothing
            previewTerrains = new string[terrains.Length];
            for (int i = 0; i < previewTerrains.Length; i++)
            {
                previewTerrains[i] = "MTID_Nothing";
            }
            
            int shiftX = 0, shiftY = 0;
            switch (shiftPreviewMode)
            {
                case ShiftDirection.Left:
                    shiftX = -shiftAmount;
                    break;
                case ShiftDirection.Right:
                    shiftX = shiftAmount;
                    break;
                case ShiftDirection.Up:
                    shiftY = -shiftAmount;
                    break;
                case ShiftDirection.Down:
                    shiftY = shiftAmount;
                    break;
            }
            
            // Copy shifted data
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int srcIndex = y * width + x;
                    int newX = x + shiftX;
                    int newY = y + shiftY;
                    
                    if (newX >= 0 && newX < width && newY >= 0 && newY < height && srcIndex < terrains.Length)
                    {
                        int destIndex = newY * width + newX;
                        previewTerrains[destIndex] = terrains[srcIndex];
                    }
                }
            }
        }
        
        private static void DrawAdvancedOperationPreview(int width, int height, float startX, float startZ, float y)
        {
            if (previewTerrains == null || terrainDatabase == null) return;
            
            // Draw current terrain in semi-transparent green
            Color currentColor = new Color(0f, 1f, 0f, 0.3f);
            for (int row = 0; row < height; row++)
            {
                for (int col = 0; col < width; col++)
                {
                    int index = row * width + col;
                    if (index >= selectedTerrain.m_Terrains.Length) continue;
                    
                    string terrainId = selectedTerrain.m_Terrains[index];
                    if (IsEmptyTerrain(terrainId) || terrainId == "MTID_Nothing") continue;
                    
                    float tileX = startX + col * TILE_SIZE;
                    float tileZ = startZ + row * TILE_SIZE;
                    
                    Vector3[] verts = new Vector3[]
                    {
                        new Vector3(tileX, y + 0.02f, tileZ),
                        new Vector3(tileX + TILE_SIZE, y + 0.02f, tileZ),
                        new Vector3(tileX + TILE_SIZE, y + 0.02f, tileZ + TILE_SIZE),
                        new Vector3(tileX, y + 0.02f, tileZ + TILE_SIZE)
                    };
                    
                    Handles.DrawSolidRectangleWithOutline(verts, currentColor, Color.clear);
                }
            }
            
            // Draw preview terrain in semi-transparent blue
            Color previewColor = new Color(0f, 0.5f, 1f, 0.5f);
            for (int row = 0; row < height; row++)
            {
                for (int col = 0; col < width; col++)
                {
                    int index = row * width + col;
                    if (index >= previewTerrains.Length) continue;
                    
                    string terrainId = previewTerrains[index];
                    if (IsEmptyTerrain(terrainId) || terrainId == "MTID_Nothing") continue;
                    
                    float tileX = startX + col * TILE_SIZE;
                    float tileZ = startZ + row * TILE_SIZE;
                    
                    // Get color from terrain database
                    Color tileColor = terrainDatabase.GetTerrainColor(terrainId, previewColor);
                    tileColor.a = 0.6f; // Make semi-transparent
                    
                    Vector3[] verts = new Vector3[]
                    {
                        new Vector3(tileX, y + 0.04f, tileZ),
                        new Vector3(tileX + TILE_SIZE, y + 0.04f, tileZ),
                        new Vector3(tileX + TILE_SIZE, y + 0.04f, tileZ + TILE_SIZE),
                        new Vector3(tileX, y + 0.04f, tileZ + TILE_SIZE)
                    };
                    
                    Handles.DrawSolidRectangleWithOutline(verts, tileColor, Color.blue);
                }
            }
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

        // Reusable GUI styles
        private static GUIStyle s_LabelStyle;
        private static GUIStyle s_LabelStyleSmall;
        private static GUIStyle s_LabelStyleHover;
    }

}
