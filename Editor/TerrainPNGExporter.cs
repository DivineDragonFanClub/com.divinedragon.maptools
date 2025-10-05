using System;
using System.IO;
using UnityEngine;
using UnityEditor;

namespace DivineDragon.MapTools
{
    public static class TerrainPNGExporter
    {
        public static void ExportToPNG(
            TerrainAssetAdapter terrain,
            TerrainTypeDatabase database,
            int pixelsPerTile,
            bool includeGrid,
            Color gridColor,
            int gridThickness,
            float colorBrightness,
            string outputPath)
        {
            if (terrain?.IsValid != true)
            {
                EditorUtility.DisplayDialog("Export Error", "No terrain selected or terrain data is null.", "OK");
                return;
            }

            TerrainVirtualGrid grid = TerrainVirtualGridCache.GetGrid(terrain);
            if (grid == null)
            {
                EditorUtility.DisplayDialog("Export Error", "Unable to interpret terrain data for export.", "OK");
                return;
            }

            int width = grid.Width;
            int height = grid.Height;
            
            // Create texture with appropriate size
            int textureWidth = width * pixelsPerTile;
            int textureHeight = height * pixelsPerTile;
            
            if (includeGrid)
            {
                // Add space for grid lines
                textureWidth += (width + 1) * gridThickness;
                textureHeight += (height + 1) * gridThickness;
            }
            
            Texture2D texture = new Texture2D(textureWidth, textureHeight, TextureFormat.RGBA32, false);
            
            // Fill with background color (transparent or a default color)
            Color[] pixels = new Color[textureWidth * textureHeight];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = new Color(0.1f, 0.1f, 0.1f, 1f); // Dark gray background
            }
            texture.SetPixels(pixels);
            
            // Draw terrain tiles
            DrawTerrainTiles(texture, grid, database, pixelsPerTile, includeGrid, gridThickness, colorBrightness);
            
            // Draw grid lines if requested
            if (includeGrid)
            {
                DrawGridLines(texture, width, height, pixelsPerTile, gridColor, gridThickness);
            }
            
            // Apply changes to texture
            texture.Apply();
            
            // Export to PNG
            byte[] pngData = texture.EncodeToPNG();
            
            if (pngData != null)
            {
                try
                {
                    File.WriteAllBytes(outputPath, pngData);
                    EditorUtility.DisplayDialog("Export Successful", 
                        $"Terrain exported successfully to:\n{outputPath}", "OK");
                    
                    // Optionally reveal in finder/explorer
                    EditorUtility.RevealInFinder(outputPath);
                }
                catch (System.Exception e)
                {
                    EditorUtility.DisplayDialog("Export Error", 
                        $"Failed to save PNG:\n{e.Message}", "OK");
                }
            }
            
            // Clean up texture
            UnityEngine.Object.DestroyImmediate(texture);
        }
        
        private static void DrawTerrainTiles(
            Texture2D texture,
            TerrainVirtualGrid grid,
            TerrainTypeDatabase database,
            int pixelsPerTile,
            bool includeGrid,
            int gridThickness,
            float colorBrightness)
        {
            int width = grid.Width;
            int height = grid.Height;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    string terrainId = grid.GetTerrainId(x, y);
                    if (TerrainVirtualGridCache.IsEmptyTerrain(terrainId))
                        continue;
                    
                    // Get color for this terrain type
                    Color tileColor = GetTerrainColor(terrainId, database, colorBrightness);
                    
                    // Calculate pixel position (don't flip Y - keep it as in Unity)
                    int pixelX, pixelY;
                    if (includeGrid)
                    {
                        pixelX = x * (pixelsPerTile + gridThickness) + gridThickness;
                        pixelY = y * (pixelsPerTile + gridThickness) + gridThickness;
                    }
                    else
                    {
                        pixelX = x * pixelsPerTile;
                        pixelY = y * pixelsPerTile;
                    }
                    
                    // Fill the tile area with the color
                    for (int py = 0; py < pixelsPerTile; py++)
                    {
                        for (int px = 0; px < pixelsPerTile; px++)
                        {
                            texture.SetPixel(pixelX + px, pixelY + py, tileColor);
                        }
                    }
                }
            }
        }
        
        private static void DrawGridLines(
            Texture2D texture,
            int gridWidth,
            int gridHeight,
            int pixelsPerTile,
            Color gridColor,
            int gridThickness)
        {
            int textureWidth = texture.width;
            int textureHeight = texture.height;
            
            // Draw horizontal lines
            for (int row = 0; row <= gridHeight; row++)
            {
                int y = row * (pixelsPerTile + gridThickness);
                for (int t = 0; t < gridThickness; t++)
                {
                    for (int x = 0; x < textureWidth; x++)
                    {
                        if (y + t < textureHeight)
                        {
                            texture.SetPixel(x, y + t, gridColor);
                        }
                    }
                }
            }
            
            // Draw vertical lines
            for (int col = 0; col <= gridWidth; col++)
            {
                int x = col * (pixelsPerTile + gridThickness);
                for (int t = 0; t < gridThickness; t++)
                {
                    for (int y = 0; y < textureHeight; y++)
                    {
                        if (x + t < textureWidth)
                        {
                            texture.SetPixel(x + t, y, gridColor);
                        }
                    }
                }
            }
        }
        
        private static Color GetTerrainColor(string terrainId, TerrainTypeDatabase database, float brightness)
        {
            if (database == null)
                return Color.gray;
            
            // Special case for MTID_Nothing
            if (terrainId == "MTID_Nothing")
                return new Color(0.2f, 0.2f, 0.2f, 1f); // Dark gray for empty tiles
            
            // Get color from database
            Color color = database.GetTerrainColor(terrainId, Color.gray);
            
            // Apply brightness adjustment
            color.r = Mathf.Clamp01(color.r * brightness);
            color.g = Mathf.Clamp01(color.g * brightness);
            color.b = Mathf.Clamp01(color.b * brightness);
            color.a = 1f; // Ensure full opacity for export
            
            return color;
        }
        
        public static string GetDefaultExportPath(TerrainAssetAdapter terrain)
        {
            string terrainName = terrain?.Asset != null ? terrain.Asset.name : "terrain";
            string timestamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string fileName = $"{terrainName}_export_{timestamp}.png";

            // Default to project's Assets folder
            string projectPath = Application.dataPath;
            return Path.Combine(projectPath, fileName);
        }
    }
}
