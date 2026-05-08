using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace DivineDragon.MapTools
{
    /// <summary>
    /// Loads the project's TileColorOverrides asset on demand, caches a tid → color lookup, and
    /// notifies the paint tool when overrides change so all consumers (minimap, picker, scene
    /// gizmos, status chip) refresh.
    /// </summary>
    internal static class TileColorOverridesProvider
    {
        // Default path used when the asset doesn't exist yet. Lives under Assets/Editor so it
        // never ships in a player build.
        private const string DefaultAssetPath = "Assets/Editor/MapTools/TileColorOverrides.asset";

        private static TileColorOverrides cachedAsset;
        private static Dictionary<string, Color> lookup;
        // Tracks whether EnsureLoaded ran. cachedAsset can legitimately be null (no SO in the
        // project), so we need a separate flag — otherwise every call re-runs FindAssets, which
        // turns into a per-OnGUI-row asset database scan and chokes the editor.
        private static bool isLoaded;

        /// <summary>
        /// Asset that backs the override list. Auto-created at <see cref="DefaultAssetPath"/> on
        /// first mutation if no existing instance is found in the project.
        /// </summary>
        internal static TileColorOverrides Asset
        {
            get
            {
                EnsureLoaded();
                return cachedAsset;
            }
        }

        internal static bool TryGetOverride(string tid, out Color color)
        {
            EnsureLoaded();
            if (lookup != null && !string.IsNullOrEmpty(tid) && lookup.TryGetValue(tid, out color))
            {
                return true;
            }
            color = default;
            return false;
        }

        internal static int Count
        {
            get
            {
                EnsureLoaded();
                return lookup?.Count ?? 0;
            }
        }

        internal static void SetOverride(string tid, Color color)
        {
            if (string.IsNullOrEmpty(tid)) return;
            TileColorOverrides asset = GetOrCreateAsset();
            TileColorOverrides.Entry existing = FindEntry(asset, tid);
            if (existing != null)
            {
                existing.color = color;
            }
            else
            {
                asset.Entries.Add(new TileColorOverrides.Entry { tid = tid, color = color });
            }
            Persist(asset);
        }

        internal static void ClearOverride(string tid)
        {
            if (string.IsNullOrEmpty(tid)) return;
            EnsureLoaded();
            if (cachedAsset == null) return;
            int removed = cachedAsset.Entries.RemoveAll(e => string.Equals(e.tid, tid, StringComparison.Ordinal));
            if (removed > 0)
            {
                Persist(cachedAsset);
            }
        }

        internal static void ClearAll()
        {
            EnsureLoaded();
            if (cachedAsset == null || cachedAsset.Entries.Count == 0) return;
            cachedAsset.Entries.Clear();
            Persist(cachedAsset);
        }

        /// <summary>
        /// Forces a re-read of the asset. Call when the asset file changes outside our writes
        /// (e.g., via the inspector or git pull).
        /// </summary>
        internal static void InvalidateCache()
        {
            cachedAsset = null;
            lookup = null;
            isLoaded = false;
        }

        private static void EnsureLoaded()
        {
            if (isLoaded) return;
            cachedAsset = FindExistingAsset();
            RebuildLookup();
            isLoaded = true;
        }

        private static TileColorOverrides GetOrCreateAsset()
        {
            EnsureLoaded();
            if (cachedAsset != null) return cachedAsset;

            string folder = Path.GetDirectoryName(DefaultAssetPath)?.Replace("\\", "/");
            if (!string.IsNullOrEmpty(folder))
            {
                CreateAssetFolderRecursive(folder);
            }

            cachedAsset = ScriptableObject.CreateInstance<TileColorOverrides>();
            AssetDatabase.CreateAsset(cachedAsset, DefaultAssetPath);
            AssetDatabase.SaveAssets();
            return cachedAsset;
        }

        private static TileColorOverrides FindExistingAsset()
        {
            string[] guids = AssetDatabase.FindAssets("t:TileColorOverrides");
            if (guids.Length == 0) return null;

            // First match wins. If the project has multiple, we prefer the canonical default
            // location so users with split installations get predictable behavior.
            TileColorOverrides preferred = null;
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                TileColorOverrides asset = AssetDatabase.LoadAssetAtPath<TileColorOverrides>(path);
                if (asset == null) continue;
                if (string.Equals(path, DefaultAssetPath, StringComparison.OrdinalIgnoreCase))
                {
                    return asset;
                }
                if (preferred == null) preferred = asset;
            }
            return preferred;
        }

        private static TileColorOverrides.Entry FindEntry(TileColorOverrides asset, string tid)
        {
            if (asset == null) return null;
            foreach (TileColorOverrides.Entry entry in asset.Entries)
            {
                if (string.Equals(entry.tid, tid, StringComparison.Ordinal)) return entry;
            }
            return null;
        }

        private static void Persist(TileColorOverrides asset)
        {
            EditorUtility.SetDirty(asset);
            // SaveAssetIfDirty was added in Unity 2021.1; this project is on 2020.3 LTS.
            AssetDatabase.SaveAssets();
            RebuildLookup();
            // Mirrors the pipeline used by terrain.xml reloads so paint tool, minimap, picker,
            // and scene gizmos all re-fetch colors.
            TerrainPaintToolWindow.NotifyTerrainDatabaseChanged();
        }

        private static void RebuildLookup()
        {
            lookup = new Dictionary<string, Color>(StringComparer.Ordinal);
            if (cachedAsset == null) return;
            foreach (TileColorOverrides.Entry entry in cachedAsset.Entries)
            {
                if (string.IsNullOrEmpty(entry.tid)) continue;
                lookup[entry.tid] = entry.color;
            }
        }

        private static void CreateAssetFolderRecursive(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = Path.GetDirectoryName(folder)?.Replace("\\", "/");
            string leaf = Path.GetFileName(folder);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(leaf)) return;
            CreateAssetFolderRecursive(parent);
            if (!AssetDatabase.IsValidFolder(folder))
            {
                AssetDatabase.CreateFolder(parent, leaf);
            }
        }
    }
}
