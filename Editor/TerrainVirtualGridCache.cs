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

            if (!s_VirtualGrids.TryGetValue(adapter, out TerrainVirtualGrid grid))
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

        public TerrainAssetAdapter Adapter { get; }
        public int Width { get; }
        public int Height { get; }

        private TerrainVirtualGrid(TerrainAssetAdapter adapter, int[] indices, string[] ids)
        {
            Adapter = adapter;
            Width = adapter.Width;
            Height = adapter.Height;
            actualIndices = indices;
            terrainIds = ids;
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
            int fill = 0;
            bool overflow = false;

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

            return new TerrainVirtualGrid(adapter, actualIndices, terrainIds);
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
}
