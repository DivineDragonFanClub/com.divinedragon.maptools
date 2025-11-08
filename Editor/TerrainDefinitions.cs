using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace DivineDragon.MapTools
{
    internal static class TerrainDefinitions
    {
        internal const string TerrainXmlAssetRelativePath = "Assets/Share/Addressables/Patch/Patch3/GameData/Terrain.xml";

        private static readonly Dictionary<string, TerrainType> Lookup = new Dictionary<string, TerrainType>(StringComparer.OrdinalIgnoreCase);
        private static List<TerrainType> cachedTerrains = new List<TerrainType>();
        private static bool isLoaded;
        private static bool watcherRegistered;
        private static bool warnedMissingXml;
        private static string terrainXmlProjectPath;

        internal static string TerrainXmlProjectFullPath
        {
            get
            {
                EnsurePathsInitialized();
                return terrainXmlProjectPath;
            }
        }

        internal static IReadOnlyList<TerrainType> GetAllTerrains()
        {
            EnsureLoaded();
            return cachedTerrains;
        }

        internal static bool HasDefinitions
        {
            get
            {
                EnsureLoaded();
                return cachedTerrains.Count > 0;
            }
        }

        internal static bool TryGetTerrain(string tid, out TerrainType terrain)
        {
            EnsureLoaded();
            return Lookup.TryGetValue(tid ?? string.Empty, out terrain);
        }

        internal static string GetTerrainName(string tid)
        {
            return TryGetTerrain(tid, out TerrainType terrain) ? terrain.name : null;
        }

        internal static Color GetColorOrFallback(string tid)
        {
            return TryGetTerrain(tid, out TerrainType terrain)
                ? terrain.color
                : GenerateFallbackColor(tid);
        }

        internal static void InvalidateCache()
        {
            isLoaded = false;
            Lookup.Clear();
            cachedTerrains = new List<TerrainType>();
            warnedMissingXml = false;
        }

        private static void EnsureLoaded()
        {
            if (isLoaded)
            {
                return;
            }

            EnsurePathsInitialized();

            isLoaded = true;
            Lookup.Clear();
            cachedTerrains = new List<TerrainType>();

            if (string.IsNullOrEmpty(terrainXmlProjectPath) || !File.Exists(terrainXmlProjectPath))
            {
                if (!warnedMissingXml)
                {
                    Debug.LogWarning($"Terrain XML not found at '{terrainXmlProjectPath}'. Extract terrain.xml.bundle so the terrain editor has color/name metadata.");
                    warnedMissingXml = true;
                }
                return;
            }

            warnedMissingXml = false;

            if (!TerrainXmlLoader.TryLoadTerrainTypes(terrainXmlProjectPath, out List<TerrainType> parsedTerrains, out string error))
            {
                Debug.LogWarning($"Failed to load terrain definitions: {error}");
                return;
            }

            cachedTerrains = parsedTerrains ?? new List<TerrainType>();
            foreach (TerrainType terrain in cachedTerrains)
            {
                if (!string.IsNullOrEmpty(terrain.tid))
                {
                    Lookup[terrain.tid] = terrain;
                }
            }
        }

        private static void EnsurePathsInitialized()
        {
            if (!string.IsNullOrEmpty(terrainXmlProjectPath))
            {
                return;
            }

            string assetsPath = Application.dataPath;
            string projectRoot = Path.GetDirectoryName(assetsPath);
            string normalizedRoot = string.IsNullOrEmpty(projectRoot) ? assetsPath : projectRoot.Replace("\\", "/");

            terrainXmlProjectPath = string.IsNullOrEmpty(TerrainXmlAssetRelativePath)
                ? normalizedRoot
                : Path.Combine(normalizedRoot, TerrainXmlAssetRelativePath).Replace("\\", "/");

            if (!watcherRegistered && !string.IsNullOrEmpty(TerrainXmlAssetRelativePath))
            {
                XmlAssetTracker.Register(TerrainXmlAssetRelativePath, OnTerrainXmlChanged);
                watcherRegistered = true;
            }
        }

        private static void OnTerrainXmlChanged()
        {
            InvalidateCache();
            TerrainPaintToolWindow.NotifyTerrainDatabaseChanged();
        }

        private static Color GenerateFallbackColor(string terrainId)
        {
            if (string.IsNullOrEmpty(terrainId))
            {
                return new Color(0.5f, 0.5f, 0.5f, 1f);
            }

            unchecked
            {
                int hash = 17;
                for (int i = 0; i < terrainId.Length; i++)
                {
                    hash = hash * 31 + terrainId[i];
                }

                float hue = (hash & 0x7fffffff) / (float)int.MaxValue;
                float saturation = 0.4f + ((hash >> 8) & 0xFF) / 255f * 0.4f;
                float value = 0.6f + ((hash >> 16) & 0xFF) / 255f * 0.35f;
                Color color = Color.HSVToRGB(Mathf.Repeat(hue, 1f), Mathf.Clamp01(saturation), Mathf.Clamp01(value));
                color.a = 1f;
                return color;
            }
        }
    }
}
