using System;
using System.Collections.Generic;
using System.IO;
using DivineDragon;
using DivineDragon.Msbt.Editor;
using UnityEditor;
using UnityEngine;

namespace DivineDragon.MapTools
{
    public sealed class MapToolsSetupWindow : EditorWindow
    {
        private const string DivineDragonSettingsPath = "Project/Divine Dragon";
        private const string ProgressTitle = "Set up Map Tools";

        private bool showAdvanced;
        private string lastActionMessage;

        [MenuItem("Map Tools/Set up Map Tools", priority = -100)]
        public static void ShowWindow() => Open();

        public static void Open()
        {
            var win = GetWindow<MapToolsSetupWindow>("Set up Map Tools");
            win.minSize = new Vector2(420f, 280f);
            win.Focus();
        }

        private void OnEnable()
        {
            EditorApplication.projectChanged += Repaint;
        }

        private void OnDisable()
        {
            EditorApplication.projectChanged -= Repaint;
        }

        private void OnGUI()
        {
            DrawPathSection();

            if (!MapToolsPaths.IsConfigured)
            {
                return;
            }

            DrawLanguageSection();
            DrawDivider();
            DrawPrimarySection();
            DrawAdvancedSection();
        }

        private void DrawLanguageSection()
        {
            List<Language> source = DiscoverSourceLanguages();
            if (source.Count == 0)
            {
                source = new List<Language>(MsbtProvider.AvailableLanguages);
            }
            if (source.Count == 0) return;

            string[] labels = new string[source.Count];
            for (int i = 0; i < source.Count; i++)
            {
                string code = source[i].Code;
                labels[i] = (code == "USen" || code == "JPja") ? $"{code} (main)" : code;
            }

            int currentIndex = 0;
            Language current = MsbtProvider.CurrentLanguage;
            for (int i = 0; i < source.Count; i++)
            {
                if (source[i].Equals(current)) { currentIndex = i; break; }
            }

            int newIndex = EditorGUILayout.Popup("Language", currentIndex, labels);
            if (newIndex != currentIndex && newIndex >= 0 && newIndex < source.Count)
            {
                MsbtProvider.SetLanguage(source[newIndex]);
            }
            EditorGUILayout.Space(4);
        }

        private void DrawPathSection()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Game data path", EditorStyles.boldLabel);

            if (!MapToolsPaths.IsConfigured)
            {
                EditorGUILayout.HelpBox(
                    "Game data path not configured.\nGo to Project Settings → Divine Dragon and set the path to settings.json from your game dump.",
                    MessageType.Error);
                if (GUILayout.Button("Open Project Settings"))
                {
                    SettingsService.OpenProjectSettings(DivineDragonSettingsPath);
                }
            }
            else
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(MapToolsPaths.GameBuildPath, EditorStyles.miniLabel);
                    if (GUILayout.Button("Edit", EditorStyles.miniButton, GUILayout.Width(50)))
                    {
                        SettingsService.OpenProjectSettings(DivineDragonSettingsPath);
                    }
                }
            }
            EditorGUILayout.Space(4);
        }

        private static void DrawDivider()
        {
            EditorGUILayout.Space(6);
            Rect r = EditorGUILayout.GetControlRect(false, 1f);
            EditorGUI.DrawRect(r, new Color(0.5f, 0.5f, 0.5f, 0.3f));
            EditorGUILayout.Space(6);
        }

        private void DrawPrimarySection()
        {
            bool hasTerrain = HasTerrainXml();
            bool hasChapter = HasChapterXml();
            string currentLang = MsbtProvider.CurrentLanguage.Code;
            bool hasCurrentLang = HasFullLocalization(currentLang);

            int presentCount = (hasTerrain ? 1 : 0) + (hasChapter ? 1 : 0) + (hasCurrentLang ? 1 : 0);

            string statusText;
            string buttonLabel;
            if (presentCount == 0)
            {
                statusText = "Map Tools needs setup";
                buttonLabel = "Set up Map Tools";
            }
            else if (presentCount < 3)
            {
                statusText = "Map Tools is partially set up";
                buttonLabel = "Continue setup";
            }
            else
            {
                statusText = "Map Tools is set up";
                buttonLabel = "Re-run setup";
            }

            GUIStyle centeredBold = new GUIStyle(EditorStyles.boldLabel) { alignment = TextAnchor.MiddleCenter };
            EditorGUILayout.LabelField(statusText, centeredBold);
            EditorGUILayout.Space(6);

            if (GUILayout.Button(buttonLabel, GUILayout.Height(36f)))
            {
                RunPrimarySetup();
                GUIUtility.ExitGUI();
            }

            EditorGUILayout.Space(6);
            DrawSummaryLine(hasTerrain, hasChapter, hasCurrentLang, currentLang);

            if (!string.IsNullOrEmpty(lastActionMessage))
            {
                EditorGUILayout.Space(4);
                GUIStyle centeredMini = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter };
                EditorGUILayout.LabelField(lastActionMessage, centeredMini);
            }
        }

        private static void DrawSummaryLine(bool hasTerrain, bool hasChapter, bool hasLang, string lang)
        {
            GUIStyle centered = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                richText = true,
            };
            string label = $"{Pill("Terrain", hasTerrain)}   ·   {Pill("Chapter", hasChapter)}   ·   {Pill(lang, hasLang)}";
            EditorGUILayout.LabelField(label, centered);
        }

        private static string Pill(string label, bool ok)
        {
            string color = ok ? "#6FD96F" : "#D9C76F";
            string mark = ok ? "✓" : "✗";
            return $"<color={color}>{label} {mark}</color>";
        }

        private void DrawAdvancedSection()
        {
            EditorGUILayout.Space(10);
            showAdvanced = EditorGUILayout.Foldout(showAdvanced, "Advanced", true);
            if (!showAdvanced) return;

            DrawAdvancedRow("Terrain.xml", HasTerrainXml(), () => ReExtract(ExtractTerrainXmlInternal, "Terrain.xml", invalidateDefinitions: true));
            DrawAdvancedRow("Chapter.xml", HasChapterXml(), () => ReExtract(ExtractChapterXmlInternal, "Chapter.xml", invalidateDefinitions: false));

            foreach (Language lang in DiscoverSourceLanguages())
            {
                string code = lang.Code;
                DrawAdvancedRow($"{code} localization",
                    HasFullLocalization(code),
                    () => ReExtract(() => ExtractLocalizationInternal(code),
                        $"{code} localization",
                        invalidateLocalizer: true));
            }
        }

        private static void DrawAdvancedRow(string label, bool present, Action action)
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                Color prev = GUI.color;
                GUI.color = present ? new Color(0.4f, 0.85f, 0.4f) : new Color(0.85f, 0.78f, 0.4f);
                GUILayout.Label(present ? "✓" : "✗", EditorStyles.boldLabel, GUILayout.Width(16));
                GUI.color = prev;

                EditorGUILayout.LabelField(label);

                if (GUILayout.Button(present ? "Re-extract" : "Extract", GUILayout.Width(90)))
                {
                    action();
                    GUIUtility.ExitGUI();
                }
            }
        }

        // --- orchestration ---

        private void RunPrimarySetup()
        {
            try
            {
                bool didAnything = false;
                int step = 0;
                const int totalSteps = 3;

                if (!HasTerrainXml())
                {
                    EditorUtility.DisplayProgressBar(ProgressTitle, "Extracting Terrain.xml...", ++step / (float)totalSteps);
                    didAnything |= ExtractTerrainXmlInternal();
                }
                else
                {
                    step++;
                }

                if (!HasChapterXml())
                {
                    EditorUtility.DisplayProgressBar(ProgressTitle, "Extracting Chapter.xml...", ++step / (float)totalSteps);
                    didAnything |= ExtractChapterXmlInternal();
                }
                else
                {
                    step++;
                }

                string lang = MsbtProvider.CurrentLanguage.Code;
                if (!HasFullLocalization(lang))
                {
                    EditorUtility.DisplayProgressBar(ProgressTitle, $"Extracting {lang} localization...", ++step / (float)totalSteps);
                    didAnything |= ExtractLocalizationInternal(lang);
                }

                if (didAnything)
                {
                    AssetDatabase.Refresh();
                    TerrainDefinitions.InvalidateCache();
                    MsbtProvider.InvalidateCache();
                    lastActionMessage = "Setup complete";
                }
                else
                {
                    lastActionMessage = "Nothing to do — already set up. Use Advanced to re-extract.";
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Map Tools Setup] Failed: {ex}");
                lastActionMessage = $"Setup failed: {ex.Message}";
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private void ReExtract(Func<bool> action, string label, bool invalidateDefinitions = false, bool invalidateLocalizer = false)
        {
            try
            {
                EditorUtility.DisplayProgressBar(ProgressTitle, $"Extracting {label}...", 0.5f);
                if (action())
                {
                    AssetDatabase.Refresh();
                    if (invalidateDefinitions) TerrainDefinitions.InvalidateCache();
                    if (invalidateLocalizer) MsbtProvider.InvalidateCache();
                    lastActionMessage = $"Re-extracted {label}";
                }
                else
                {
                    lastActionMessage = $"Failed to extract {label} — see console";
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Map Tools Setup] Re-extract {label} failed: {ex}");
                lastActionMessage = $"Failed to extract {label}: {ex.Message}";
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private static bool ExtractTerrainXmlInternal()
        {
            if (!File.Exists(MapToolsPaths.TerrainXmlBundlePath))
            {
                Debug.LogWarning($"[Map Tools Setup] Bundle not found: {MapToolsPaths.TerrainXmlBundlePath}");
                return false;
            }
            return Dumper.ExtractAssetAtPath(MapToolsPaths.TerrainXmlBundlePath);
        }

        private static bool ExtractChapterXmlInternal()
        {
            if (!File.Exists(MapToolsPaths.ChapterBundlePath))
            {
                Debug.LogWarning($"[Map Tools Setup] Bundle not found: {MapToolsPaths.ChapterBundlePath}");
                return false;
            }
            return Dumper.ExtractAssetAtPath(MapToolsPaths.ChapterBundlePath);
        }

        private static bool ExtractLocalizationInternal(string lang)
        {
            bool didAnything = false;

            // Base game GameData — only if the source bundle is available in the dump.
            (string bundleFolder, string extractedPath, string dumpedPath) = LocalizationPathsFor(lang);
            if (bundleFolder != null)
            {
                string gameDataBundle = Path.Combine(bundleFolder, "GameData.bytes.bundle");
                if (!File.Exists(gameDataBundle))
                {
                    Debug.LogWarning($"[Map Tools Setup] GameData bundle not found, skipping base: {gameDataBundle}");
                }
                else if (!Dumper.ExtractAssetAtPath(gameDataBundle))
                {
                    Debug.LogWarning($"[Map Tools Setup] Failed to extract GameData bundle: {gameDataBundle}");
                }
                else
                {
                    string bytesPath = Path.Combine(extractedPath, "GameData.bytes");
                    if (!File.Exists(bytesPath))
                    {
                        Debug.LogWarning($"[Map Tools Setup] Extracted bytes not found: {bytesPath}");
                    }
                    else
                    {
                        string outputPath = Path.Combine(dumpedPath, "GameData.txt");
                        if (MsbtDumper.DumpFile(bytesPath, outputPath))
                        {
                            didAnything = true;
                        }
                    }
                }
            }

            // Patch MSBTs (DLC chapter titles live here). Extract any source bundles for this
            // language that exist in the dump, then dump whatever .bytes ended up in the project.
            if (TryExtractPatchMsbtBundles(lang))
            {
                didAnything = true;
            }

            int patchCount = MsbtDumper.DumpPatchMessages(lang);
            if (patchCount > 0)
            {
                didAnything = true;
            }

            return didAnything;
        }

        private static bool TryExtractPatchMsbtBundles(string lang)
        {
            if (string.IsNullOrEmpty(lang) || lang.Length < 2)
            {
                return false;
            }

            string countryLower = lang.Substring(0, 2).ToLowerInvariant();
            string langLower = lang.ToLowerInvariant();

            var paths = new List<string>(4);
            for (int patchN = 0; patchN <= 3; patchN++)
            {
                string sourceBundle = $"{MapToolsPaths.MessageAssetRoot}/{countryLower}/{langLower}/patch{patchN}.bytes.bundle";
                if (File.Exists(sourceBundle))
                {
                    paths.Add(sourceBundle);
                }
            }

            if (paths.Count == 0)
            {
                return false;
            }

            return Dumper.ExtractAssets(paths.ToArray());
        }

        // Patches are "complete" for a language when every patch source bundle that exists in
        // the dump has been extracted AND dumped to text for that language.
        private static bool HasPatchDumps(string lang)
        {
            if (string.IsNullOrEmpty(lang) || lang.Length < 2)
            {
                return true;
            }

            string country = lang.Substring(0, 2).ToUpperInvariant();
            string countryLower = country.ToLowerInvariant();
            string langLower = lang.ToLowerInvariant();

            for (int patchN = 0; patchN <= 3; patchN++)
            {
                string sourceBundle = $"{MapToolsPaths.MessageAssetRoot}/{countryLower}/{langLower}/patch{patchN}.bytes.bundle";
                if (!File.Exists(sourceBundle))
                {
                    continue;
                }

                string langDir = Path.Combine(Application.dataPath,
                    $"Share/Addressables/Patch/Patch{patchN}/Message/{country}/{lang}").Replace("\\", "/");
                if (!Directory.Exists(langDir) || Directory.GetFiles(langDir, "*.bytes").Length == 0)
                {
                    return false;
                }

                string dumpedDir = langDir + "_dumped";
                if (!Directory.Exists(dumpedDir) || Directory.GetFiles(dumpedDir, "*.txt").Length == 0)
                {
                    return false;
                }
            }

            return true;
        }

        private static (string bundleFolder, string extractedPath, string dumpedPath) LocalizationPathsFor(string lang)
        {
            if (string.IsNullOrEmpty(lang) || lang.Length != 4)
            {
                Debug.LogWarning($"[Map Tools Setup] Invalid language code: {lang}");
                return (null, null, null);
            }
            return MsbtPaths.For(new Language(lang));
        }

        // Walks <GameBuildPath>/fe_assets_message/<country>/<langDir>/ and yields one Language
        // per pair where the lang dir name is 4 chars and starts with the country code.
        // Excludes pu/puppet (lang dir 6 chars) and so/sound (5 chars) automatically.
        private static List<Language> DiscoverSourceLanguages()
        {
            var result = new List<Language>();
            if (!MapToolsPaths.IsConfigured) return result;

            string root = MapToolsPaths.MessageAssetRoot;
            if (!Directory.Exists(root)) return result;

            foreach (string countryDir in Directory.GetDirectories(root))
            {
                string country = Path.GetFileName(countryDir);
                if (country == null || country.Length != 2) continue;

                foreach (string langDir in Directory.GetDirectories(countryDir))
                {
                    string langDirName = Path.GetFileName(langDir);
                    if (langDirName == null || langDirName.Length != 4) continue;
                    if (!langDirName.StartsWith(country, StringComparison.OrdinalIgnoreCase)) continue;

                    // Engage convention: project code is country uppercase + lang suffix lowercase
                    string code = country.ToUpperInvariant() + langDirName.Substring(2);
                    result.Add(new Language(code));
                }
            }

            result.Sort(MsbtProvider.MainsFirst);
            return result;
        }

        // --- status checks ---

        private static bool HasTerrainXml()
        {
            string fullPath = Path.Combine(ProjectRoot(), TerrainDefinitions.TerrainXmlAssetRelativePath).Replace("\\", "/");
            return File.Exists(fullPath);
        }

        private static bool HasChapterXml()
        {
            string fullPath = Path.Combine(ProjectRoot(), MapToolsPaths.ChapterAssetPath).Replace("\\", "/");
            return File.Exists(fullPath);
        }

        private static bool HasLanguageDump(string lang)
        {
            foreach (Language available in MsbtProvider.AvailableLanguages)
            {
                if (available.Code == lang) return true;
            }
            return false;
        }

        private static bool HasFullLocalization(string lang) =>
            HasLanguageDump(lang) && HasPatchDumps(lang);

        private static string ProjectRoot()
        {
            string assetsPath = Application.dataPath;
            string projectRoot = Path.GetDirectoryName(assetsPath);
            return string.IsNullOrEmpty(projectRoot) ? assetsPath : projectRoot.Replace("\\", "/");
        }
    }
}
