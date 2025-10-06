using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace DivineDragon.MapTools
{
    public class DisposSceneRenderer
    {
        // Immediate-mode renderer has no persistent GameObjects
        private DisposDocument currentDocument;
        private TerrainAssetAdapter currentTerrain;
        private const float TILE_SIZE = 5.0f; // Match TerrainPaintToolWindow
        private bool showGrid = true;
        private bool showLabels = true;
        private bool showDirections = true;
        private bool showIcons = true;
        private bool showSimplifiedNames = true; // Use simplified unit names instead of full PIDs
        private DisposEntry selectedEntry;
        private DisposEntry hoverEntry; // transient highlight from picker
        private Dictionary<Vector2Int, DisposEntry> tileTopEntries = new Dictionary<Vector2Int, DisposEntry>(); // Track which entry should be on top for each tile
        private bool hasSelectedTile = false;
        private Vector2Int selectedTile;
        private Vector3 worldOffset = Vector3.zero;
        private const float LABEL_SCREEN_OFFSET_Y = -35f; // pixels below sprite
        private const float ICON_TILE_SCALE = 0.9f;      // fraction of tile used for icon size
        private Rect guiOcclusionRect = Rect.zero;        // external UI occlusion (e.g., quick picker)
        private int difficultyFilterMask = (int)DisposFlags.MaskDifficulty; // default: show all

        private float TerrainOriginX => currentTerrain?.OriginX ?? 0f;
        private float TerrainOriginZ => currentTerrain?.OriginZ ?? 0f;
        private float TerrainStartX => TerrainOriginX + worldOffset.x;
        private float TerrainStartZ => TerrainOriginZ + worldOffset.z;
        private int TerrainWidth => currentTerrain?.Width ?? 0;
        private int TerrainHeight => currentTerrain?.Height ?? 0;

        private float GetTileHeight(int tileX, int tileZ)
        {
            if (currentTerrain == null)
            {
                return worldOffset.y;
            }

            return TerrainPaintToolWindow.GetTileWorldHeight(currentTerrain, tileX, tileZ);
        }

        private void GetTileCornerWorldPositions(int tileX, int tileZ, float lift, out Vector3 bl, out Vector3 br, out Vector3 tr, out Vector3 tl)
        {
            float baseX = TerrainStartX + tileX * TILE_SIZE;
            float baseZ = TerrainStartZ + tileZ * TILE_SIZE;

            if (currentTerrain == null)
            {
                float y = worldOffset.y + lift;
                bl = new Vector3(baseX, y, baseZ);
                br = new Vector3(baseX + TILE_SIZE, y, baseZ);
                tr = new Vector3(baseX + TILE_SIZE, y, baseZ + TILE_SIZE);
                tl = new Vector3(baseX, y, baseZ + TILE_SIZE);
                return;
            }

            float h00 = TerrainPaintToolWindow.GetTileCornerWorldHeight(currentTerrain, tileX, tileZ) + lift;
            float h10 = TerrainPaintToolWindow.GetTileCornerWorldHeight(currentTerrain, tileX + 1, tileZ) + lift;
            float h11 = TerrainPaintToolWindow.GetTileCornerWorldHeight(currentTerrain, tileX + 1, tileZ + 1) + lift;
            float h01 = TerrainPaintToolWindow.GetTileCornerWorldHeight(currentTerrain, tileX, tileZ + 1) + lift;

            bl = new Vector3(baseX, h00, baseZ);
            br = new Vector3(baseX + TILE_SIZE, h10, baseZ);
            tr = new Vector3(baseX + TILE_SIZE, h11, baseZ + TILE_SIZE);
            tl = new Vector3(baseX, h01, baseZ + TILE_SIZE);
        }

        private Vector3 GetTileCenterWorld(int tileX, int tileZ, float lift = 0f)
        {
            float worldX = TerrainStartX + tileX * TILE_SIZE + TILE_SIZE * 0.5f;
            float worldZ = TerrainStartZ + tileZ * TILE_SIZE + TILE_SIZE * 0.5f;
            float height = GetTileHeight(tileX, tileZ) + lift;
            return new Vector3(worldX, height, worldZ);
        }

        public DisposEntry SelectedEntry
        {
            get => selectedEntry;
            set
            {
                if (selectedEntry != value)
                {
                    selectedEntry = value;
                    
                    // Remember this entry as the top entry for its tile
                    if (value != null)
                    {
                        var tile = new Vector2Int(value.DisposX, value.DisposY);
                        tileTopEntries[tile] = value;
                    }
                    
                    UpdateSelection();
                }
            }
        }

        public void SetHoverEntry(DisposEntry entry)
        {
            // Transient highlight used when hovering items in the picker
            hoverEntry = entry;
        }
        
        public void Initialize() { }
        
        public void Cleanup() { }
        
        public void RenderDocument(DisposDocument document, TerrainAssetAdapter terrain = null)
        {
            if (document == null) return;
            
            currentDocument = document;
            currentTerrain = terrain?.IsValid == true ? terrain : null;
            Vector3 offset = TerrainPaintToolWindow.GetWorldOffset();
            if (currentTerrain != null)
            {
                offset.y = TerrainPaintToolWindow.GetHeightOffsetForTerrain(currentTerrain);
            }
            worldOffset = offset;
            RefreshUnits();
        }

        // Compute a world-space bounds for a given entry's tile center
        public bool TryGetEntryWorldBounds(DisposEntry entry, out Bounds bounds)
        {
            bounds = new Bounds(Vector3.zero, Vector3.zero);
            if (entry == null) return false;
            float startX = TerrainOriginX;
            float startZ = TerrainOriginZ;
            float worldX = startX + entry.DisposX * TILE_SIZE + TILE_SIZE * 0.5f;
            float worldZ = startZ + entry.DisposY * TILE_SIZE + TILE_SIZE * 0.5f;
            float height = GetTileHeight(entry.DisposX, entry.DisposY);
            Vector3 center = new Vector3(worldX, height, worldZ);
            bounds = new Bounds(center, new Vector3(TILE_SIZE * 2f, TILE_SIZE * 2f, TILE_SIZE * 2f));
            return true;
        }
        
        private void RefreshUnits()
        {
            // Debug: uncomment for troubleshooting
            // Debug.Log($"RefreshUnits called. Document: {currentDocument != null}, Groups: {currentDocument?.Groups?.Count ?? 0}");
            
            if (currentDocument == null)
            {
                // Debug.LogWarning("No document to refresh");
                tileTopEntries.Clear(); // Clear when no document
                return;
            }
            
            // Clean up invalid tile top entries
            var validTiles = new HashSet<Vector2Int>();
            foreach (var group in currentDocument.Groups)
            {
                if (!group.IsVisible) continue;
                foreach (var entry in group.Entries)
                {
                    if (!entry.IsGroupHeader)
                    {
                        validTiles.Add(new Vector2Int(entry.DisposX, entry.DisposY));
                    }
                }
            }
            
            // Remove entries for tiles that no longer exist or have changed
            var tilesToRemove = new List<Vector2Int>();
            foreach (var kvp in tileTopEntries)
            {
                var tile = kvp.Key;
                var topEntry = kvp.Value;
                
                // Check if the entry is still valid and at the same position
                bool isValid = validTiles.Contains(tile) && 
                              topEntry.DisposX == tile.x && 
                              topEntry.DisposY == tile.y;
                
                if (!isValid)
                {
                    tilesToRemove.Add(tile);
                }
            }
            
            foreach (var tile in tilesToRemove)
            {
                tileTopEntries.Remove(tile);
            }
            
            // Immediate-mode: nothing to instantiate
            int entries = 0;
            foreach (var group in currentDocument.Groups)
            {
                if (!group.IsVisible) continue;
                foreach (var entry in group.Entries)
                    if (!entry.IsGroupHeader) entries++;
            }
            // Debug.Log($"RefreshUnits immediate-mode. Entries visible: {entries}");
        }

        // Determine if an entry should be treated as the top entry for its tile this frame.
        // Hovered entries are temporarily treated as tile-top (if visible), otherwise we
        // defer to the persistent tileTopEntries mapping (which prefers the last-selected).
        private bool IsEntryTopForTile(DisposEntry entry)
        {
            if (entry == null) return false;
            // Prioritize hovered entry being drawn on top for preview clarity
            if (hoverEntry == entry && IsEntryVisible(entry)) return true;
            var tile = new Vector2Int(entry.DisposX, entry.DisposY);
            if (tileTopEntries.TryGetValue(tile, out var topEntry) && topEntry == entry) return true;
            return false;
        }
        
        // Removed legacy GameObject-based helpers; renderer is immediate-mode only
        
        public void DrawSceneGUI()
        {
            if (currentDocument == null)
                return;

            Vector3 offset = TerrainPaintToolWindow.GetWorldOffset();
            if (currentTerrain != null)
            {
                offset.y = TerrainPaintToolWindow.GetHeightOffsetForTerrain(currentTerrain);
            }
            worldOffset = offset;
            
            if (showGrid)
                DrawGrid();
            
            var prevZTest = Handles.zTest;
            Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;

            // Track tiles that need a stack badge
            var tilesWithStacks = new HashSet<Vector2Int>();
            var drawnEntries = new HashSet<DisposEntry>();
            var entriesToDrawLater = new List<DisposEntry>();

            // First pass: draw all entries except those that should be on top.
            // If a hovered entry exists on a tile, treat it as tile-top for this frame
            // so its plate/icon render on top even when stacked.
            foreach (var group in currentDocument.Groups)
            {
                if (!group.IsVisible) continue;
                foreach (var entry in group.Entries)
                {
                    if (entry.IsGroupHeader) continue;
                    if (!IsEntryVisible(entry)) continue;
                    if (entry == selectedEntry) continue; // Selected always drawn last
                    
                    // Check if this entry should be on top of its tile
                    var tile = new Vector2Int(entry.DisposX, entry.DisposY);
                    if (IsEntryTopForTile(entry))
                    {
                        entriesToDrawLater.Add(entry);
                        continue;
                    }
                    
                    DrawUnitPlate(entry);
                    DrawUnitGUI(entry);
                    drawnEntries.Add(entry);

                    // Record tiles with multiple entries for badge drawing after all units
                    int count = GetEntriesOnTile(tile.x, tile.y).Count;
                    if (count > 1) tilesWithStacks.Add(tile);
                }
            }

            // Second pass: draw tile-top entries (hovered entries and persistent tops), but not selected
            foreach (var entry in entriesToDrawLater)
            {
                if (entry != selectedEntry)
                {
                    DrawUnitPlate(entry);
                    DrawUnitGUI(entry);
                }
            }

            // Third pass: draw selected entry on top (plate + icon + highlight)
            if (selectedEntry != null)
            {
                bool groupVisible = false;
                foreach (var g in currentDocument.Groups)
                {
                    if (!g.IsVisible) continue;
                    foreach (var e in g.Entries) { if (e == selectedEntry) { groupVisible = true; break; } }
                    if (groupVisible) break;
                }
                if (groupVisible && IsEntryVisible(selectedEntry))
                {
                    DrawUnitPlate(selectedEntry);
                    DrawUnitGUI(selectedEntry);
                    DrawSelectionOutline(selectedEntry);
                    // If hovering the selected entry, also draw a secondary outer outline
                    if (hoverEntry == selectedEntry)
                    {
                        DrawOuterSelectionOutline(selectedEntry);
                    }
                }
            }

            // Hover highlight (outline only) so user can preview a choice in the picker or groups list
            if (hoverEntry != null && hoverEntry != selectedEntry)
            {
                bool groupVisible = false;
                foreach (var g in currentDocument.Groups)
                {
                    if (!g.IsVisible) continue;
                    foreach (var e in g.Entries) { if (e == hoverEntry) { groupVisible = true; break; } }
                    if (groupVisible) break;
                }
                if (groupVisible && IsEntryVisible(hoverEntry))
                {
                    DrawSelectionOutline(hoverEntry);
                    // If hovering a different unit on the same selected tile, draw an outer ring for double highlight
                    if (selectedEntry != null &&
                        selectedEntry.DisposX == hoverEntry.DisposX &&
                        selectedEntry.DisposY == hoverEntry.DisposY)
                    {
                        DrawOuterSelectionOutline(hoverEntry);
                    }
                }
            }

            // Draw all stack badges on top of icons
            foreach (var tile in tilesWithStacks)
            {
                int count = GetEntriesOnTile(tile.x, tile.y).Count;
                if (count > 1) DrawStackBadge(tile, count);
            }

            Handles.zTest = prevZTest;
        }
        
        private void DrawGrid()
        {
            if (currentTerrain == null)
                return;
                
            int width = TerrainWidth;
            int height = TerrainHeight;
            float startX = TerrainStartX;
            float startZ = TerrainStartZ;
            Color gridColor = new Color(1f, 1f, 1f, 0.3f);
            Handles.color = gridColor;

            if (width > 0 && height > 0)
            {
                float lift = 0.01f;

                for (int row = 0; row <= height; row++)
                {
                    float z = startZ + row * TILE_SIZE;
                    for (int col = 0; col < width; col++)
                    {
                        float x0 = startX + col * TILE_SIZE;
                        float x1 = startX + (col + 1) * TILE_SIZE;
                        float hStart = TerrainPaintToolWindow.GetTileCornerWorldHeight(currentTerrain, col, row) + lift;
                        float hEnd = TerrainPaintToolWindow.GetTileCornerWorldHeight(currentTerrain, col + 1, row) + lift;
                        Vector3 start = new Vector3(x0, hStart, z);
                        Vector3 end = new Vector3(x1, hEnd, z);
                        Handles.DrawLine(start, end, 1f);
                    }
                }

                for (int col = 0; col <= width; col++)
                {
                    float x = startX + col * TILE_SIZE;
                    for (int row = 0; row < height; row++)
                    {
                        float z0 = startZ + row * TILE_SIZE;
                        float z1 = startZ + (row + 1) * TILE_SIZE;
                        float hStart = TerrainPaintToolWindow.GetTileCornerWorldHeight(currentTerrain, col, row) + lift;
                        float hEnd = TerrainPaintToolWindow.GetTileCornerWorldHeight(currentTerrain, col, row + 1) + lift;
                        Vector3 start = new Vector3(x, hStart, z0);
                        Vector3 end = new Vector3(x, hEnd, z1);
                        Handles.DrawLine(start, end, 1f);
                    }
                }
            }

            if (hasSelectedTile)
            {
                float tileHeight = GetTileHeight(selectedTile.x, selectedTile.y);
                float tileX = startX + selectedTile.x * TILE_SIZE;
                float tileZ = startZ + selectedTile.y * TILE_SIZE;
                Vector2 p0 = HandleUtility.WorldToGUIPoint(new Vector3(tileX, tileHeight, tileZ));
                Vector2 p1 = HandleUtility.WorldToGUIPoint(new Vector3(tileX + TILE_SIZE, tileHeight, tileZ));
                Vector2 p2 = HandleUtility.WorldToGUIPoint(new Vector3(tileX + TILE_SIZE, tileHeight, tileZ + TILE_SIZE));
                Vector2 p3 = HandleUtility.WorldToGUIPoint(new Vector3(tileX, tileHeight, tileZ + TILE_SIZE));
                float minX = Mathf.Min(Mathf.Min(p0.x, p1.x), Mathf.Min(p2.x, p3.x));
                float maxX = Mathf.Max(Mathf.Max(p0.x, p1.x), Mathf.Max(p2.x, p3.x));
                float minY = Mathf.Min(Mathf.Min(p0.y, p1.y), Mathf.Min(p2.y, p3.y));
                float maxY = Mathf.Max(Mathf.Max(p0.y, p1.y), Mathf.Max(p2.y, p3.y));
                Rect r = new Rect(minX, minY, maxX - minX, maxY - minY);
                Handles.BeginGUI();
                Color c = new Color(0.2f, 0.85f, 0.95f, 0.9f);
                EditorGUI.DrawRect(new Rect(r.xMin, r.yMin, r.width, 2f), c);
                EditorGUI.DrawRect(new Rect(r.xMin, r.yMax - 2f, r.width, 2f), c);
                EditorGUI.DrawRect(new Rect(r.xMin, r.yMin, 2f, r.height), c);
                EditorGUI.DrawRect(new Rect(r.xMax - 2f, r.yMin, 2f, r.height), c);
                Handles.EndGUI();
            }
        }
        
        private void DrawUnitGUI(DisposEntry entry)
        {
            float startX = TerrainOriginX;
            float startZ = TerrainOriginZ;
            float worldX = startX + entry.DisposX * TILE_SIZE + TILE_SIZE * 0.5f;
            float worldZ = startZ + entry.DisposY * TILE_SIZE + TILE_SIZE * 0.5f;
            float tileHeight = GetTileHeight(entry.DisposX, entry.DisposY);
            Vector3 worldPos = new Vector3(worldX, tileHeight + 0.02f, worldZ);

            // Screen-space icon overlay sized to tile
            if (showIcons)
            {
                Texture2D icon = DisposDataLoader.Instance.GetUnitIcon(entry);
                if (icon != null)
                {
                    float tileX = startX + entry.DisposX * TILE_SIZE;
                    float tileZ = startZ + entry.DisposY * TILE_SIZE;
                    Vector3 blW = new Vector3(tileX, worldPos.y, tileZ);
                    Vector3 brW = new Vector3(tileX + TILE_SIZE, worldPos.y, tileZ);
                    Vector3 tlW = new Vector3(tileX, worldPos.y, tileZ + TILE_SIZE);

                    Vector2 bl = HandleUtility.WorldToGUIPoint(blW);
                    Vector2 br = HandleUtility.WorldToGUIPoint(brW);
                    Vector2 tl = HandleUtility.WorldToGUIPoint(tlW);

                    float tileWidthPx = (br - bl).magnitude;
                    float tileHeightPx = (tl - bl).magnitude;
                    float size = Mathf.Min(tileWidthPx, tileHeightPx) * ICON_TILE_SCALE;

                    Vector2 center = HandleUtility.WorldToGUIPoint(worldPos);
                    Rect iconRect = new Rect(center.x - size * 0.5f, center.y - size * 0.5f, size, size);

                    Handles.BeginGUI();
                    GUI.DrawTexture(iconRect, icon, ScaleMode.ScaleToFit, true);
                    Handles.EndGUI();
                }
            }

            // No visible handle; dragging is handled by DisposToolWindow input over tiles/icons

            if (showDirections && entry.Direction >= 0 && entry.Direction <= 8)
            {
                DrawDirectionArrow(worldPos, entry.Direction);
            }
            
            // Only show labels for selected units to reduce clutter
            bool shouldDrawLabel = showLabels && entry == selectedEntry;
            if (shouldDrawLabel)
            {
                // Position label at bottom of tile
                float tileX = startX + entry.DisposX * TILE_SIZE;
                float tileZ = startZ + entry.DisposY * TILE_SIZE;
                
                string label = showSimplifiedNames 
                    ? GetSimplifiedUnitName(entry) 
                    : DisposDataLoader.Instance.GetUnitDisplayName(entry);
                
                GUIStyle style = new GUIStyle(GUI.skin.label);
                style.alignment = TextAnchor.MiddleCenter;
                style.normal.textColor = Color.white;
                style.fontSize = 10;
                
                Color bgColor = DisposDataLoader.Instance.GetForceColor(entry.Force);
                bgColor.a = 0.8f;
                
                Handles.BeginGUI();
                
                // Get screen coordinates of the bottom edge of the tile
                Vector3 bottomLeft = new Vector3(tileX, worldPos.y, tileZ);
                Vector3 bottomRight = new Vector3(tileX + TILE_SIZE, worldPos.y, tileZ);
                Vector2 blScreen = HandleUtility.WorldToGUIPoint(bottomLeft);
                Vector2 brScreen = HandleUtility.WorldToGUIPoint(bottomRight);
                
                // Calculate label position at bottom of tile
                float labelX = (blScreen.x + brScreen.x) * 0.5f;
                float labelY = blScreen.y - 10f; // Move label up a bit from bottom edge
                Vector2 screenPos = new Vector2(labelX, labelY);
                
                Vector2 labelSize = style.CalcSize(new GUIContent(label));
                float bgHeight = 18f; // Fixed height for subtitle-like appearance
                float tileWidth = Mathf.Abs(brScreen.x - blScreen.x);
                
                // Background rect spans width of tile
                Rect bgRect = new Rect(
                    blScreen.x, 
                    screenPos.y - bgHeight * 0.5f, 
                    tileWidth, 
                    bgHeight
                );

                // Avoid overlapping with external UI (quick picker)
                if (guiOcclusionRect.width > 0 && guiOcclusionRect.Overlaps(bgRect))
                {
                    Handles.EndGUI();
                    goto SkipLabel; // skip drawing this label
                }
                
                EditorGUI.DrawRect(bgRect, bgColor);
                
                // Center text within the tile width
                Rect labelRect = new Rect(
                    screenPos.x - labelSize.x * 0.5f, 
                    screenPos.y - labelSize.y * 0.5f, 
                    labelSize.x, 
                    labelSize.y
                );
                
                GUI.Label(labelRect, label, style);
                
                Handles.EndGUI();
            SkipLabel: ;
            }
        }

        private void DrawSelectionOutline(DisposEntry entry)
        {
            if (currentTerrain == null || entry == null) return;
            float startX = TerrainStartX;
            float startZ = TerrainStartZ;
            float y = GetTileHeight(entry.DisposX, entry.DisposY) + 0.02f;

            float tileX = startX + entry.DisposX * TILE_SIZE;
            float tileZ = startZ + entry.DisposY * TILE_SIZE;

            Vector2 p0 = HandleUtility.WorldToGUIPoint(new Vector3(tileX, y, tileZ));
            Vector2 p1 = HandleUtility.WorldToGUIPoint(new Vector3(tileX + TILE_SIZE, y, tileZ));
            Vector2 p2 = HandleUtility.WorldToGUIPoint(new Vector3(tileX + TILE_SIZE, y, tileZ + TILE_SIZE));
            Vector2 p3 = HandleUtility.WorldToGUIPoint(new Vector3(tileX, y, tileZ + TILE_SIZE));

            float minX = Mathf.Min(Mathf.Min(p0.x, p1.x), Mathf.Min(p2.x, p3.x));
            float maxX = Mathf.Max(Mathf.Max(p0.x, p1.x), Mathf.Max(p2.x, p3.x));
            float minY = Mathf.Min(Mathf.Min(p0.y, p1.y), Mathf.Min(p2.y, p3.y));
            float maxY = Mathf.Max(Mathf.Max(p0.y, p1.y), Mathf.Max(p2.y, p3.y));
            Rect r = new Rect(minX, minY, maxX - minX, maxY - minY);

            Handles.BeginGUI();
            Color c = new Color(1f, 0.9f, 0.2f, 0.95f);
            DrawBorder(r, c, 2f);
            Handles.EndGUI();
        }

        // Draw an extra, slightly larger outline to indicate hover-on-selected state
        private void DrawOuterSelectionOutline(DisposEntry entry)
        {
            if (currentTerrain == null || entry == null) return;
            float startX = TerrainStartX;
            float startZ = TerrainStartZ;
            float y = GetTileHeight(entry.DisposX, entry.DisposY) + 0.02f;

            float tileX = startX + entry.DisposX * TILE_SIZE;
            float tileZ = startZ + entry.DisposY * TILE_SIZE;

            Vector2 p0 = HandleUtility.WorldToGUIPoint(new Vector3(tileX, y, tileZ));
            Vector2 p1 = HandleUtility.WorldToGUIPoint(new Vector3(tileX + TILE_SIZE, y, tileZ));
            Vector2 p2 = HandleUtility.WorldToGUIPoint(new Vector3(tileX + TILE_SIZE, y, tileZ + TILE_SIZE));
            Vector2 p3 = HandleUtility.WorldToGUIPoint(new Vector3(tileX, y, tileZ + TILE_SIZE));

            float minX = Mathf.Min(Mathf.Min(p0.x, p1.x), Mathf.Min(p2.x, p3.x));
            float maxX = Mathf.Max(Mathf.Max(p0.x, p1.x), Mathf.Max(p2.x, p3.x));
            float minY = Mathf.Min(Mathf.Min(p0.y, p1.y), Mathf.Min(p2.y, p3.y));
            float maxY = Mathf.Max(Mathf.Max(p0.y, p1.y), Mathf.Max(p2.y, p3.y));
            Rect r = new Rect(minX, minY, maxX - minX, maxY - minY);

            // Expand outward a few pixels so it appears as a second ring
            float pad = 4f;
            Rect outer = new Rect(r.xMin - pad, r.yMin - pad, r.width + pad * 2f, r.height + pad * 2f);

            Handles.BeginGUI();
            Color c = new Color(1f, 0.95f, 0.4f, 0.9f);
            DrawBorder(outer, c, 2f);
            Handles.EndGUI();
        }

        private void DrawBorder(Rect r, Color c, float thickness)
        {
            // Top
            EditorGUI.DrawRect(new Rect(r.xMin, r.yMin, r.width, thickness), c);
            // Bottom
            EditorGUI.DrawRect(new Rect(r.xMin, r.yMax - thickness, r.width, thickness), c);
            // Left
            EditorGUI.DrawRect(new Rect(r.xMin, r.yMin, thickness, r.height), c);
            // Right
            EditorGUI.DrawRect(new Rect(r.xMax - thickness, r.yMin, thickness, r.height), c);
        }

        public void SetGuiOcclusionRect(Rect r)
        {
            guiOcclusionRect = r;
        }

        public void SetSelectedTile(Vector2Int? tile)
        {
            if (tile.HasValue)
            {
                selectedTile = tile.Value;
                hasSelectedTile = true;
            }
            else
            {
                hasSelectedTile = false;
            }
            SceneView.RepaintAll();
        }

        private bool IsEntryVisible(DisposEntry entry)
        {
            int diff = entry.Flag & (int)DisposFlags.MaskDifficulty;
            // If no difficulty flags set, treat as visible in all
            if (diff == 0) return true;
            return (diff & difficultyFilterMask) != 0;
        }

        public void SetDifficultyFilter(bool showNormal, bool showHard, bool showLunatic)
        {
            int mask = 0;
            if (showNormal) mask |= (int)DisposFlags.Normal;
            if (showHard) mask |= (int)DisposFlags.Hard;
            if (showLunatic) mask |= (int)DisposFlags.Lunatic;
            difficultyFilterMask = mask;
            SceneView.RepaintAll();
        }

        public List<DisposEntry> GetEntriesOnTile(int gridX, int gridY)
        {
            var list = new List<DisposEntry>();
            if (currentDocument == null) return list;
            foreach (var group in currentDocument.Groups)
            {
                if (!group.IsVisible) continue;
                foreach (var entry in group.Entries)
                {
                    if (entry.IsGroupHeader) continue;
                    if (!IsEntryVisible(entry)) continue;
                    if (entry.DisposX == gridX && entry.DisposY == gridY)
                        list.Add(entry);
                }
            }
            return list;
        }

        public bool TryGetTileFromWorld(Vector3 worldPos, out Vector2Int tile)
        {
            tile = new Vector2Int(-1, -1);
            if (currentTerrain == null) return false;
            float startX = TerrainStartX;
            float startZ = TerrainStartZ;
            int gridX = Mathf.FloorToInt((worldPos.x - startX) / TILE_SIZE);
            int gridY = Mathf.FloorToInt((worldPos.z - startZ) / TILE_SIZE);
            if (gridX < 0 || gridY < 0 || gridX >= TerrainWidth || gridY >= TerrainHeight)
                return false;
            tile = new Vector2Int(gridX, gridY);
            return true;
        }

        public List<DisposEntry> GetEntriesAtScreenPosition(Vector2 mouseGui)
        {
            var result = new List<DisposEntry>();
            if (currentTerrain == null || currentDocument == null) return result;

            foreach (var group in currentDocument.Groups)
            {
                if (!group.IsVisible) continue;
                foreach (var entry in group.Entries)
                {
                    if (entry.IsGroupHeader) continue;
                    if (!IsEntryVisible(entry)) continue;

                    GetTileCornerWorldPositions(entry.DisposX, entry.DisposY, 0.02f, out Vector3 blW, out Vector3 brW, out Vector3 trW, out Vector3 tlW);
                    Vector2 p0 = HandleUtility.WorldToGUIPoint(blW);
                    Vector2 p1 = HandleUtility.WorldToGUIPoint(brW);
                    Vector2 p2 = HandleUtility.WorldToGUIPoint(trW);
                    Vector2 p3 = HandleUtility.WorldToGUIPoint(tlW);
                    float minX = Mathf.Min(Mathf.Min(p0.x, p1.x), Mathf.Min(p2.x, p3.x));
                    float maxX = Mathf.Max(Mathf.Max(p0.x, p1.x), Mathf.Max(p2.x, p3.x));
                    float minY = Mathf.Min(Mathf.Min(p0.y, p1.y), Mathf.Min(p2.y, p3.y));
                    float maxY = Mathf.Max(Mathf.Max(p0.y, p1.y), Mathf.Max(p2.y, p3.y));
                    Rect r = new Rect(minX, minY, maxX - minX, maxY - minY);
                    if (r.Contains(mouseGui)) result.Add(entry);
                }
            }
            return result;
        }

        private void DrawStackBadge(Vector2Int tile, int count)
        {
            if (currentTerrain == null || count <= 1) return;
            // Compute the same icon rect used by unit icons for this tile
            GetTileCornerWorldPositions(tile.x, tile.y, 0.02f, out Vector3 blW, out Vector3 brW, out Vector3 trW, out Vector3 tlW);
            Vector2 bl = HandleUtility.WorldToGUIPoint(blW);
            Vector2 br = HandleUtility.WorldToGUIPoint(brW);
            Vector2 tl = HandleUtility.WorldToGUIPoint(tlW);
            float tileWidthPx = (br - bl).magnitude;
            float tileHeightPx = (tl - bl).magnitude;
            float sizePx = Mathf.Min(tileWidthPx, tileHeightPx) * ICON_TILE_SCALE;
            Vector3 centerWorld = (blW + brW + trW + tlW) * 0.25f;
            Vector2 center = HandleUtility.WorldToGUIPoint(centerWorld);
            Rect iconRect = new Rect(center.x - sizePx * 0.5f, center.y - sizePx * 0.5f, sizePx, sizePx);

            string text = "x" + count;
            GUIStyle style = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white },
                fontSize = 11,
                fontStyle = FontStyle.Bold
            };

            Vector2 badgeSize = style.CalcSize(new GUIContent(text)) + new Vector2(6f, 4f);

            // Anchor inside the icon rect at top-right with a small inset
            float inset = 3f;
            Rect r = new Rect(
                iconRect.xMax - badgeSize.x - inset,
                iconRect.yMin + inset,
                badgeSize.x,
                badgeSize.y
            );

            // Avoid overlapping with external UI (quick picker) — if picker overlaps icon area, hide badge
            if (guiOcclusionRect.width > 0 && guiOcclusionRect.Overlaps(r))
                return;

            Handles.BeginGUI();
            bool hover = r.Contains(Event.current.mousePosition);
            Color bg = hover ? new Color(0.25f, 0.45f, 0.85f, 0.75f) : new Color(0, 0, 0, 0.55f);
            EditorGUI.DrawRect(r, bg);
            if (hover)
            {
                // Light border and pointer cursor when hovering
                DrawBorder(r, new Color(1f, 1f, 1f, 0.8f), 1f);
                EditorGUIUtility.AddCursorRect(r, MouseCursor.Link);
            }
            GUI.Label(r, text, style);
            Handles.EndGUI();
        }

        public Rect GetStackBadgeRect(Vector2Int tile, int count = 0)
        {
            if (currentTerrain == null) return Rect.zero;
            GetTileCornerWorldPositions(tile.x, tile.y, 0.02f, out Vector3 blW, out Vector3 brW, out Vector3 trW, out Vector3 tlW);

            // Recompute icon rect for hit testing
            Vector2 bl = HandleUtility.WorldToGUIPoint(blW);
            Vector2 br = HandleUtility.WorldToGUIPoint(brW);
            Vector2 tl = HandleUtility.WorldToGUIPoint(tlW);
            float tileWidthPx = (br - bl).magnitude;
            float tileHeightPx = (tl - bl).magnitude;
            float sizePx = Mathf.Min(tileWidthPx, tileHeightPx) * ICON_TILE_SCALE;
            Vector3 centerWorld = (blW + brW + trW + tlW) * 0.25f;
            Vector2 center = HandleUtility.WorldToGUIPoint(centerWorld);
            Rect iconRect = new Rect(center.x - sizePx * 0.5f, center.y - sizePx * 0.5f, sizePx, sizePx);

            string text = "x" + (count > 0 ? count : 99);
            GUIStyle style = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white },
                fontSize = 11,
                fontStyle = FontStyle.Bold
            };
            Vector2 badgeSize = style.CalcSize(new GUIContent(text)) + new Vector2(6f, 4f);
            float inset = 3f;
            return new Rect(
                iconRect.xMax - badgeSize.x - inset,
                iconRect.yMin + inset,
                badgeSize.x,
                badgeSize.y
            );
        }

        public DisposEntry GetTopEntryOnTile(Vector2Int tile)
        {
            // If user has explicitly chosen a top entry for this tile, prefer it
            if (tileTopEntries.TryGetValue(tile, out var entry))
                return entry;

            // Otherwise, infer current rendered top as the last visible entry on this tile
            if (currentDocument == null) return null;
            DisposEntry last = null;
            foreach (var group in currentDocument.Groups)
            {
                if (!group.IsVisible) continue;
                foreach (var e in group.Entries)
                {
                    if (e.IsGroupHeader) continue;
                    if (!IsEntryVisible(e)) continue;
                    if (e.DisposX == tile.x && e.DisposY == tile.y)
                    {
                        last = e; // later entries draw over earlier ones
                    }
                }
            }
            return last;
        }

        public Rect GetTileScreenRect(Vector2Int tile)
        {
            float startX = TerrainStartX;
            float startZ = TerrainStartZ;
            GetTileCornerWorldPositions(tile.x, tile.y, 0.02f, out Vector3 blW, out Vector3 brW, out Vector3 trW, out Vector3 tlW);
            Vector2 p0 = HandleUtility.WorldToGUIPoint(blW);
            Vector2 p1 = HandleUtility.WorldToGUIPoint(brW);
            Vector2 p2 = HandleUtility.WorldToGUIPoint(trW);
            Vector2 p3 = HandleUtility.WorldToGUIPoint(tlW);
            float minX = Mathf.Min(Mathf.Min(p0.x, p1.x), Mathf.Min(p2.x, p3.x));
            float maxX = Mathf.Max(Mathf.Max(p0.x, p1.x), Mathf.Max(p2.x, p3.x));
            float minY = Mathf.Min(Mathf.Min(p0.y, p1.y), Mathf.Min(p2.y, p3.y));
            float maxY = Mathf.Max(Mathf.Max(p0.y, p1.y), Mathf.Max(p2.y, p3.y));
            return new Rect(minX, minY, maxX - minX, maxY - minY);
        }
        
        private void DrawDirectionArrow(Vector3 position, int direction)
        {
            float arrowLength = TILE_SIZE * 0.3f;
            Vector3 arrowDir = GetDirectionVector(direction);
            Vector3 arrowStart = position + Vector3.up * 0.1f;
            Vector3 arrowEnd = arrowStart + arrowDir * arrowLength;
            
            Handles.color = Color.yellow;
            Handles.DrawLine(arrowStart, arrowEnd, 2f);
            
            Vector3 arrowRight = Vector3.Cross(arrowDir, Vector3.up) * 0.1f;
            Vector3 arrowBack = -arrowDir * 0.1f;
            Vector3[] arrowHead = new Vector3[]
            {
                arrowEnd,
                arrowEnd + arrowBack + arrowRight,
                arrowEnd + arrowBack - arrowRight
            };
            Handles.DrawAAConvexPolygon(arrowHead);
        }
        
        private Vector3 GetDirectionVector(int direction)
        {
            switch (direction)
            {
                case 0: return Vector3.forward;
                case 1: return new Vector3(1, 0, 1).normalized;
                case 2: return Vector3.right;
                case 3: return new Vector3(1, 0, -1).normalized;
                case 4: return Vector3.back;
                case 5: return new Vector3(-1, 0, -1).normalized;
                case 6: return Vector3.left;
                case 7: return new Vector3(-1, 0, 1).normalized;
                case 8: return Vector3.forward;
                default: return Vector3.forward;
            }
        }
        
        
        
        public bool TryGetStackBadgeClick(Vector2 mouseGui, out Vector2Int tile, out List<DisposEntry> entries)
        {
            tile = Vector2Int.zero;
            entries = null;
            
            if (currentDocument == null || currentTerrain == null) 
                return false;
            
            // Check each tile that has multiple units
            var checkedTiles = new HashSet<Vector2Int>();
            
            foreach (var group in currentDocument.Groups)
            {
                if (!group.IsVisible) continue;
                foreach (var entry in group.Entries)
                {
                    if (entry.IsGroupHeader || !IsEntryVisible(entry)) continue;
                    
                    var tilePos = new Vector2Int(entry.DisposX, entry.DisposY);
                    if (checkedTiles.Contains(tilePos)) continue;
                    
                    var tileEntries = GetEntriesOnTile(tilePos.x, tilePos.y);
                    if (tileEntries.Count > 1)
                    {
                        Rect badgeRect = GetStackBadgeRect(tilePos, tileEntries.Count);
                        if (badgeRect.Contains(mouseGui))
                        {
                            tile = tilePos;
                            entries = tileEntries;
                            return true;
                        }
                    }
                    
                    checkedTiles.Add(tilePos);
                }
            }
            
            return false;
        }

        public DisposEntry GetEntryAtPosition(Vector3 worldPos)
        {
            if (currentTerrain == null)
                return null;
                
            float startX = TerrainStartX;
            float startZ = TerrainStartZ;
            
            int gridX = Mathf.FloorToInt((worldPos.x - startX) / TILE_SIZE);
            int gridY = Mathf.FloorToInt((worldPos.z - startZ) / TILE_SIZE);
            
            foreach (var group in currentDocument.Groups)
            {
                if (!group.IsVisible)
                    continue;
                
                foreach (var entry in group.Entries)
                {
                    if (!entry.IsGroupHeader && 
                        entry.DisposX == gridX && 
                        entry.DisposY == gridY)
                    {
                        return entry;
                    }
                }
            }
            
            return null;
        }

        // Screen-space hit test against a unit's tile rectangle (icon/plate area)
        public DisposEntry GetEntryAtScreenPosition(Vector2 mouseGui)
        {
            if (currentTerrain == null || currentDocument == null)
                return null;

            float startX = TerrainStartX;
            float startZ = TerrainStartZ;

            DisposEntry best = null;
            float bestDist = float.MaxValue;

            foreach (var group in currentDocument.Groups)
            {
                if (!group.IsVisible) continue;
                foreach (var entry in group.Entries)
                {
                    if (entry.IsGroupHeader) continue;

                    float tileX = startX + entry.DisposX * TILE_SIZE;
                    float tileZ = startZ + entry.DisposY * TILE_SIZE;
                    float tileHeight = GetTileHeight(entry.DisposX, entry.DisposY) + 0.02f;
                    Vector2 p0 = HandleUtility.WorldToGUIPoint(new Vector3(tileX, tileHeight, tileZ));
                    Vector2 p1 = HandleUtility.WorldToGUIPoint(new Vector3(tileX + TILE_SIZE, tileHeight, tileZ));
                    Vector2 p2 = HandleUtility.WorldToGUIPoint(new Vector3(tileX + TILE_SIZE, tileHeight, tileZ + TILE_SIZE));
                    Vector2 p3 = HandleUtility.WorldToGUIPoint(new Vector3(tileX, tileHeight, tileZ + TILE_SIZE));
                    float minX = Mathf.Min(Mathf.Min(p0.x, p1.x), Mathf.Min(p2.x, p3.x));
                    float maxX = Mathf.Max(Mathf.Max(p0.x, p1.x), Mathf.Max(p2.x, p3.x));
                    float minY = Mathf.Min(Mathf.Min(p0.y, p1.y), Mathf.Min(p2.y, p3.y));
                    float maxY = Mathf.Max(Mathf.Max(p0.y, p1.y), Mathf.Max(p2.y, p3.y));
                    Rect r = new Rect(minX, minY, maxX - minX, maxY - minY);

                    if (r.Contains(mouseGui))
                    {
                        // Prefer the closest tile center in screen space when overlapping
                        Vector2 center = HandleUtility.WorldToGUIPoint(new Vector3(tileX + TILE_SIZE * 0.5f, tileHeight, tileZ + TILE_SIZE * 0.5f));
                        float d = (center - mouseGui).sqrMagnitude;
                        if (d < bestDist)
                        {
                            bestDist = d;
                            best = entry;
                        }
                    }
                }
            }
            return best;
        }
        
        public void MoveEntry(DisposEntry entry, Vector3 worldPos, DisposUndoProxy undoProxy = null)
        {
            if (entry == null || entry.IsGroupHeader || currentTerrain == null)
                return;
            
            // Record undo before making changes
            if (undoProxy != null)
            {
                undoProxy.RecordUndo("Move Unit");
            }
            
            // Remember old position
            var oldTile = new Vector2Int(entry.DisposX, entry.DisposY);
            
            float startX = TerrainStartX;
            float startZ = TerrainStartZ;
            int newX = Mathf.FloorToInt((worldPos.x - startX) / TILE_SIZE);
            int newY = Mathf.FloorToInt((worldPos.z - startZ) / TILE_SIZE);
            
            // Update position
            entry.DisposX = newX;
            entry.DisposY = newY;
            
            // Update tile top entries if this unit was on top
            if (tileTopEntries.TryGetValue(oldTile, out var topEntry) && topEntry == entry)
            {
                tileTopEntries.Remove(oldTile);
                var newTile = new Vector2Int(newX, newY);
                tileTopEntries[newTile] = entry;
            }
            if (undoProxy != null)
            {
                undoProxy.CapturePostState();
            }
        }

        public void NudgeEntry(DisposEntry entry, int dx, int dy, DisposUndoProxy undoProxy = null)
        {
            if (entry == null || entry.IsGroupHeader || currentTerrain == null)
                return;
                
            var oldTile = new Vector2Int(entry.DisposX, entry.DisposY);
                
            int width = TerrainWidth;
            int height = TerrainHeight;
            int nx = Mathf.Clamp(entry.DisposX + dx, 0, Mathf.Max(0, width - 1));
            int ny = Mathf.Clamp(entry.DisposY + dy, 0, Mathf.Max(0, height - 1));
            if (nx == entry.DisposX && ny == entry.DisposY)
                return;
            
            // Record undo before making changes
            if (undoProxy != null)
            {
                undoProxy.RecordUndo("Nudge Unit");
            }
                
            entry.DisposX = nx;
            entry.DisposY = ny;
            
            // Update tile top entries if this unit was on top
            if (tileTopEntries.TryGetValue(oldTile, out var topEntry) && topEntry == entry)
            {
                tileTopEntries.Remove(oldTile);
                var newTile = new Vector2Int(nx, ny);
                tileTopEntries[newTile] = entry;
            }
            
            SceneView.RepaintAll();
            
            if (undoProxy != null)
            {
                undoProxy.CapturePostState();
            }
        }

        private void DrawUnitPlate(DisposEntry entry)
        {
            if (currentTerrain == null) return;
            float startX = TerrainStartX;
            float startZ = TerrainStartZ;
            GetTileCornerWorldPositions(entry.DisposX, entry.DisposY, 0.02f, out Vector3 bl, out Vector3 br, out Vector3 tr, out Vector3 tl);
            Vector3[] verts = new Vector3[] { bl, br, tr, tl };
            Color c = DisposDataLoader.Instance.GetForceColor(entry.Force);
            Handles.DrawSolidRectangleWithOutline(verts, c, Color.black);
        }
        
        private void UpdateSelection()
        {
            SceneView.RepaintAll();
        }
        
        public void SetShowGrid(bool show)
        {
            showGrid = show;
            SceneView.RepaintAll();
        }
        
        public void SetShowLabels(bool show)
        {
            showLabels = show;
            SceneView.RepaintAll();
        }
        
        public void SetShowDirections(bool show)
        {
            showDirections = show;
            SceneView.RepaintAll();
        }
        
        public void SetShowIcons(bool show)
        {
            showIcons = show;
            SceneView.RepaintAll();
        }
        
        public void SetShowSimplifiedNames(bool show)
        {
            showSimplifiedNames = show;
            SceneView.RepaintAll();
        }
        
        private string GetSimplifiedUnitName(DisposEntry entry)
        {
            string fullName = DisposDataLoader.Instance.GetUnitDisplayName(entry);
            
            // If it starts with PID pattern, extract the last part
            if (entry.Pid != null && entry.Pid.StartsWith("PID_"))
            {
                string[] parts = entry.Pid.Split('_');
                if (parts.Length >= 3)
                {
                    // Return the last part (e.g., "Cadda" from "PID_S069_Cadda")
                    return parts[parts.Length - 1];
                }
            }
            
            // Otherwise return the full name
            return fullName;
        }


        // No fallback sprites or overlay materials needed in immediate-mode
        
        public void SetWorldOffset(Vector3 offset)
        {
            worldOffset = offset;
            RefreshUnits();
            SceneView.RepaintAll();
        }

        public float GetBasePlaneY()
        {
            return worldOffset.y;
        }
    }
}
