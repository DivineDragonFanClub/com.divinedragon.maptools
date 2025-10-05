using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace DivineDragon.MapTools
{
    [CreateAssetMenu(fileName = "TerrainTypeDatabase", menuName = "MapEditor/TerrainTypeDatabase")]
    public class TerrainTypeDatabase : ScriptableObject
    {
        private const string DatabaseGuid = "de9b9da0a76f2450ea1508609dc66d99";

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
                if (instance == null)
                {
                    string path = AssetPath;
                    if (!string.IsNullOrEmpty(path))
                    {
                        instance = AssetDatabase.LoadAssetAtPath<TerrainTypeDatabase>(path);
                    }
                    
                    if (instance == null)
                    {
                        Debug.LogWarning("TerrainTypeDatabase asset not found. Run the terrain parser to generate it.");
                    }
                }
                return instance;
            }
        }
        
        public void Initialize(List<TerrainType> types)
        {
            terrainTypes = types;
            RebuildLookup();
            EditorUtility.SetDirty(this);
            AssetDatabase.SaveAssets();
        }
        
        private void RebuildLookup()
        {
            tidLookup = new Dictionary<string, TerrainType>();
            foreach (var terrain in terrainTypes)
            {
                if (!string.IsNullOrEmpty(terrain.tid))
                {
                    tidLookup[terrain.tid] = terrain;
                }
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
            return new List<TerrainType>(terrainTypes);
        }
        
        public int Count => terrainTypes.Count;
    }
}
