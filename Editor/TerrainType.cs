using UnityEngine;

namespace DivineDragon.MapTools
{
    [System.Serializable]
    public class TerrainType
    {
        public string tid;
        public string name;
        public Color color;
        
        public TerrainType(string tid, string name, int r, int g, int b)
        {
            this.tid = tid;
            this.name = name;
            this.color = new Color(r / 255f, g / 255f, b / 255f, 1f);
        }
    }
}