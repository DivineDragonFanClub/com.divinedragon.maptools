using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace DivineDragon.MapTools
{
    public partial class TerrainPaintToolWindow
    {
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

        private static readonly Dictionary<TerrainAssetAdapter, OverlayMeshData> overlayMeshCache = new Dictionary<TerrainAssetAdapter, OverlayMeshData>();
        private static readonly Dictionary<TerrainAssetAdapter, GridMeshData> gridMeshCache = new Dictionary<TerrainAssetAdapter, GridMeshData>();
        private static readonly List<TerrainAssetAdapter> meshCacheRemovalBuffer = new List<TerrainAssetAdapter>();
        private static Material overlayMaterial;

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
            if (terrain == null || grid == null)
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
    }
}
