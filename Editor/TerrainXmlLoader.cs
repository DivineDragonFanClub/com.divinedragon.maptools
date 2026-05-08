using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;

namespace DivineDragon.MapTools
{
    internal static class TerrainXmlLoader
    {
        private const string TerrainSheetXPath = "//Sheet[@Name='地形']/Data/Param";

        public static bool TryLoadTerrainTypes(string xmlPath, out List<TerrainType> terrainTypes, out string errorMessage)
        {
            terrainTypes = new List<TerrainType>();
            errorMessage = null;

            if (string.IsNullOrEmpty(xmlPath))
            {
                errorMessage = "Terrain XML path is null or empty.";
                return false;
            }

            if (!File.Exists(xmlPath))
            {
                errorMessage = $"Terrain XML not found at '{xmlPath}'.";
                return false;
            }

            try
            {
                XmlDocument doc = new XmlDocument();
                doc.Load(xmlPath);

                XmlNodeList dataNodes = doc.SelectNodes(TerrainSheetXPath);
                if (dataNodes == null)
                {
                    errorMessage = "Terrain XML does not contain any terrain entries.";
                    return false;
                }

                foreach (XmlNode node in dataNodes)
                {
                    string tid = node.Attributes?["Tid"]?.Value;
                    if (string.IsNullOrEmpty(tid))
                    {
                        continue;
                    }

                    string name = node.Attributes?["Name"]?.Value ?? tid;

                    terrainTypes.Add(new TerrainType(
                        tid,
                        name,
                        ParseColorComponent(node, "ColorR", 128),
                        ParseColorComponent(node, "ColorG", 128),
                        ParseColorComponent(node, "ColorB", 128)));
                }

                return true;
            }
            catch (Exception ex)
            {
                errorMessage = $"Exception while parsing Terrain XML: {ex.Message}";
                terrainTypes.Clear();
                return false;
            }
        }

        private static int ParseColorComponent(XmlNode node, string attributeName, int defaultValue)
        {
            string value = node.Attributes?[attributeName]?.Value;
            if (string.IsNullOrEmpty(value))
            {
                return defaultValue;
            }

            return int.TryParse(value, out int parsed) ? parsed : defaultValue;
        }
    }
}
