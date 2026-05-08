using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DivineDragon.MapTools
{
    public partial class TerrainPaintToolWindow
    {
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
            public float[] tileHeightSamples;
            public bool anyTileHits;
            public bool autoSelection;
            public string colliderPath;
            public string requestedColliderPath;
        }

        private static readonly Dictionary<string, TerrainHeightSettings> terrainHeightSettings = new Dictionary<string, TerrainHeightSettings>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<TerrainAssetAdapter, TerrainHeightCache> terrainHeightCache = new Dictionary<TerrainAssetAdapter, TerrainHeightCache>();
        private static readonly Dictionary<int, List<MeshCollider>> sceneColliderListCache = new Dictionary<int, List<MeshCollider>>();

        private static TerrainHeightSettings GetHeightSettings(TerrainAssetAdapter terrain)
        {
            LoadTerrainHeightPreferencesIfNeeded();

            if (terrain?.Asset == null)
            {
                return new TerrainHeightSettings
                {
                    offset = 0f,
                    mode = TerrainHeightMode.RaycastMesh
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
                    mode = TerrainHeightMode.RaycastMesh
                };
            }

            if (!terrainHeightSettings.TryGetValue(assetPath, out TerrainHeightSettings settings))
            {
                settings = new TerrainHeightSettings
                {
                    offset = 0f,
                    mode = TerrainHeightMode.RaycastMesh
                };
                terrainHeightSettings[assetPath] = settings;
            }

            return settings;
        }

        [Serializable]
        private class TerrainHeightPreferences
        {
            public List<string> paths = new List<string>();
            public List<float> heights = new List<float>();
            public List<int> modes = new List<int>();
            public List<int> autoColliderFlags = new List<int>();
            public List<string> colliderPaths = new List<string>();
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
                            TerrainHeightMode mode = TerrainHeightMode.RaycastMesh;
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

                            TerrainHeightSettings settings = new TerrainHeightSettings
                            {
                                offset = offset,
                                mode = mode,
                                autoSelectCollider = autoSelect,
                                colliderPath = colliderPath
                            };

                            terrainHeightSettings[path] = settings;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Failed to load terrain height preferences: {ex}");
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
            settings.mode = mode;
            terrainHeightSettings[path] = settings;
            SaveTerrainHeightPreferences();
            InvalidateTerrainHeightCache(terrain);
        }

        private static void SetColliderSelectionForTerrain(TerrainAssetAdapter terrain, bool auto, string colliderPath)
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
            settings.autoSelectCollider = auto;
            settings.colliderPath = colliderPath ?? string.Empty;
            terrainHeightSettings[path] = settings;
            SaveTerrainHeightPreferences();
            InvalidateTerrainHeightCache(terrain);
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

        private static void InvalidateTerrainHeightCache(TerrainAssetAdapter terrain)
        {
            if (terrain == null)
            {
                terrainHeightCache.Clear();
                sceneColliderCache.Clear();
                InvalidateSceneColliderLists();
                loggedRaycastFailures.Clear();
                DisposeGridMeshes();
                return;
            }

            terrainHeightCache.Remove(terrain);
            sceneColliderCache.Clear();
            InvalidateSceneColliderLists();
            InvalidateOverlayMesh(terrain);
            InvalidateGridMesh(terrain);

            if (terrain?.Asset != null)
            {
                string assetPath = AssetDatabase.GetAssetPath(terrain.Asset);
                RemoveRaycastFailureEntries(assetPath);
            }
        }

        private static void InvalidateSceneColliderLists()
        {
            sceneColliderListCache.Clear();
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

            MeshCollider result = null;
            if (settings != null && !settings.autoSelectCollider && !string.IsNullOrEmpty(settings.colliderPath))
            {
                result = FindMeshColliderByPath(scene, settings.colliderPath);
                if (result != null)
                {
                    colliderPath = GetTransformPath(result.transform);
                }
                else if (logWarnings)
                {
                    LogRaycastFailure(terrain, GetSceneKey(scene), settings.colliderPath, "Specified collider was not found in the active scene.");
                }
            }

            if (result == null)
            {
                result = FindDefaultMeshCollider(scene);
                colliderPath = result != null ? GetTransformPath(result.transform) : string.Empty;
            }

            if (result != null)
            {
                sceneColliderCache[cacheKey] = result;
            }

            return result;
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

            if (cache == null || cache.tileHeightSamples == null)
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

            if (cache.tileHeightSamples.Length != cache.width * cache.height)
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

        internal static float GetTileWorldHeight(TerrainAssetAdapter terrain, int col, int row)
        {
            TerrainHeightSettings settings = GetHeightSettings(terrain);
            TerrainHeightCache cache = settings.mode == TerrainHeightMode.RaycastMesh ? GetOrBuildTerrainHeightCache(terrain, settings) : null;
            return ResolveTileHeight(cache, settings, col, row);
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
            cache.tileHeightSamples = new float[centerCount];
            for (int i = 0; i < centerCount; i++)
            {
                cache.tileHeightSamples[i] = float.NaN;
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

            // Only sample at tile centers - each tile is flat at its center height
            for (int row = 0; row < cache.height; row++)
            {
                for (int col = 0; col < cache.width; col++)
                {
                    int centerIndex = row * cache.width + col;
                    float centerX = startX + col * TILE_SIZE + halfTile;
                    float centerZ = startZ + row * TILE_SIZE + halfTile;

                    if (TrySampleHeight(collider, topY, bottomY, maxDistance, centerX, centerZ, out float centerHeight))
                    {
                        cache.tileHeightSamples[centerIndex] = centerHeight;
                        cache.anyTileHits = true;
                    }
                }
            }

            string logKey = BuildRaycastLogKey(terrain, cache.sceneKey, cache.colliderPath);
            if (cache.anyTileHits)
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

            if (cache != null && cache.tileHeightSamples != null && cache.width > 0 && cache.height > 0)
            {
                if (col >= 0 && col < cache.width && row >= 0 && row < cache.height)
                {
                    float sample = cache.tileHeightSamples[row * cache.width + col];
                    if (!float.IsNaN(sample))
                    {
                        return sample + settings.offset;
                    }
                }
            }

            return settings.offset;
        }

        private static void FillTileQuad(Vector3[] buffer, float startX, float startZ, int col, int row, TerrainHeightCache cache, TerrainHeightSettings settings, float fallbackY, float yOffset = 0f)
        {
            float baseX = startX + col * TILE_SIZE;
            float baseZ = startZ + row * TILE_SIZE;

            if (settings != null && settings.mode == TerrainHeightMode.RaycastMesh && cache != null)
            {
                // Flat tile: uniform height
                float tileHeight = ResolveTileHeight(cache, settings, col, row) + yOffset;
                FillQuad(buffer, baseX, baseZ, tileHeight, TILE_SIZE, 0f);
                return;
            }

            FillQuad(buffer, baseX, baseZ, fallbackY, TILE_SIZE, yOffset);
        }

        private static MeshCollider FindMeshColliderByPath(Scene scene, string colliderPath)
        {
            if (string.IsNullOrEmpty(colliderPath))
            {
                return null;
            }

            foreach (MeshCollider collider in GetSceneColliders(scene))
            {
                if (collider == null)
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
            foreach (MeshCollider collider in GetSceneColliders(scene))
            {
                if (collider == null)
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
            if (!scene.IsValid())
            {
                return new List<MeshCollider>(0);
            }

            int handle = scene.handle;
            if (sceneColliderListCache.TryGetValue(handle, out List<MeshCollider> cached) && cached != null)
            {
                return cached;
            }

            MeshCollider[] colliders = UnityEngine.Object.FindObjectsOfType<MeshCollider>(true);
            List<MeshCollider> results = new List<MeshCollider>(colliders.Length);
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
            sceneColliderListCache[handle] = results;
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

        private static TerrainHeightCache GetOrCreateCacheForSelectedTerrain()
        {
            TerrainHeightSettings heightSettings = GetHeightSettings(selectedTerrain);
            return heightSettings.mode == TerrainHeightMode.RaycastMesh ? GetOrBuildTerrainHeightCache(selectedTerrain, heightSettings) : null;
        }
    }
}
