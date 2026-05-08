using System;
using System.Collections.Generic;
using UnityEngine;

namespace DivineDragon.MapTools
{
    internal static class TerrainRegionCache
    {
        private static readonly Dictionary<RegionCacheKey, HashSet<Vector2Int>> s_Cache =
            new Dictionary<RegionCacheKey, HashSet<Vector2Int>>();
        private static readonly Queue<Vector2Int> s_FillQueue = new Queue<Vector2Int>();
        private static readonly HashSet<Vector2Int> s_FillVisited = new HashSet<Vector2Int>();
        private static readonly Vector2Int[] s_Directions4 =
        {
            new Vector2Int(0, 1),
            new Vector2Int(1, 0),
            new Vector2Int(0, -1),
            new Vector2Int(-1, 0)
        };

        public static HashSet<Vector2Int> GetSameTerrainRegion(
            TerrainAssetAdapter adapter,
            TerrainVirtualGrid grid,
            Vector2Int startTile,
            int width,
            int height,
            bool useCache)
        {
            if (grid == null || adapter == null)
            {
                return new HashSet<Vector2Int>();
            }

            if (startTile.x < 0 || startTile.y < 0 || startTile.x >= width || startTile.y >= height)
            {
                return new HashSet<Vector2Int>();
            }

            string targetId = grid.GetTerrainId(startTile.x, startTile.y);
            if (TerrainVirtualGridCache.IsEmptyTerrain(targetId))
            {
                return new HashSet<Vector2Int>();
            }

            if (useCache)
            {
                RegionCacheKey key = new RegionCacheKey(adapter, adapter.Asset ?? null, startTile.x, startTile.y, width, height, targetId);
                if (s_Cache.TryGetValue(key, out HashSet<Vector2Int> cachedRegion))
                {
                    return cachedRegion;
                }

                HashSet<Vector2Int> computed = FloodFill(grid, startTile, width, height, targetId);
                s_Cache[key] = computed;
                return computed;
            }

            return FloodFill(grid, startTile, width, height, targetId);
        }

        public static void Invalidate(TerrainAssetAdapter adapter)
        {
            if (adapter == null)
            {
                return;
            }

            if (adapter.Asset == null)
            {
                ClearAll();
                return;
            }

            var keysToRemove = new List<RegionCacheKey>();
            foreach (var key in s_Cache.Keys)
            {
                if (ReferenceEquals(key.Adapter, adapter) || ReferenceEquals(key.TerrainAsset, adapter.Asset))
                {
                    keysToRemove.Add(key);
                }
            }

            foreach (var key in keysToRemove)
            {
                s_Cache.Remove(key);
            }
        }

        public static void ClearAll()
        {
            s_Cache.Clear();
        }

        private static HashSet<Vector2Int> FloodFill(
            TerrainVirtualGrid grid,
            Vector2Int startTile,
            int width,
            int height,
            string targetId)
        {
            HashSet<Vector2Int> region = new HashSet<Vector2Int>();
            s_FillQueue.Clear();
            s_FillVisited.Clear();

            s_FillQueue.Enqueue(startTile);
            s_FillVisited.Add(startTile);

            while (s_FillQueue.Count > 0)
            {
                Vector2Int current = s_FillQueue.Dequeue();
                string currentId = grid.GetTerrainId(current.x, current.y);
                if (TerrainVirtualGridCache.IsEmptyTerrain(currentId) || currentId != targetId)
                {
                    continue;
                }

                region.Add(current);

                foreach (var dir in s_Directions4)
                {
                    Vector2Int neighbor = current + dir;
                    if (neighbor.x < 0 || neighbor.y < 0 || neighbor.x >= width || neighbor.y >= height)
                    {
                        continue;
                    }

                    if (s_FillVisited.Add(neighbor))
                    {
                        s_FillQueue.Enqueue(neighbor);
                    }
                }
            }

            s_FillVisited.Clear();
            s_FillQueue.Clear();
            return region;
        }

        private readonly struct RegionCacheKey : IEquatable<RegionCacheKey>
        {
            public readonly TerrainAssetAdapter Adapter;
            public readonly UnityEngine.Object TerrainAsset;
            public readonly int StartX;
            public readonly int StartY;
            public readonly int Width;
            public readonly int Height;
            public readonly string TerrainId;

            public RegionCacheKey(TerrainAssetAdapter adapter, UnityEngine.Object terrainAsset, int startX, int startY, int width, int height, string terrainId)
            {
                Adapter = adapter;
                TerrainAsset = terrainAsset;
                StartX = startX;
                StartY = startY;
                Width = width;
                Height = height;
                TerrainId = terrainId;
            }

            public bool Equals(RegionCacheKey other)
            {
                return ReferenceEquals(Adapter, other.Adapter)
                    && ReferenceEquals(TerrainAsset, other.TerrainAsset)
                    && StartX == other.StartX
                    && StartY == other.StartY
                    && Width == other.Width
                    && Height == other.Height
                    && string.Equals(TerrainId, other.TerrainId);
            }

            public override bool Equals(object obj)
            {
                return obj is RegionCacheKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = Adapter != null ? Adapter.GetHashCode() : 0;
                    hash = (hash * 397) ^ (TerrainAsset != null ? TerrainAsset.GetHashCode() : 0);
                    hash = (hash * 397) ^ StartX;
                    hash = (hash * 397) ^ StartY;
                    hash = (hash * 397) ^ Width;
                    hash = (hash * 397) ^ Height;
                    hash = (hash * 397) ^ (TerrainId != null ? TerrainId.GetHashCode() : 0);
                    return hash;
                }
            }
        }
    }
}
