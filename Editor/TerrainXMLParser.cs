using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace DivineDragon.MapTools
{
    public static class TerrainXMLParser
    {
        [MenuItem("Tools/Parse Terrain XML")]
        public static void ParseTerrainXML()
        {
            string terrainXmlPath = TerrainTypeDatabase.TerrainXmlProjectFullPath;

            if (!TerrainXmlLoader.TryLoadTerrainTypes(terrainXmlPath, out List<TerrainType> terrainTypes, out string error))
            {
                Debug.LogError(error);
                EditorUtility.DisplayDialog("Error", $"Failed to parse Terrain XML:\n{error}", "OK");
                return;
            }

            string databasePath = TerrainTypeDatabase.AssetPath;
            TerrainTypeDatabase database = AssetDatabase.LoadAssetAtPath<TerrainTypeDatabase>(databasePath);

            if (database == null)
            {
                database = ScriptableObject.CreateInstance<TerrainTypeDatabase>();
                AssetDatabase.CreateAsset(database, databasePath);
            }

            database.Initialize(terrainTypes);

            Debug.Log($"Successfully parsed {terrainTypes.Count} terrain types from Terrain.xml");

            EditorUtility.DisplayDialog(
                "Success",
                $"Parsed {terrainTypes.Count} terrain types from Terrain.xml\n" +
                $"Database saved to {databasePath}",
                "OK");
        }
        
        [MenuItem("Tools/Validate Terrain Database")]
        public static void ValidateDatabase()
        {
            TerrainTypeDatabase database = TerrainTypeDatabase.Instance;

            if (database == null || database.Count == 0)
            {
                Debug.LogError("TerrainTypeDatabase is empty. Ensure terrain.xml.bundle has been extracted and parsed.");
                return;
            }

            Debug.Log($"Terrain database contains {database.Count} terrain types");
            
            string[] testTids = { "TID_平地", "TID_海", "TID_山", "TID_茂み", "TID_SEA069" };
            foreach (string tid in testTids)
            {
                var terrain = database.GetTerrainType(tid);
                if (terrain != null)
                {
                    Debug.Log($"{tid}: Name='{terrain.name}', Color={terrain.color}");
                }
                else
                {
                    Debug.LogWarning($"{tid}: Not found in database");
                }
            }
        }
    }
}
