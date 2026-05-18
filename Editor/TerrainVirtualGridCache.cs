using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace DivineDragon.MapTools
{
    internal static class TerrainVirtualGridCache
    {
        private static readonly Dictionary<TerrainAssetAdapter, TerrainVirtualGrid> s_VirtualGrids =
            new Dictionary<TerrainAssetAdapter, TerrainVirtualGrid>();
        private static readonly HashSet<string> s_LoggedInsufficientTiles = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> s_LoggedOverflowTiles = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> EmptyTerrainIds = new HashSet<string>(StringComparer.Ordinal)
        {
            "TID_無し"
        };

        public static bool IsEmptyTerrain(string terrainId)
        {
            if (string.IsNullOrEmpty(terrainId))
            {
                return true;
            }

            return EmptyTerrainIds.Contains(terrainId);
        }

        public static TerrainVirtualGrid GetGrid(TerrainAssetAdapter adapter)
        {
            if (adapter == null || !adapter.IsValid)
            {
                return null;
            }

            if (s_VirtualGrids.TryGetValue(adapter, out TerrainVirtualGrid grid))
            {
                if (grid.Width != adapter.Width || grid.Height != adapter.Height)
                {
                    Invalidate(adapter);
                    grid = null;
                }
            }

            if (grid == null)
            {
                string assetKey = GetAssetKey(adapter);
                grid = TerrainVirtualGrid.Build(adapter, IsEmptyTerrain, assetKey, s_LoggedInsufficientTiles, s_LoggedOverflowTiles);
                s_VirtualGrids[adapter] = grid;
            }

            return grid;
        }

        public static void Invalidate(TerrainAssetAdapter adapter)
        {
            if (adapter == null)
            {
                return;
            }

            s_VirtualGrids.Remove(adapter);
            string assetKey = GetAssetKey(adapter);
            s_LoggedInsufficientTiles.Remove(assetKey);
            s_LoggedOverflowTiles.Remove(assetKey);
        }

        public static void ClearAll()
        {
            s_VirtualGrids.Clear();
            s_LoggedInsufficientTiles.Clear();
            s_LoggedOverflowTiles.Clear();
        }

        private static string GetAssetKey(TerrainAssetAdapter adapter)
        {
            if (adapter?.Asset == null)
            {
                return adapter?.Name ?? string.Empty;
            }

            string path = AssetDatabase.GetAssetPath(adapter.Asset);
            return string.IsNullOrEmpty(path) ? adapter.Name : path;
        }
    }

    internal sealed class TerrainVirtualGrid
    {
        private readonly int[] actualIndices;
        private readonly string[] terrainIds;
        private readonly int tailStartIndex;

        public TerrainAssetAdapter Adapter { get; }
        public int Width { get; }
        public int Height { get; }

        private TerrainVirtualGrid(TerrainAssetAdapter adapter, int[] indices, string[] ids, int tailStart)
        {
            Adapter = adapter;
            Width = adapter.Width;
            Height = adapter.Height;
            actualIndices = indices;
            terrainIds = ids;
            tailStartIndex = tailStart;
        }

        public static TerrainVirtualGrid Build(
            TerrainAssetAdapter adapter,
            Func<string, bool> isEmptyPredicate,
            string assetKey,
            HashSet<string> loggedInsufficient,
            HashSet<string> loggedOverflow)
        {
            int width = adapter.Width;
            int height = adapter.Height;
            int expectedCount = Math.Max(0, width * height);

            int[] actualIndices = new int[expectedCount];
            string[] terrainIds = new string[expectedCount];
            for (int i = 0; i < expectedCount; i++)
            {
                actualIndices[i] = -1;
                terrainIds[i] = string.Empty;
            }

            var raw = adapter.m_Terrains ?? Array.Empty<string>();

            // Detect if this is an official map with padding (1024 tiles, dimensions < 32)
            bool isOfficialPaddedMap = raw.Length == 1024 && (width < 32 || height < 32);

            int fill = 0;
            bool overflow = false;

            if (isOfficialPaddedMap)
            {
                // For official maps, we need to handle the padding structure
                // The data is stored in a 32x32 grid with padding at row ends and bottom
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        // Calculate the position in the 32x32 padded array
                        int paddedIndex = y * 32 + x;

                        if (paddedIndex < raw.Length)
                        {
                            string tid = raw[paddedIndex];
                            // Don't skip TID_無し here - it might be legitimate content
                            // We'll include all tiles within the actual map bounds
                            if (fill < expectedCount)
                            {
                                actualIndices[fill] = paddedIndex;
                                terrainIds[fill] = tid;
                                fill++;
                            }
                        }
                    }
                }
            }
            else
            {
                // For custom maps, use the original logic (skip all empty tiles)
                for (int rawIndex = 0; rawIndex < raw.Length; rawIndex++)
                {
                    string tid = raw[rawIndex];
                    if (isEmptyPredicate(tid))
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
            }

            int tailEmptyCount = Math.Max(0, expectedCount - raw.Length);
            int limit = Math.Min(raw.Length, expectedCount);
            for (int i = limit - 1; i >= 0; i--)
            {
                string tid = raw[i];
                if (!isEmptyPredicate(tid))
                {
                    break;
                }
                tailEmptyCount++;
            }
            tailEmptyCount = Mathf.Clamp(tailEmptyCount, 0, expectedCount);
            int tailStartIndex = expectedCount - tailEmptyCount;
            tailStartIndex = Mathf.Clamp(tailStartIndex, 0, expectedCount);

            if (fill < expectedCount)
            {
                if (loggedInsufficient.Add(assetKey))
                {
                    Debug.LogWarning($"Terrain '{adapter.Name}' only provided {fill} non-empty tiles but expected {expectedCount}. Remaining slots will be empty.");
                }
            }
            else if (overflow)
            {
                if (loggedOverflow.Add(assetKey))
                {
                    Debug.LogWarning($"Terrain '{adapter.Name}' has more non-empty tiles than expected ({expectedCount}). Extra tiles will be ignored in the virtual view.");
                }
            }

            return new TerrainVirtualGrid(adapter, actualIndices, terrainIds, tailStartIndex);
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

        public bool IsTailIndex(int virtualIndex)
        {
            return virtualIndex >= tailStartIndex && virtualIndex < Width * Height;
        }

        public int TailStartIndex => tailStartIndex;

        private int GetVirtualIndex(int x, int y)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height)
            {
                return -1;
            }
            return y * Width + x;
        }
    }
}
