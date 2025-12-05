using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using UnityEditor;
using UnityEngine;
using DivineDragon;

namespace DivineDragon.MapTools
{
    public class ChapterDumperWindow : EditorWindow
    {
        private const string WindowTitle = "Chapter Dumper";

        // Supporting XML bundles to stage alongside map dumps if not already imported
        private static SupportAsset[] GetSupportingAssets() => new[]
        {
            new SupportAsset(MapToolsPaths.GameDataShareAssetPath + "/Person.xml", MapToolsPaths.GameDataAssetRoot + "/person.xml.bundle"),
            new SupportAsset(MapToolsPaths.GameDataShareAssetPath + "/Job.xml", MapToolsPaths.GameDataAssetRoot + "/job.xml.bundle"),
            new SupportAsset(MapToolsPaths.GameDataShareAssetPath + "/Item.xml", MapToolsPaths.GameDataAssetRoot + "/item.xml.bundle"),
            new SupportAsset(MapToolsPaths.GameDataShareAssetPath + "/Skill.xml", MapToolsPaths.GameDataAssetRoot + "/skill.xml.bundle"),
            new SupportAsset(TerrainDefinitions.TerrainXmlAssetRelativePath, MapToolsPaths.TerrainXmlBundlePath)
        };

        private readonly List<ChapterRecord> chapters = new List<ChapterRecord>();

        private string chapterAssetPath;
        private string lastMessage;
        private string chapterLoadError;
        private bool isExtracting;
        private Vector2 listScroll;
        private int selectedIndex = -1;

        private const float RowHeight = 20f;
        private const float ColumnCidWidth = 110f;
        private const float ColumnSceneWidth = 160f;
        private const float ColumnTerrainWidth = 180f;
        private const float ColumnDisposWidth = 140f;

        [MenuItem("Window/Map Tools/Chapter Dumper")]
        public static void ShowWindow()
        {
            var window = GetWindow<ChapterDumperWindow>(WindowTitle);
            window.minSize = new Vector2(620f, 260f);
        }

        private void OnEnable()
        {
            RefreshChapterLocation();
        }

        private void OnGUI()
        {
            if (!MapToolsPaths.IsConfigured)
            {
                EditorGUILayout.HelpBox(
                    "Game data path not configured.\n\nGo to Project Settings → Divine Dragon and set the path to settings.json from your game dump.",
                    MessageType.Error);
                return;
            }

            EditorGUILayout.LabelField("Chapter XML Status", EditorStyles.boldLabel);

            if (!string.IsNullOrEmpty(chapterAssetPath))
            {
                EditorGUILayout.HelpBox($"Found at {chapterAssetPath}", MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox("Chapter.xml not found under Assets. Click the button below to extract it.", MessageType.Warning);
            }

            using (new EditorGUI.DisabledScope(isExtracting || !string.IsNullOrEmpty(chapterAssetPath)))
            {
                if (GUILayout.Button("Extract Chapter.xml"))
                {
                    ExtractChapterXml();
                }
            }

            EditorGUILayout.Space();

            using (new EditorGUI.DisabledScope(selectedIndex < 0 || selectedIndex >= chapters.Count))
            {
                if (GUILayout.Button("Dump Selected Chapter"))
                {
                    DumpSelectedChapter();
                }
            }

            if (!string.IsNullOrEmpty(lastMessage))
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(lastMessage, MessageType.None);
            }

            if (!string.IsNullOrEmpty(chapterLoadError))
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(chapterLoadError, MessageType.Warning);
                return;
            }

            if (chapters.Count == 0 && !string.IsNullOrEmpty(chapterAssetPath))
            {
                LoadChapterData();
            }

            if (chapters.Count == 0)
            {
                return;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField($"Chapters ({chapters.Count})", EditorStyles.boldLabel);

            DrawTableHeader();

            listScroll = EditorGUILayout.BeginScrollView(listScroll);
            for (int i = 0; i < chapters.Count; i++)
            {
                DrawRow(chapters[i], i);
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawTableHeader()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                GUILayout.Label("CID", EditorStyles.boldLabel, GUILayout.Width(ColumnCidWidth));
                GUILayout.Label("Title", EditorStyles.boldLabel);
                GUILayout.Label("Scene Bundle", EditorStyles.boldLabel, GUILayout.Width(ColumnSceneWidth));
                GUILayout.Label("Terrain Bundle", EditorStyles.boldLabel, GUILayout.Width(ColumnTerrainWidth));
                GUILayout.Label("Dispos Bundle", EditorStyles.boldLabel, GUILayout.Width(ColumnDisposWidth));
            }
        }

        private void DrawRow(ChapterRecord chapter, int index)
        {
            Rect rowRect = GUILayoutUtility.GetRect(0f, RowHeight, GUILayout.ExpandWidth(true));
            bool isSelected = index == selectedIndex;

            if (Event.current.type == EventType.MouseDown && rowRect.Contains(Event.current.mousePosition))
            {
                selectedIndex = index;
                GUI.FocusControl(null);
                Repaint();
            }

            if (isSelected)
            {
                EditorGUI.DrawRect(rowRect, new Color(0.24f, 0.36f, 0.64f, 0.25f));
            }
            else if (rowRect.Contains(Event.current.mousePosition))
            {
                EditorGUI.DrawRect(rowRect, new Color(1f, 1f, 1f, 0.07f));
            }

            float x = rowRect.x + 4f;
            float y = rowRect.y + 2f;
            float height = rowRect.height - 4f;
            float titleWidth = rowRect.width - ColumnCidWidth - ColumnSceneWidth - ColumnTerrainWidth - ColumnDisposWidth - 20f;
            if (titleWidth < 60f)
            {
                titleWidth = 60f;
            }

            GUIStyle labelStyle = EditorStyles.label;

            GUI.Label(new Rect(x, y, ColumnCidWidth - 4f, height), chapter.Cid ?? "", labelStyle);
            x += ColumnCidWidth;

            GUI.Label(new Rect(x, y, titleWidth, height), chapter.Title ?? string.Empty, labelStyle);
            x += titleWidth + 4f;

            GUI.Label(new Rect(x, y, ColumnSceneWidth, height), chapter.DisplaySceneBundle ?? string.Empty, labelStyle);
            x += ColumnSceneWidth;

            GUI.Label(new Rect(x, y, ColumnTerrainWidth, height), chapter.DisplayTerrainBundle ?? string.Empty, labelStyle);
            x += ColumnTerrainWidth;

            GUI.Label(new Rect(x, y, ColumnDisposWidth, height), chapter.DisplayDisposBundle ?? string.Empty, labelStyle);
        }

        private void DumpSelectedChapter()
        {
            if (selectedIndex < 0 || selectedIndex >= chapters.Count)
            {
                return;
            }

            ChapterRecord chapter = chapters[selectedIndex];
            List<string> extractionTargets = new List<string>();
            List<string> warnings = new List<string>();

            EnsureSupportingAssets(extractionTargets);

            if (!TryAddSceneBundle(chapter, extractionTargets))
            {
                warnings.Add($"Scene bundle not found for {chapter.Cid}.");
            }

            if (!TryAddTerrainBundle(chapter, extractionTargets))
            {
                warnings.Add($"Terrain bundle not found for {chapter.Cid}.");
            }

            if (!TryAddDisposBundle(chapter, extractionTargets))
            {
                warnings.Add($"Dispos bundle not found for {chapter.Cid}.");
            }

            if (extractionTargets.Count == 0)
            {
                lastMessage = "No bundles queued for extraction.";
                return;
            }

            bool success = Dumper.ExtractMultipleAssets(extractionTargets);
            AssetDatabase.Refresh();

            if (success && warnings.Count == 0)
            {
                lastMessage = $"Extraction completed for {chapter.Cid}.";
            }
            else
            {
                string status = success ? "Extraction finished with warnings." : "Extraction reported failures.";
                if (warnings.Count > 0)
                {
                    status += "\n" + string.Join("\n", warnings);
                }
                lastMessage = status;
            }
        }

        private void EnsureSupportingAssets(List<string> extractionTargets)
        {
            foreach (var support in GetSupportingAssets())
            {
                string projectPath = Path.Combine(ProjectRootPath(), support.ExpectedAssetPath).Replace("\\", "/");
                if (!File.Exists(projectPath) && File.Exists(support.BundlePath))
                {
                    extractionTargets.Add(support.BundlePath);
                }
            }
        }

        private bool TryAddSceneBundle(ChapterRecord chapter, List<string> extractionTargets)
        {
            string suffix = GetChapterSuffix(chapter.Cid);
            if (string.IsNullOrEmpty(suffix))
            {
                return false;
            }

            foreach (string candidate in GenerateSuffixCandidates(suffix))
            {
                string bundlePath = $"{MapToolsPaths.GameBuildPath}/fe_scenes_fld_{candidate}.bundle";
                if (File.Exists(bundlePath))
                {
                    extractionTargets.Add(bundlePath);
                    chapter.ResolvedSceneBundle = Path.GetFileName(bundlePath);
                    chapter.DisplaySceneBundle = chapter.ResolvedSceneBundle;
                    return true;
                }
            }
            return false;
        }

        private bool TryAddTerrainBundle(ChapterRecord chapter, List<string> extractionTargets)
        {
            string suffix = GetChapterSuffix(chapter.Cid);
            if (string.IsNullOrEmpty(suffix))
            {
                return false;
            }

            foreach (string candidate in GenerateSuffixCandidates(suffix))
            {
                string bundlePath = $"{MapToolsPaths.TerrainDirectory}/mapterrain_{candidate}.bundle";
                if (File.Exists(bundlePath))
                {
                    extractionTargets.Add(bundlePath);
                    chapter.ResolvedTerrainBundle = Path.GetFileName(bundlePath);
                    chapter.DisplayTerrainBundle = chapter.ResolvedTerrainBundle;
                    return true;
                }
            }
            return false;
        }

        private bool TryAddDisposBundle(ChapterRecord chapter, List<string> extractionTargets)
        {
            string suffix = GetChapterSuffix(chapter.Cid);
            if (string.IsNullOrEmpty(suffix))
            {
                return false;
            }

            foreach (string candidate in GenerateSuffixCandidates(suffix))
            {
                string bundlePath = $"{MapToolsPaths.DisposDirectory}/{candidate}.xml.bundle";
                if (File.Exists(bundlePath))
                {
                    extractionTargets.Add(bundlePath);
                    chapter.ResolvedDisposBundle = Path.GetFileName(bundlePath);
                    chapter.DisplayDisposBundle = chapter.ResolvedDisposBundle;
                    return true;
                }
            }
            return false;
        }

        private void ExtractChapterXml()
        {
            if (!File.Exists(MapToolsPaths.ChapterBundlePath))
            {
                lastMessage = $"Bundle not found: {MapToolsPaths.ChapterBundlePath}";
                return;
            }

            try
            {
                isExtracting = true;
                EditorUtility.DisplayProgressBar(WindowTitle, "Extracting Chapter.xml...", 0.5f);

                bool success = Dumper.ExtractAssetAtPath(MapToolsPaths.ChapterBundlePath);
                AssetDatabase.Refresh();
                RefreshChapterLocation();

                lastMessage = success ? "Extraction completed." : "Extraction failed. Check console for details.";
            }
            catch (Exception ex)
            {
                lastMessage = $"Extraction error: {ex.Message}";
            }
            finally
            {
                isExtracting = false;
                EditorUtility.ClearProgressBar();
            }
        }

        private void RefreshChapterLocation()
        {
            string previous = chapterAssetPath;
            chapterAssetPath = null;
            string projectAssetsPath = Application.dataPath.Replace("\\", "/");

            try
            {
                if (!string.IsNullOrEmpty(MapToolsPaths.ChapterAssetPath))
                {
                    string candidateFullPath = Path.Combine(ProjectRootPath(), MapToolsPaths.ChapterAssetPath).Replace("\\", "/");
                    if (File.Exists(candidateFullPath))
                    {
                        chapterAssetPath = MapToolsPaths.ChapterAssetPath;
                    }
                }

                if (string.IsNullOrEmpty(chapterAssetPath) && Directory.Exists(projectAssetsPath))
                {
                    foreach (var file in Directory.EnumerateFiles(projectAssetsPath, "Chapter.xml", SearchOption.AllDirectories))
                    {
                        chapterAssetPath = "Assets" + file.Substring(projectAssetsPath.Length).Replace("\\", "/");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                chapterLoadError = $"Failed to scan Assets: {ex.Message}";
                return;
            }

            if (chapterAssetPath != previous)
            {
                LoadChapterData();
            }
        }

        private void LoadChapterData()
        {
            chapters.Clear();
            chapterLoadError = null;
            selectedIndex = -1;

            if (string.IsNullOrEmpty(chapterAssetPath))
            {
                return;
            }

            try
            {
                string fullPath = Path.Combine(ProjectRootPath(), chapterAssetPath).Replace("\\", "/");

                if (!File.Exists(fullPath))
                {
                    chapterLoadError = $"Chapter.xml missing at expected path: {fullPath}";
                    return;
                }

                XDocument doc = XDocument.Load(fullPath);
                XElement sheet = doc.Root?
                    .Elements("Sheet")
                    .FirstOrDefault(e => string.Equals((string)e.Attribute("Name"), "チャプター", StringComparison.Ordinal));

                if (sheet == null)
                {
                    chapterLoadError = "Chapter sheet not found in XML.";
                    return;
                }

                XElement dataNode = sheet.Element("Data");
                if (dataNode == null)
                {
                    chapterLoadError = "No Data node found in Chapter sheet.";
                    return;
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

                    PopulateDisplayIds(record);
                    chapters.Add(record);
                }

            }
            catch (Exception ex)
            {
                chapterLoadError = $"Failed to parse Chapter.xml: {ex.Message}";
            }
        }

        private void PopulateDisplayIds(ChapterRecord record)
        {
            string suffix = GetChapterSuffix(record.Cid);
            if (!string.IsNullOrEmpty(suffix))
            {
                string displayScene = null;
                foreach (string candidate in GenerateSuffixCandidates(suffix))
                {
                    string fileName = $"fe_scenes_fld_{candidate}.bundle";
                    string fullPath = Path.Combine(MapToolsPaths.GameBuildPath, fileName).Replace("\\", "/");
                    if (File.Exists(fullPath))
                    {
                        displayScene = fileName;
                        break;
                    }
                }

                string displayTerrain = null;
                foreach (string candidate in GenerateSuffixCandidates(suffix))
                {
                    string fileName = $"mapterrain_{candidate}.bundle";
                    string fullPath = Path.Combine(MapToolsPaths.TerrainDirectory, fileName).Replace("\\", "/");
                    if (File.Exists(fullPath))
                    {
                        displayTerrain = fileName;
                        break;
                    }
                }

                string displayDispos = null;
                foreach (string candidate in GenerateSuffixCandidates(suffix))
                {
                    string fileName = $"{candidate}.xml.bundle";
                    string fullPath = Path.Combine(MapToolsPaths.DisposDirectory, fileName).Replace("\\", "/");
                    if (File.Exists(fullPath))
                    {
                        displayDispos = fileName;
                        break;
                    }
                }

                record.DisplaySceneBundle = !string.IsNullOrEmpty(displayScene)
                    ? displayScene
                    : (!string.IsNullOrEmpty(record.RawField) && record.RawField != "*"
                        ? record.RawField
                        : null);

                record.DisplayTerrainBundle = !string.IsNullOrEmpty(displayTerrain)
                    ? displayTerrain
                    : (!string.IsNullOrEmpty(record.RawTerrain) && record.RawTerrain != "*"
                        ? record.RawTerrain
                        : null);

                record.DisplayDisposBundle = displayDispos;
            }
            else
            {
                record.DisplaySceneBundle = record.RawField;
                record.DisplayTerrainBundle = record.RawTerrain;
                record.DisplayDisposBundle = null;
            }
        }

        private static IEnumerable<string> GenerateSuffixCandidates(string suffix)
        {
            if (string.IsNullOrEmpty(suffix)) yield break;

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

            public string DisplaySceneBundle;
            public string DisplayTerrainBundle;
            public string DisplayDisposBundle;
            public string ResolvedSceneBundle;
            public string ResolvedTerrainBundle;
            public string ResolvedDisposBundle;
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
    }
}
