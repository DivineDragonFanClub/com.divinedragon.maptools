using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace DivineDragon.MapTools
{
    /// <summary>
    /// Provides localized terrain names by parsing GameData.txt script files.
    /// Supports multiple languages and custom terrain definitions.
    /// </summary>
    internal static class TerrainLocalizer
    {
        private const string PREFS_LANGUAGE = "TerrainLocalizer_Language";
        private const string MESSAGE_RELATIVE_PATH = "Share/Addressables/Message";
        private const string DEFAULT_LANGUAGE = "USen";

        private static Dictionary<string, string> localizedNames;
        private static string currentLanguage;
        private static string[] availableLanguages;
        private static bool hasWarned;

        /// <summary>
        /// Event fired when the language is changed. Subscribe to refresh cached localized data.
        /// </summary>
        public static event Action OnLanguageChanged;

        /// <summary>
        /// Available language codes detected from *_scripts folders.
        /// </summary>
        public static string[] AvailableLanguages
        {
            get
            {
                if (availableLanguages == null)
                {
                    DetectLanguages();
                }
                return availableLanguages;
            }
        }

        /// <summary>
        /// Currently selected language code.
        /// </summary>
        public static string CurrentLanguage
        {
            get
            {
                if (string.IsNullOrEmpty(currentLanguage))
                {
                    currentLanguage = EditorPrefs.GetString(PREFS_LANGUAGE, DEFAULT_LANGUAGE);
                }
                return currentLanguage;
            }
        }

        /// <summary>
        /// Returns true if localization data is available for at least one language.
        /// </summary>
        public static bool HasLocalization
        {
            get
            {
                var langs = AvailableLanguages;
                // If only default language exists and no actual script folder found, we have no localization
                if (langs.Length == 1 && langs[0] == DEFAULT_LANGUAGE)
                {
                    string scriptFolder = FindScriptFolder(DEFAULT_LANGUAGE);
                    return !string.IsNullOrEmpty(scriptFolder);
                }
                return langs.Length > 0;
            }
        }

        /// <summary>
        /// Get localized name for an MTID key (e.g., "MTID_Ground" → "Plain").
        /// Returns the key itself if no localization found.
        /// </summary>
        public static string GetLocalizedName(string mtidKey)
        {
            if (string.IsNullOrEmpty(mtidKey))
            {
                return mtidKey;
            }

            EnsureLoaded();

            if (localizedNames != null && localizedNames.TryGetValue(mtidKey, out string name))
            {
                return name;
            }

            return mtidKey; // fallback to key
        }

        /// <summary>
        /// Change the current language. Triggers reload of localization data.
        /// </summary>
        public static void SetLanguage(string lang)
        {
            if (string.IsNullOrEmpty(lang) || lang == currentLanguage)
            {
                return;
            }

            currentLanguage = lang;
            EditorPrefs.SetString(PREFS_LANGUAGE, lang);
            InvalidateCache();
            OnLanguageChanged?.Invoke();
        }

        /// <summary>
        /// Force reload of localization data.
        /// </summary>
        public static void InvalidateCache()
        {
            localizedNames = null;
            hasWarned = false;
        }

        private static void EnsureLoaded()
        {
            if (localizedNames != null)
            {
                return;
            }

            localizedNames = new Dictionary<string, string>(StringComparer.Ordinal);

            string lang = CurrentLanguage;
            string scriptFolder = FindScriptFolder(lang);
            
            if (string.IsNullOrEmpty(scriptFolder))
            {
                if (!hasWarned)
                {
                    Debug.LogWarning($"[TerrainLocalizer] Script folder not found for language '{lang}'");
                    hasWarned = true;
                }
                return;
            }

            string gameDataPath = Path.Combine(scriptFolder, "GameData.txt");

            if (!File.Exists(gameDataPath))
            {
                if (!hasWarned)
                {
                    Debug.LogWarning($"[TerrainLocalizer] GameData.txt not found for language '{lang}' at: {gameDataPath}");
                    hasWarned = true;
                }
                return;
            }

            ParseScriptFile(gameDataPath);
        }

        private static void ParseScriptFile(string path)
        {
            // Parse AstraScript format:
            // [MTID_Ground]
            // Plain
            //
            // [MTID_Forest]
            // Forest

            try
            {
                string[] lines = File.ReadAllLines(path);
                string currentKey = null;

                foreach (string line in lines)
                {
                    string trimmed = line.Trim();

                    // Check for key header: [MTID_Something]
                    if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                    {
                        currentKey = trimmed.Substring(1, trimmed.Length - 2);
                    }
                    else if (!string.IsNullOrEmpty(trimmed) && currentKey != null)
                    {
                        // First non-empty line after key is the value
                        localizedNames[currentKey] = trimmed;
                        currentKey = null;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[TerrainLocalizer] Failed to parse {path}: {ex.Message}");
            }
        }

        private static void DetectLanguages()
        {
            var languages = new List<string>();

            string basePath = GetMessageBasePath();
            if (!string.IsNullOrEmpty(basePath) && Directory.Exists(basePath))
            {
                SearchForScriptFolders(basePath, languages);
            }

            // Sort for consistent ordering
            languages.Sort(StringComparer.OrdinalIgnoreCase);

            availableLanguages = languages.Count > 0 ? languages.ToArray() : new[] { DEFAULT_LANGUAGE };
        }

        private static void SearchForScriptFolders(string currentPath, List<string> languages)
        {
            try
            {
                // Check current directory for _scripts folders
                foreach (string dir in Directory.GetDirectories(currentPath))
                {
                    string dirName = Path.GetFileName(dir);
                    if (dirName.EndsWith("_scripts"))
                    {
                        string gameDataPath = Path.Combine(dir, "GameData.txt");
                        if (File.Exists(gameDataPath))
                        {
                            string lang = dirName.Substring(0, dirName.Length - "_scripts".Length);
                            if (!languages.Contains(lang))
                            {
                                languages.Add(lang);
                            }
                        }
                    }
                }

                // Recursively search subdirectories
                foreach (string dir in Directory.GetDirectories(currentPath))
                {
                    SearchForScriptFolders(dir, languages);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[TerrainLocalizer] Error searching directory {currentPath}: {ex.Message}");
            }
        }

        private static string FindScriptFolder(string lang)
        {
            string basePath = GetMessageBasePath();
            if (string.IsNullOrEmpty(basePath) || !Directory.Exists(basePath))
            {
                return null;
            }

            return FindScriptFolderRecursive(basePath, lang);
        }

        private static string FindScriptFolderRecursive(string currentPath, string lang)
        {
            try
            {
                // Check current directory for the matching _scripts folder
                string scriptFolder = Path.Combine(currentPath, $"{lang}_scripts");
                if (Directory.Exists(scriptFolder))
                {
                    string gameDataPath = Path.Combine(scriptFolder, "GameData.txt");
                    if (File.Exists(gameDataPath))
                    {
                        return scriptFolder;
                    }
                }

                // Recursively search subdirectories
                foreach (string dir in Directory.GetDirectories(currentPath))
                {
                    string result = FindScriptFolderRecursive(dir, lang);
                    if (!string.IsNullOrEmpty(result))
                    {
                        return result;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[TerrainLocalizer] Error searching directory {currentPath}: {ex.Message}");
            }

            return null;
        }

        private static string GetMessageBasePath()
        {
            string assetsPath = Application.dataPath;
            return Path.Combine(assetsPath, MESSAGE_RELATIVE_PATH).Replace("\\", "/");
        }
    }
}