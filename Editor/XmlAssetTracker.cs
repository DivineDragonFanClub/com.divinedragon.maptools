using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace DivineDragon.MapTools
{
    internal static class XmlAssetTracker
    {
        private static readonly Dictionary<string, List<Action>> Watchers =
            new Dictionary<string, List<Action>>(StringComparer.OrdinalIgnoreCase);

        public static void Register(string assetPath, Action callback)
        {
            if (callback == null)
            {
                return;
            }

            string normalizedPath = Normalize(assetPath);
            if (string.IsNullOrEmpty(normalizedPath))
            {
                return;
            }

            if (!Watchers.TryGetValue(normalizedPath, out List<Action> listeners))
            {
                listeners = new List<Action>();
                Watchers[normalizedPath] = listeners;
            }

            if (!listeners.Contains(callback))
            {
                listeners.Add(callback);
            }
        }

        internal static void Notify(string assetPath)
        {
            string normalizedPath = Normalize(assetPath);
            if (string.IsNullOrEmpty(normalizedPath))
            {
                return;
            }

            if (!Watchers.TryGetValue(normalizedPath, out List<Action> listeners) || listeners.Count == 0)
            {
                return;
            }

            // Copy to avoid modification during iteration
            var snapshot = listeners.ToArray();
            foreach (var listener in snapshot)
            {
                try
                {
                    listener?.Invoke();
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
            }
        }

        private static string Normalize(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
            {
                return null;
            }

            string normalized = assetPath.Replace("\\", "/");
            return normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ? normalized : null;
        }
    }

    internal sealed class XmlAssetTrackerPostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            Dispatch(importedAssets);
            Dispatch(deletedAssets);
            Dispatch(movedAssets);
            Dispatch(movedFromAssetPaths);
        }

        private static void Dispatch(string[] assetPaths)
        {
            if (assetPaths == null)
            {
                return;
            }

            foreach (string assetPath in assetPaths)
            {
                XmlAssetTracker.Notify(assetPath);
            }
        }
    }
}
