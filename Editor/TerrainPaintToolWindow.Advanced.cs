using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace DivineDragon.MapTools
{
    public partial class TerrainPaintToolWindow
    {
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
        
    }
}
