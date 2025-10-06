using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using System.IO;

namespace DivineDragon.MapTools
{
    [CreateAssetMenu(fileName = "TerrainTypeDatabase", menuName = "MapEditor/TerrainTypeDatabase")]
    public class TerrainTypeDatabase : ScriptableObject
    {
        private const string DatabaseGuid = "de9b9da0a76f2450ea1508609dc66d99";
        internal const string TerrainXmlAssetRelativePath = "Assets/Share/Addressables/Patch/Patch3/GameData/Terrain.xml";

        private static string projectRootPath;
        private static string terrainXmlProjectPath;
        private static bool pathsInitialized;
        private static bool watcherRegistered;
        private static bool needsReload = true;

        internal static string TerrainXmlProjectFullPath
        {
            get
            {
                EnsurePathsInitialized();
                return terrainXmlProjectPath;
            }
        }

        private static bool warnedMissingXml;

        [SerializeField]
        private List<TerrainType> terrainTypes = new List<TerrainType>();
        
        private Dictionary<string, TerrainType> tidLookup;
        
        private static TerrainTypeDatabase instance;

        public static string AssetPath
        {
            get
            {
                string path = AssetDatabase.GUIDToAssetPath(DatabaseGuid);
                if (string.IsNullOrEmpty(path))
                {
                    path = "Packages/com.divinedragon.maptools/Editor/TerrainTypeDatabase.asset";
                }
                return path;
            }
        }
        
        public static TerrainTypeDatabase Instance
        {
            get
            {
                EnsurePathsInitialized();
                instance ??= LoadOrCreateInstance();
                instance?.EnsureLoadedFromTerrainXml();
                return instance;
            }
        }

        public void Initialize(List<TerrainType> types, bool markDirty = true)
        {
            terrainTypes = types ?? new List<TerrainType>();
            RebuildLookup();
            if (markDirty)
            {
                EditorUtility.SetDirty(this);
                AssetDatabase.SaveAssets();
            }
            needsReload = false;
        }

        private void RebuildLookup()
        {
            tidLookup = new Dictionary<string, TerrainType>();
            if (terrainTypes == null)
            {
                return;
            }

            foreach (var terrain in terrainTypes)
            {
                if (!string.IsNullOrEmpty(terrain.tid))
                {
                    tidLookup[terrain.tid] = terrain;
                }
            }
        }

        private static TerrainTypeDatabase LoadOrCreateInstance()
        {
            string assetPath = AssetPath;
            TerrainTypeDatabase database = null;

            if (!string.IsNullOrEmpty(assetPath))
            {
                database = AssetDatabase.LoadAssetAtPath<TerrainTypeDatabase>(assetPath);
            }

            if (database != null)
            {
                return database;
            }

            database = CreateInstance<TerrainTypeDatabase>();
            database.hideFlags = HideFlags.HideAndDontSave;
            return database;
        }

        private void EnsureLoadedFromTerrainXml()
        {
            EnsurePathsInitialized();

            if (string.IsNullOrEmpty(terrainXmlProjectPath))
            {
                return;
            }

            if (!needsReload && terrainTypes != null && terrainTypes.Count > 0)
            {
                return;
            }

            if (!File.Exists(terrainXmlProjectPath))
            {
                if (!warnedMissingXml)
                {
                    Debug.LogWarning($"Terrain XML not found at '{terrainXmlProjectPath}'. Extract terrain.xml.bundle via the Chapter Dumper before opening terrain tools.");
                    warnedMissingXml = true;
                }
                needsReload = true;
                return;
            }

            warnedMissingXml = false;

            if (!TerrainXmlLoader.TryLoadTerrainTypes(terrainXmlProjectPath, out List<TerrainType> parsedTerrains, out string error))
            {
                Debug.LogError($"Failed to load terrain definitions: {error}");
                needsReload = true;
                return;
            }

            bool markDirty = AssetDatabase.Contains(this);
            Initialize(parsedTerrains, markDirty);
        }

        private static string ResolveProjectPath(string relativePath)
        {
            EnsurePathsInitialized();

            if (string.IsNullOrEmpty(relativePath))
            {
                return projectRootPath;
            }

            return Path.Combine(projectRootPath, relativePath).Replace("\\", "/");
        }

        private static void EnsurePathsInitialized()
        {
            if (pathsInitialized)
            {
                return;
            }

            string assetsPath = Application.dataPath;
            string root = Path.GetDirectoryName(assetsPath);
            projectRootPath = string.IsNullOrEmpty(root) ? assetsPath : root.Replace("\\", "/");
            terrainXmlProjectPath = string.IsNullOrEmpty(TerrainXmlAssetRelativePath)
                ? projectRootPath
                : Path.Combine(projectRootPath, TerrainXmlAssetRelativePath).Replace("\\", "/");

            pathsInitialized = true;

            if (!watcherRegistered && !string.IsNullOrEmpty(TerrainXmlAssetRelativePath))
            {
                XmlAssetTracker.Register(TerrainXmlAssetRelativePath, OnTerrainXmlChanged);
                watcherRegistered = true;
            }
        }

        public TerrainType GetTerrainType(string tid)
        {
            if (tidLookup == null)
            {
                RebuildLookup();
            }
            
            if (tidLookup.TryGetValue(tid, out TerrainType terrain))
            {
                return terrain;
            }
            
            return null;
        }
        
        public Color GetTerrainColor(string tid, Color defaultColor)
        {
            var terrain = GetTerrainType(tid);
            return terrain != null ? terrain.color : defaultColor;
        }
        
        public string GetTerrainName(string tid)
        {
            var terrain = GetTerrainType(tid);
            return terrain != null ? terrain.name : tid;
        }

        public List<TerrainType> GetAllTerrainTypes()
        {
            return terrainTypes != null ? new List<TerrainType>(terrainTypes) : new List<TerrainType>();
        }

        public int Count => terrainTypes?.Count ?? 0;

        private static void OnTerrainXmlChanged()
        {
            needsReload = true;
            warnedMissingXml = false;

            if (instance != null)
            {
                instance.EnsureLoadedFromTerrainXml();
            }
        }
    }
}
