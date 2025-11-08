using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using UnityEditor;
using UnityEngine;
using DivineDragon;

namespace DivineDragon.MapTools
{
    /// <summary>
    /// TEST-ONLY utility: queues every chapter-related bundle for extraction in one go.
    /// Remove this once bulk dump investigations conclude.
    /// </summary>
    public class ChapterDumpAllTestWindow : EditorWindow
    {
        private const string WindowTitle = "[TEST] Chapter Dump (All Maps)";
        private const string ChapterAssetPath = "Assets/Share/Addressables/Patch/Patch3/GameData/Chapter.xml";
        private const string GameDataShareAssetPath = "Assets/Share/Addressables/Patch/Patch3/GameData";
        private const int ExtractionBatchSize = 10;

        private string lastMessage;
        private bool isExtracting;

        [MenuItem("Window/Map Tools/Test/Dump All Chapters (Test Only)")]
        public static void ShowWindow()
        {
            var window = GetWindow<ChapterDumpAllTestWindow>(WindowTitle);
            window.minSize = new Vector2(420f, 140f);
        }

        private void OnGUI()
        {
            EditorGUILayout.HelpBox("Test-only helper that dumps every chapter's scene, terrain, and dispos bundles. Remove when no longer needed.", MessageType.Warning);

            using (new EditorGUI.DisabledScope(isExtracting))
            {
                if (GUILayout.Button("Dump All Chapters (Test Only)", GUILayout.Height(32f)))
                {
                    DumpAllChapters();
                }
            }

            EditorGUILayout.Space();

            if (GUILayout.Button("Extract Chapter.xml (if missing)", GUILayout.Height(22f)))
            {
                ExtractChapterXmlIfNeeded();
            }

            if (!string.IsNullOrEmpty(lastMessage))
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(lastMessage, MessageType.None);
            }
        }

        private void DumpAllChapters()
        {
            string chapterAssetPath = FindChapterAssetPath();
            if (string.IsNullOrEmpty(chapterAssetPath))
            {
                lastMessage = "Chapter.xml not found under Assets. Extract it first.";
                return;
            }

            if (!TryGetGameDataPaths(out GameDataPaths gameDataPaths))
            {
                return;
            }

            List<ChapterRecord> chapters;
            try
            {
                chapters = LoadChapterData(chapterAssetPath);
            }
            catch (Exception ex)
            {
                lastMessage = $"Failed to load Chapter.xml: {ex.Message}";
                return;
            }

            if (chapters.Count == 0)
            {
                lastMessage = "No chapters found in Chapter.xml.";
                return;
            }

            HashSet<string> queuedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> warnings = new List<string>();
            List<string> batchTargets = new List<string>();
            List<string> batchChapterLabels = new List<string>();

            int sceneFound = 0;
            int sceneMissing = 0;
            int terrainFound = 0;
            int terrainMissing = 0;
            int disposFound = 0;
            int disposMissing = 0;
            int chaptersWithAnyTargets = 0;
            bool overallSuccess = true;

            isExtracting = true;
            try
            {
                List<string> supportingTargets = new List<string>();
                EnsureSupportingAssets(gameDataPaths, supportingTargets, queuedPaths);

                if (supportingTargets.Count > 0)
                {
                    EditorUtility.DisplayProgressBar(WindowTitle, $"Extracting {supportingTargets.Count} supporting bundles...", 0.5f);
                    if (!RunExtractionBatch(supportingTargets, "[supporting XMLs]"))
                    {
                        overallSuccess = false;
                    }
                    batchTargets.Clear();
                }

                for (int i = 0; i < chapters.Count; i++)
                {
                    ChapterRecord chapter = chapters[i];
                    float progress = (i + 1f) / chapters.Count;
                    EditorUtility.DisplayProgressBar(WindowTitle, $"Queueing bundles ({i + 1}/{chapters.Count})...", progress);

                    int beforeCount = batchTargets.Count;
                    bool chapterHasBundle = false;

                    if (TryAddSceneBundle(gameDataPaths, chapter, batchTargets, queuedPaths))
                    {
                        sceneFound++;
                        chapterHasBundle = true;
                    }
                    else
                    {
                        sceneMissing++;
                        warnings.Add($"Scene bundle missing for {chapter.Cid}.");
                    }

                    if (TryAddTerrainBundle(gameDataPaths, chapter, batchTargets, queuedPaths))
                    {
                        terrainFound++;
                        chapterHasBundle = true;
                    }
                    else
                    {
                        terrainMissing++;
                        warnings.Add($"Terrain bundle missing for {chapter.Cid}.");
                    }

                    if (TryAddDisposBundle(gameDataPaths, chapter, batchTargets, queuedPaths))
                    {
                        disposFound++;
                        chapterHasBundle = true;
                    }
                    else
                    {
                        disposMissing++;
                        warnings.Add($"Dispos bundle missing for {chapter.Cid}.");
                    }

                    if (chapterHasBundle)
                    {
                        chaptersWithAnyTargets++;
                        if (batchTargets.Count > beforeCount)
                        {
                            batchChapterLabels.Add(chapter.Cid);
                        }
                    }

                    if (batchTargets.Count >= ExtractionBatchSize)
                    {
                        string batchLabel = batchChapterLabels.Count == 0
                            ? "chapters batch"
                            : $"{batchChapterLabels[0]}…{batchChapterLabels[batchChapterLabels.Count - 1]}";
                        EditorUtility.DisplayProgressBar(WindowTitle, $"Extracting {batchTargets.Count} bundles ({batchLabel})...", progress);

                        if (!RunExtractionBatch(batchTargets, batchLabel))
                        {
                            overallSuccess = false;
                        }

                        batchTargets.Clear();
                        batchChapterLabels.Clear();
                    }
                }

                if (batchTargets.Count > 0)
                {
                    string batchLabel = batchChapterLabels.Count == 0
                        ? "final batch"
                        : $"{batchChapterLabels[0]}…{batchChapterLabels[batchChapterLabels.Count - 1]}";
                    EditorUtility.DisplayProgressBar(WindowTitle, $"Extracting {batchTargets.Count} bundles ({batchLabel})...", 1f);

                    if (!RunExtractionBatch(batchTargets, batchLabel))
                    {
                        overallSuccess = false;
                    }

                    batchTargets.Clear();
                    batchChapterLabels.Clear();
                }

                string statusMessage =
                    $"Queued {queuedPaths.Count} unique bundles across {chaptersWithAnyTargets} chapters.\n" +
                    $"Scenes: {sceneFound} found, {sceneMissing} missing. Terrains: {terrainFound} found, {terrainMissing} missing. Dispos: {disposFound} found, {disposMissing} missing.";

                if (!overallSuccess)
                {
                    statusMessage = "Extraction reported failures.\n" + statusMessage;
                }

                if (warnings.Count > 0)
                {
                    statusMessage += "\nWarnings:\n" + string.Join("\n", warnings);
                }

                lastMessage = statusMessage;
            }
            catch (Exception ex)
            {
                overallSuccess = false;
                lastMessage = $"Extraction error: {ex.Message}";
            }
            finally
            {
                isExtracting = false;
                EditorUtility.ClearProgressBar();
            }

            if (!overallSuccess)
            {
                Debug.LogWarning("[TEST Chapter Dump] One or more batches reported issues. See the window message for details.");
            }
        }

        private void ExtractChapterXmlIfNeeded()
        {
            string assetPath = FindChapterAssetPath();
            if (!string.IsNullOrEmpty(assetPath))
            {
                lastMessage = $"Chapter.xml already present at {assetPath}.";
                return;
            }

            if (!TryGetGameDataPaths(out GameDataPaths gameDataPaths))
            {
                return;
            }

            if (!File.Exists(gameDataPaths.ChapterBundlePath))
            {
                lastMessage = $"Bundle not found: {gameDataPaths.ChapterBundlePath}";
                return;
            }

            try
            {
                EditorUtility.DisplayProgressBar(WindowTitle, "Extracting Chapter.xml...", 0.5f);
                bool success = Dumper.ExtractAssetAtPath(gameDataPaths.ChapterBundlePath);
                AssetDatabase.Refresh();
                lastMessage = success ? "Chapter.xml extracted successfully." : "Extraction failed. Check console for details.";
            }
            catch (Exception ex)
            {
                lastMessage = $"Extraction error: {ex.Message}";
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private static string FindChapterAssetPath()
        {
            string projectRoot = ProjectRootPath();
            string candidate = Path.Combine(projectRoot, ChapterAssetPath).Replace("\\", "/");
            if (File.Exists(candidate))
            {
                return ChapterAssetPath;
            }

            string projectAssetsPath = Application.dataPath.Replace("\\", "/");
            if (!Directory.Exists(projectAssetsPath))
            {
                return null;
            }

            try
            {
                foreach (string file in Directory.EnumerateFiles(projectAssetsPath, "Chapter.xml", SearchOption.AllDirectories))
                {
                    return "Assets" + file.Substring(projectAssetsPath.Length).Replace("\\", "/");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[TEST Chapter Dump] Failed to scan Assets: {ex.Message}");
            }

            return null;
        }

        private static List<ChapterRecord> LoadChapterData(string chapterAssetPath)
        {
            List<ChapterRecord> chapters = new List<ChapterRecord>();
            string fullPath = Path.Combine(ProjectRootPath(), chapterAssetPath).Replace("\\", "/");

            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("Chapter.xml missing at expected path.", fullPath);
            }

            XDocument doc = XDocument.Load(fullPath);
            XElement sheet = doc.Root?
                .Elements("Sheet")
                .FirstOrDefault(e => string.Equals((string)e.Attribute("Name"), "チャプター", StringComparison.Ordinal));

            if (sheet == null)
            {
                return chapters;
            }

            XElement dataNode = sheet.Element("Data");
            if (dataNode == null)
            {
                return chapters;
            }

            foreach (XElement param in dataNode.Elements("Param"))
            {
                ChapterRecord record = new ChapterRecord
                {
                    Cid = (string)param.Attribute("Cid"),
                    Title = (string)param.Attribute("Help"),
                    RawField = (string)param.Attribute("Field"),
                    RawTerrain = (string)param.Attribute("Terrain")
                };

                chapters.Add(record);
            }

            return chapters;
        }

        private void EnsureSupportingAssets(GameDataPaths paths, List<string> extractionTargets, HashSet<string> queued)
        {
            foreach (SupportAsset support in EnumerateSupportingAssets(paths))
            {
                string projectPath = Path.Combine(ProjectRootPath(), support.ExpectedAssetPath).Replace("\\", "/");
                if (!File.Exists(projectPath) && !string.IsNullOrEmpty(support.BundlePath) && File.Exists(support.BundlePath))
                {
                    QueueBundle(support.BundlePath, extractionTargets, queued);
                }
            }
        }

        private bool TryAddSceneBundle(GameDataPaths paths, ChapterRecord chapter, List<string> extractionTargets, HashSet<string> queued)
        {
            string suffix = GetChapterSuffix(chapter.Cid);
            if (string.IsNullOrEmpty(suffix))
            {
                return false;
            }

            foreach (string candidate in GenerateSuffixCandidates(suffix))
            {
                string bundlePath = CombineNormalized(paths.GameDataRoot, $"fe_scenes_fld_{candidate}.bundle");
                if (File.Exists(bundlePath))
                {
                    QueueBundle(bundlePath, extractionTargets, queued);
                    return true;
                }
            }

            return false;
        }

        private bool TryAddTerrainBundle(GameDataPaths paths, ChapterRecord chapter, List<string> extractionTargets, HashSet<string> queued)
        {
            string suffix = GetChapterSuffix(chapter.Cid);
            if (string.IsNullOrEmpty(suffix))
            {
                return false;
            }

            foreach (string candidate in GenerateSuffixCandidates(suffix))
            {
                string bundlePath = CombineNormalized(paths.TerrainDirectory, $"mapterrain_{candidate}.bundle");
                if (File.Exists(bundlePath))
                {
                    QueueBundle(bundlePath, extractionTargets, queued);
                    return true;
                }
            }

            return false;
        }

        private bool TryAddDisposBundle(GameDataPaths paths, ChapterRecord chapter, List<string> extractionTargets, HashSet<string> queued)
        {
            string suffix = GetChapterSuffix(chapter.Cid);
            if (string.IsNullOrEmpty(suffix))
            {
                return false;
            }

            foreach (string candidate in GenerateSuffixCandidates(suffix))
            {
                string bundlePath = CombineNormalized(paths.DisposDirectory, $"{candidate}.xml.bundle");
                if (File.Exists(bundlePath))
                {
                    QueueBundle(bundlePath, extractionTargets, queued);
                    return true;
                }
            }

            return false;
        }

        private void QueueBundle(string bundlePath, List<string> extractionTargets, HashSet<string> queued)
        {
            if (queued.Add(bundlePath))
            {
                extractionTargets.Add(bundlePath);
            }
        }

        private bool RunExtractionBatch(List<string> bundlePaths, string label)
        {
            if (bundlePaths == null || bundlePaths.Count == 0)
            {
                return true;
            }

            try
            {
                bool success = Dumper.ExtractMultipleAssets(bundlePaths);
                AssetDatabase.Refresh();

                if (!success)
                {
                    Debug.LogWarning($"[TEST Chapter Dump] Batch extraction reported failures for {label} ({bundlePaths.Count} bundles).");
                }
                else
                {
                    Debug.Log($"[TEST Chapter Dump] Extracted {bundlePaths.Count} bundles for {label}.");
                }

                return success;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[TEST Chapter Dump] Exception while extracting {label}: {ex.Message}");
                return false;
            }
        }

        private static IEnumerable<string> GenerateSuffixCandidates(string suffix)
        {
            if (string.IsNullOrEmpty(suffix))
            {
                yield break;
            }

            string lower = suffix.ToLowerInvariant();
            yield return lower;

            int index = lower.Length;
            while ((index = lower.LastIndexOf('_', index - 1)) > 0)
            {
                yield return lower.Substring(0, index);
            }
        }

        private static string GetChapterSuffix(string cid)
        {
            if (string.IsNullOrEmpty(cid))
            {
                return null;
            }

            return cid.StartsWith("CID_", StringComparison.OrdinalIgnoreCase)
                ? cid.Substring(4).ToLowerInvariant()
                : cid.ToLowerInvariant();
        }

        private static string ProjectRootPath()
        {
            string assetsPath = Application.dataPath;
            string projectRoot = Path.GetDirectoryName(assetsPath);
            return string.IsNullOrEmpty(projectRoot) ? assetsPath : projectRoot.Replace("\\", "/");
        }

        private class ChapterRecord
        {
            public string Cid;
            public string Title;
            public string RawField;
            public string RawTerrain;
        }

        private readonly struct GameDataPaths
        {
            public GameDataPaths(string gameDataRoot, string gameDataAssetRoot, string chapterBundlePath, string terrainXmlBundlePath, string terrainDirectory, string disposDirectory)
            {
                GameDataRoot = gameDataRoot;
                GameDataAssetRoot = gameDataAssetRoot;
                ChapterBundlePath = chapterBundlePath;
                TerrainXmlBundlePath = terrainXmlBundlePath;
                TerrainDirectory = terrainDirectory;
                DisposDirectory = disposDirectory;
            }

            public string GameDataRoot { get; }
            public string GameDataAssetRoot { get; }
            public string ChapterBundlePath { get; }
            public string TerrainXmlBundlePath { get; }
            public string TerrainDirectory { get; }
            public string DisposDirectory { get; }
        }

        private readonly struct SupportAsset
        {
            public SupportAsset(string expectedAssetPath, string bundlePath)
            {
                ExpectedAssetPath = expectedAssetPath;
                BundlePath = bundlePath;
            }

            public string ExpectedAssetPath { get; }
            public string BundlePath { get; }
        }

        private bool TryGetGameDataPaths(out GameDataPaths paths)
        {
            string gameBuildPath = NormalizePath(ResolveGameBuildPath());
            if (string.IsNullOrEmpty(gameBuildPath))
            {
                lastMessage = "Game build path not configured. Use Divine Dragon > Settings to locate Engage settings.json.";
                paths = default;
                return false;
            }

            string assetRoot = CombineNormalized(gameBuildPath, "fe_assets_gamedata");
            paths = new GameDataPaths(
                gameBuildPath,
                assetRoot,
                CombineNormalized(assetRoot, "chapter.xml.bundle"),
                CombineNormalized(assetRoot, "terrain.xml.bundle"),
                CombineNormalized(assetRoot, "terrains"),
                CombineNormalized(assetRoot, "dispos")
            );

            return true;
        }

        private static IEnumerable<SupportAsset> EnumerateSupportingAssets(GameDataPaths paths)
        {
            if (string.IsNullOrEmpty(paths.GameDataAssetRoot))
            {
                yield break;
            }

            yield return new SupportAsset(GameDataShareAssetPath + "/Person.xml", CombineNormalized(paths.GameDataAssetRoot, "person.xml.bundle"));
            yield return new SupportAsset(GameDataShareAssetPath + "/Job.xml", CombineNormalized(paths.GameDataAssetRoot, "job.xml.bundle"));
            yield return new SupportAsset(GameDataShareAssetPath + "/Item.xml", CombineNormalized(paths.GameDataAssetRoot, "item.xml.bundle"));
            yield return new SupportAsset(GameDataShareAssetPath + "/Skill.xml", CombineNormalized(paths.GameDataAssetRoot, "skill.xml.bundle"));
            yield return new SupportAsset(TerrainDefinitions.TerrainXmlAssetRelativePath, paths.TerrainXmlBundlePath);
        }

        private static string CombineNormalized(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
            {
                return null;
            }

            return NormalizePath(Path.Combine(left, right));
        }

        private static string NormalizePath(string path)
        {
            return string.IsNullOrEmpty(path) ? path : path.Replace("\\", "/");
        }

        private static string ResolveGameBuildPath()
        {
            const string EngageSettingsTypeName = "Dragonstone.EngageAddressableSettings";

            // Try to access the property via reflection so we don't need a hard reference to the core assembly.
            Type settingsType = Type.GetType(EngageSettingsTypeName);
            if (settingsType == null)
            {
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    settingsType = asm.GetType(EngageSettingsTypeName);
                    if (settingsType != null)
                    {
                        break;
                    }
                }
            }

            if (settingsType == null)
            {
                return null;
            }

            PropertyInfo property = settingsType.GetProperty("GameBuildPath", BindingFlags.Public | BindingFlags.Static);
            if (property == null)
            {
                return null;
            }

            return property.GetValue(null) as string;
        }
    }
}
