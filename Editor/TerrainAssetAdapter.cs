using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace DivineDragon.MapTools
{
    public sealed class TerrainAssetAdapter
    {
        private static readonly Type mapTerrainType = ResolveMapTerrainType();
        private static readonly FieldInfo widthField = GetField("m_Width");
        private static readonly FieldInfo heightField = GetField("m_Height");
        private static readonly FieldInfo originXField = GetField("m_X");
        private static readonly FieldInfo originZField = GetField("m_Z");
        private static readonly FieldInfo terrainsField = GetField("m_Terrains");
        private static bool hasLoggedMissingType;

        private static Type ResolveMapTerrainType()
        {
            Type type = Type.GetType("Bridge.MapTerrain, Assembly-CSharp");
            if (type != null)
            {
                return type;
            }

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = asm.GetType("Bridge.MapTerrain");
                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        private static FieldInfo GetField(string name)
        {
            return mapTerrainType?.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }

        public static Type MapTerrainType
        {
            get
            {
                if (mapTerrainType == null && !hasLoggedMissingType)
                {
                    hasLoggedMissingType = true;
                    Debug.LogWarning("DivineDragon.MapTools: Bridge.MapTerrain type not found. Terrain tooling will have limited functionality.");
                }

                return mapTerrainType;
            }
        }

        public static TerrainAssetAdapter Load(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
            {
                return null;
            }

            var type = MapTerrainType ?? typeof(ScriptableObject);
            var obj = AssetDatabase.LoadAssetAtPath(assetPath, type) as ScriptableObject;
            return obj != null ? new TerrainAssetAdapter(obj) : null;
        }

        public static TerrainAssetAdapter FromObject(UnityEngine.Object obj)
        {
            return obj is ScriptableObject so ? new TerrainAssetAdapter(so) : null;
        }

        public ScriptableObject Asset { get; }

        public bool IsValid => Asset != null;

        public string Name => Asset != null ? Asset.name : string.Empty;

        public int Width
        {
            get => GetInt(widthField, "m_Width");
            set => SetInt(widthField, "m_Width", value);
        }

        public int Height
        {
            get => GetInt(heightField, "m_Height");
            set => SetInt(heightField, "m_Height", value);
        }

        public float OriginX
        {
            get => GetFloat(originXField, "m_X");
            set => SetFloat(originXField, "m_X", value);
        }

        public float OriginZ
        {
            get => GetFloat(originZField, "m_Z");
            set => SetFloat(originZField, "m_Z", value);
        }

        public string[] Terrains
        {
            get => GetTerrains();
            set => SetTerrains(value);
        }

        public int TerrainCount => Terrains?.Length ?? 0;

        private readonly SerializedObject serialized;

        public TerrainAssetAdapter(ScriptableObject asset)
        {
            Asset = asset;
            serialized = asset != null ? new SerializedObject(asset) : null;
        }

        private int GetInt(FieldInfo field, string propertyName)
        {
            if (Asset == null)
            {
                return 0;
            }

            if (field != null)
            {
                return (int)field.GetValue(Asset);
            }

            if (serialized != null)
            {
                serialized.Update();
                SerializedProperty prop = serialized.FindProperty(propertyName);
                if (prop != null)
                {
                    return prop.intValue;
                }
            }

            return 0;
        }

        private void SetInt(FieldInfo field, string propertyName, int value)
        {
            if (Asset == null)
            {
                return;
            }

            if (field != null)
            {
                field.SetValue(Asset, value);
                return;
            }

            if (serialized != null)
            {
                serialized.Update();
                SerializedProperty prop = serialized.FindProperty(propertyName);
                if (prop != null)
                {
                    prop.intValue = value;
                    serialized.ApplyModifiedProperties();
                }
            }
        }

        private float GetFloat(FieldInfo field, string propertyName)
        {
            if (Asset == null)
            {
                return 0f;
            }

            if (field != null)
            {
                return Convert.ToSingle(field.GetValue(Asset));
            }

            if (serialized != null)
            {
                serialized.Update();
                SerializedProperty prop = serialized.FindProperty(propertyName);
                if (prop != null)
                {
                    return prop.floatValue;
                }
            }

            return 0f;
        }

        private void SetFloat(FieldInfo field, string propertyName, float value)
        {
            if (Asset == null)
            {
                return;
            }

            if (field != null)
            {
                field.SetValue(Asset, value);
                return;
            }

            if (serialized != null)
            {
                serialized.Update();
                SerializedProperty prop = serialized.FindProperty(propertyName);
                if (prop != null)
                {
                    prop.floatValue = value;
                    serialized.ApplyModifiedProperties();
                }
            }
        }

        private string[] GetTerrains()
        {
            if (Asset == null)
            {
                return null;
            }

            if (terrainsField != null)
            {
                return terrainsField.GetValue(Asset) as string[];
            }

            if (serialized != null)
            {
                serialized.Update();
                SerializedProperty prop = serialized.FindProperty("m_Terrains");
                if (prop != null && prop.isArray)
                {
                    string[] result = new string[prop.arraySize];
                    for (int i = 0; i < prop.arraySize; i++)
                    {
                        result[i] = prop.GetArrayElementAtIndex(i).stringValue;
                    }
                    return result;
                }
            }

            return null;
        }

        private void SetTerrains(string[] value)
        {
            if (Asset == null)
            {
                return;
            }

            if (terrainsField != null)
            {
                terrainsField.SetValue(Asset, value);
                return;
            }

            if (serialized != null)
            {
                serialized.Update();
                SerializedProperty prop = serialized.FindProperty("m_Terrains");
                if (prop != null && prop.isArray)
                {
                    prop.arraySize = value?.Length ?? 0;
                    if (value != null)
                    {
                        for (int i = 0; i < value.Length; i++)
                        {
                            prop.GetArrayElementAtIndex(i).stringValue = value[i];
                        }
                    }
                    serialized.ApplyModifiedProperties();
                }
            }
        }

        public override int GetHashCode()
        {
            return Asset != null ? Asset.GetHashCode() : base.GetHashCode();
        }

        public override bool Equals(object obj)
        {
            return obj is TerrainAssetAdapter other && Equals(Asset, other.Asset);
        }

        // Compatibility accessors for legacy field-style usage
        public int m_Width
        {
            get => Width;
            set => Width = value;
        }

        public int m_Height
        {
            get => Height;
            set => Height = value;
        }

        public float m_X
        {
            get => OriginX;
            set => OriginX = value;
        }

        public float m_Z
        {
            get => OriginZ;
            set => OriginZ = value;
        }

        public string[] m_Terrains
        {
            get => Terrains;
            set => Terrains = value;
        }
    }
}
