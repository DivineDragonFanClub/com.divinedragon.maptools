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

        // Supporting bundles to stage alongside map dumps if not already imported.
        // XML bundles are language-agnostic; patch MSBT bundles are tied to the user's
        // current localization language so DLC chapter titles resolve after dumping.
        private static IEnumerable<SupportAsset> GetSupportingAssets()
        {
            yield return new SupportAsset(MapToolsPaths.GameDataShareAssetPath + "/Person.xml", MapToolsPaths.GameDataAssetRoot + "/person.xml.bundle");
            yield return new SupportAsset(MapToolsPaths.GameDataShareAssetPath + "/Job.xml", MapToolsPaths.GameDataAssetRoot + "/job.xml.bundle");
            yield return new SupportAsset(MapToolsPaths.GameDataShareAssetPath + "/Item.xml", MapToolsPaths.GameDataAssetRoot + "/item.xml.bundle");
            yield return new SupportAsset(MapToolsPaths.GameDataShareAssetPath + "/Skill.xml", MapToolsPaths.GameDataAssetRoot + "/skill.xml.bundle");
            yield return new SupportAsset(TerrainDefinitions.TerrainXmlAssetRelativePath, MapToolsPaths.TerrainXmlBundlePath);

            string lang = TerrainLocalizer.CurrentLanguage;
            if (!string.IsNullOrEmpty(lang) && lang.Length >= 2)
            {
                string country = lang.Substring(0, 2).ToUpperInvariant();
                string langLower = lang.ToLowerInvariant();
                string countryLower = country.ToLowerInvariant();
                for (int patchN = 0; patchN <= 3; patchN++)
                {
                    yield return new SupportAsset(
                        $"Assets/Share/Addressables/Patch/Patch{patchN}/Message/{country}/{lang}/Patch{patchN}.bytes",
                        $"{MapToolsPaths.MessageAssetRoot}/{countryLower}/{langLower}/patch{patchN}.bytes.bundle");
                }
            }
        }

        private readonly List<ChapterRecord> chapters = new List<ChapterRecord>();

        private string chapterAssetPath;
        private string lastMessage;
        private string chapterLoadError;
        private bool isExtracting;
        private Vector2 listScroll;
        private bool isDragChecking;
        private bool dragCheckValue;
        private bool showAdvancedColumns;

        private const float RowHeight = 20f;
        private const float ColumnCheckboxWidth = 22f;
        private const float ColumnDumpedWidth = 64f;
        private const float ColumnCidWidth = 110f;
        private const float ColumnSceneWidth = 160f;
        private const float ColumnTerrainWidth = 180f;
        private const int ExtractionBatchSize = 10;
        private const string SkipBulkWarningPrefsKey = "DivineDragon.MapTools.ChapterDumper.SkipBulkWarning";
        private const string ShowAdvancedColumnsPrefsKey = "DivineDragon.MapTools.ChapterDumper.ShowAdvancedColumns";
        private const string AddressablesRootFolder = "Assets/Share/Addressables";

        [MenuItem("Window/Map Tools/Chapter Dumper")]
        public static void ShowWindow()
        {
            var window = GetWindow<ChapterDumperWindow>(WindowTitle);
            window.minSize = new Vector2(620f, 260f);
        }

        private void OnEnable()
        {
            showAdvancedColumns = EditorPrefs.GetBool(ShowAdvancedColumnsPrefsKey, false);
            RefreshChapterLocation();
            TerrainLocalizer.OnLanguageChanged += OnLanguageChanged;
            EditorApplication.projectChanged += OnProjectChanged;
        }

        private void OnDisable()
        {
            TerrainLocalizer.OnLanguageChanged -= OnLanguageChanged;
            EditorApplication.projectChanged -= OnProjectChanged;
        }

        private void OnProjectChanged()
        {
            TerrainLocalizer.InvalidateCache();
            RefreshTitles();
            RefreshDumpStatus();
            Repaint();
        }

        private void RefreshTitles()
        {
            for (int i = 0; i < chapters.Count; i++)
            {
                string newTitle = GetLocalizedChapterTitle(chapters[i].Cid);
                if (!string.IsNullOrEmpty(newTitle))
                {
                    chapters[i].Title = newTitle;
                }
            }
        }

        private void OnLanguageChanged()
        {
            // Reload chapter data to update localized titles
            LoadChapterData();
            Repaint();
        }

        private void OnGUI()
        {
            EventType evt = Event.current.type;
            if (evt == EventType.MouseUp || evt == EventType.MouseLeaveWindow || evt == EventType.Ignore)
            {
                isDragChecking = false;
            }

            if (!MapToolsPaths.IsConfigured)
            {
                EditorGUILayout.HelpBox(
                    "Game data path not configured.\n\nGo to Project Settings → Divine Dragon and set the path to settings.json from your game dump.",
                    MessageType.Error);
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Chapter XML Status", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(EditorGUIUtility.IconContent("_Popup"), GUILayout.Width(24), GUILayout.Height(20)))
                {
                    ShowSettingsMenu();
                }
            }

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

            int checkedCount = 0;
            for (int i = 0; i < chapters.Count; i++)
            {
                if (chapters[i].Checked) checkedCount++;
            }

            using (new EditorGUI.DisabledScope(isExtracting || checkedCount == 0))
            {
                string label = checkedCount == 0
                    ? "Dump Checked Chapters"
                    : $"Dump Checked Chapters ({checkedCount})";
                if (GUILayout.Button(label, GUILayout.Height(28f)))
                {
                    DumpCheckedChapters();
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
                DrawRow(chapters[i]);
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawTableHeader()
        {
            int checkedCount = 0;
            for (int i = 0; i < chapters.Count; i++)
            {
                if (chapters[i].Checked) checkedCount++;
            }
            bool allChecked = checkedCount == chapters.Count && chapters.Count > 0;
            bool isMixed = checkedCount > 0 && !allChecked;

            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                EditorGUI.BeginChangeCheck();
                EditorGUI.showMixedValue = isMixed;
                bool toggled = GUILayout.Toggle(allChecked, GUIContent.none, GUILayout.Width(ColumnCheckboxWidth));
                EditorGUI.showMixedValue = false;
                if (EditorGUI.EndChangeCheck())
                {
                    for (int i = 0; i < chapters.Count; i++)
                    {
                        chapters[i].Checked = toggled;
                    }
                }

                GUILayout.Label("Dumped", EditorStyles.boldLabel, GUILayout.Width(ColumnDumpedWidth));
                GUILayout.Label("CID", EditorStyles.boldLabel, GUILayout.Width(ColumnCidWidth));
                GUILayout.Label("Title", EditorStyles.boldLabel);
                if (showAdvancedColumns)
                {
                    GUILayout.Label("Scene Bundle", EditorStyles.boldLabel, GUILayout.Width(ColumnSceneWidth));
                    GUILayout.Label("Terrain Bundle", EditorStyles.boldLabel, GUILayout.Width(ColumnTerrainWidth));
                }
            }
        }

        private void DrawRow(ChapterRecord chapter)
        {
            Rect rowRect = GUILayoutUtility.GetRect(0f, RowHeight, GUILayout.ExpandWidth(true));

            if (rowRect.Contains(Event.current.mousePosition))
            {
                EditorGUI.DrawRect(rowRect, new Color(1f, 1f, 1f, 0.07f));
            }

            float x = rowRect.x + 4f;
            float y = rowRect.y + 2f;
            float height = rowRect.height - 4f;
            float titleWidth = rowRect.width - ColumnCheckboxWidth - ColumnDumpedWidth - ColumnCidWidth - 20f;
            if (showAdvancedColumns) titleWidth -= ColumnSceneWidth + ColumnTerrainWidth;
            if (titleWidth < 60f)
            {
                titleWidth = 60f;
            }

            GUIStyle labelStyle = EditorStyles.label;

            Rect checkboxRect = new Rect(x, y, ColumnCheckboxWidth, height);
            HandleCheckboxDragPaint(checkboxRect, chapter);
            EditorGUI.Toggle(checkboxRect, chapter.Checked);
            x += ColumnCheckboxWidth;

            // Whole-row click target for dumped chapters (everything to the right of the checkbox).
            if (chapter.IsDumped)
            {
                Rect rowClickRect = new Rect(rowRect.x + ColumnCheckboxWidth, rowRect.y, rowRect.width - ColumnCheckboxWidth, rowRect.height);
                EditorGUIUtility.AddCursorRect(rowClickRect, MouseCursor.Link);

                if (Event.current.type == EventType.MouseDown &&
                    Event.current.button == 0 &&
                    rowClickRect.Contains(Event.current.mousePosition))
                {
                    string targetPath = !string.IsNullOrEmpty(chapter.SceneAtPath) ? chapter.SceneAtPath : chapter.DumpedAtPath;
                    var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(targetPath);
                    if (asset != null)
                    {
                        EditorGUIUtility.PingObject(asset);
                        Selection.activeObject = asset;
                    }
                    Event.current.Use();
                }

                Color prev = GUI.color;
                GUI.color = new Color(0.4f, 0.85f, 0.4f);
                GUI.Label(new Rect(x, y, ColumnDumpedWidth, height), "✓", labelStyle);
                GUI.color = prev;
            }
            x += ColumnDumpedWidth;

            GUI.Label(new Rect(x, y, ColumnCidWidth - 4f, height), chapter.Cid ?? "", labelStyle);
            x += ColumnCidWidth;

            GUI.Label(new Rect(x, y, titleWidth, height), chapter.Title ?? string.Empty, labelStyle);
            x += titleWidth + 4f;

            if (showAdvancedColumns)
            {
                GUI.Label(new Rect(x, y, ColumnSceneWidth, height), chapter.DisplaySceneBundle ?? string.Empty, labelStyle);
                x += ColumnSceneWidth;

                GUI.Label(new Rect(x, y, ColumnTerrainWidth, height), chapter.DisplayTerrainBundle ?? string.Empty, labelStyle);
            }
        }

        private void RefreshDumpStatus()
        {
            Dictionary<string, string> dumpedTerrains = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string projectRoot = ProjectRootPath();
            string addressablesRoot = Path.Combine(projectRoot, AddressablesRootFolder).Replace("\\", "/");
            if (Directory.Exists(addressablesRoot))
            {
                foreach (string file in Directory.EnumerateFiles(addressablesRoot, "MapTerrain_*.asset", SearchOption.AllDirectories))
                {
                    string normalized = file.Replace("\\", "/");
                    string projectRelative = normalized.StartsWith(projectRoot + "/")
                        ? normalized.Substring(projectRoot.Length + 1)
                        : normalized;
                    string fileName = Path.GetFileName(file);
                    if (!dumpedTerrains.ContainsKey(fileName))
                    {
                        dumpedTerrains[fileName] = projectRelative;
                    }
                }
            }

            // Reverse index: which scene references each MapTerrain asset.
            Dictionary<string, string> terrainToScene = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string sceneGuid in AssetDatabase.FindAssets("t:SceneAsset"))
            {
                string scenePath = AssetDatabase.GUIDToAssetPath(sceneGuid);
                if (string.IsNullOrEmpty(scenePath)) continue;
                foreach (string dep in AssetDatabase.GetDependencies(scenePath, recursive: false))
                {
                    if (dep.EndsWith(".asset", StringComparison.OrdinalIgnoreCase) &&
                        Path.GetFileName(dep).StartsWith("MapTerrain_", StringComparison.OrdinalIgnoreCase) &&
                        !terrainToScene.ContainsKey(dep))
                    {
                        terrainToScene[dep] = scenePath;
                    }
                }
            }

            for (int i = 0; i < chapters.Count; i++)
            {
                ChapterRecord chapter = chapters[i];
                chapter.IsDumped = false;
                chapter.DumpedAtPath = null;
                chapter.SceneAtPath = null;
                string suffix = GetChapterSuffix(chapter.Cid);
                if (string.IsNullOrEmpty(suffix)) continue;
                foreach (string candidate in GenerateSuffixCandidates(suffix))
                {
                    if (dumpedTerrains.TryGetValue($"MapTerrain_{candidate}.asset", out string path))
                    {
                        chapter.IsDumped = true;
                        chapter.DumpedAtPath = path;
                        terrainToScene.TryGetValue(path, out string scenePath);
                        chapter.SceneAtPath = scenePath;
                        break;
                    }
                }
            }
        }

        private void ShowSettingsMenu()
        {
            var menu = new GenericMenu();

            bool isSuppressed = EditorPrefs.GetBool(SkipBulkWarningPrefsKey, false);
            menu.AddItem(
                new GUIContent("Suppress bulk dump warning"),
                isSuppressed,
                () =>
                {
                    if (isSuppressed)
                    {
                        EditorPrefs.DeleteKey(SkipBulkWarningPrefsKey);
                    }
                    else
                    {
                        EditorPrefs.SetBool(SkipBulkWarningPrefsKey, true);
                    }
                });

            menu.AddItem(
                new GUIContent("Show advanced columns"),
                showAdvancedColumns,
                () =>
                {
                    showAdvancedColumns = !showAdvancedColumns;
                    EditorPrefs.SetBool(ShowAdvancedColumnsPrefsKey, showAdvancedColumns);
                    Repaint();
                });

            menu.ShowAsContext();
        }

        private void HandleCheckboxDragPaint(Rect checkboxRect, ChapterRecord chapter)
        {
            Event e = Event.current;
            if (e.button != 0) return;

            bool inside = checkboxRect.Contains(e.mousePosition);

            if (e.type == EventType.MouseDown && inside)
            {
                chapter.Checked = !chapter.Checked;
                isDragChecking = true;
                dragCheckValue = chapter.Checked;
                e.Use();
                Repaint();
            }
            else if (e.type == EventType.MouseDrag && isDragChecking && inside)
            {
                if (chapter.Checked != dragCheckValue)
                {
                    chapter.Checked = dragCheckValue;
                    Repaint();
                }
            }
        }

        private void DumpCheckedChapters()
        {
            int totalChecked = 0;
            for (int i = 0; i < chapters.Count; i++)
            {
                if (chapters[i].Checked) totalChecked++;
            }

            if (totalChecked == 0)
            {
                lastMessage = "No chapters checked.";
                return;
            }

            if (totalChecked > 1 && !EditorPrefs.GetBool(SkipBulkWarningPrefsKey, false))
            {
                int choice = EditorUtility.DisplayDialogComplex(
                    "Dump Multiple Chapters",
                    $"You're about to dump {totalChecked} chapters. This may take a while.\n\nContinue?",
                    "Continue",
                    "Cancel",
                    "Continue (don't ask again)");

                if (choice == 1) return;
                if (choice == 2) EditorPrefs.SetBool(SkipBulkWarningPrefsKey, true);
            }

            HashSet<string> queuedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> warnings = new List<string>();
            List<string> batchTargets = new List<string>();
            List<string> batchChapterLabels = new List<string>();

            int sceneFound = 0, sceneMissing = 0;
            int terrainFound = 0, terrainMissing = 0;
            int chaptersWithAnyTargets = 0;
            bool overallSuccess = true;

            isExtracting = true;
            try
            {
                List<string> supportingTargets = new List<string>();
                EnsureSupportingAssets(supportingTargets, queuedPaths);

                if (supportingTargets.Count > 0)
                {
                    EditorUtility.DisplayProgressBar(WindowTitle, $"Extracting {supportingTargets.Count} supporting bundles...", 0.5f);
                    if (!RunExtractionBatch(supportingTargets, "[supporting XMLs]"))
                    {
                        overallSuccess = false;
                    }
                }

                int processedChecked = 0;
                for (int i = 0; i < chapters.Count; i++)
                {
                    ChapterRecord chapter = chapters[i];
                    if (!chapter.Checked) continue;

                    processedChecked++;
                    float progress = (float)processedChecked / totalChecked;
                    EditorUtility.DisplayProgressBar(WindowTitle, $"Queueing bundles ({processedChecked}/{totalChecked})...", progress);

                    int beforeCount = batchTargets.Count;
                    bool chapterHasBundle = false;

                    if (TryAddSceneBundle(chapter, batchTargets, queuedPaths))
                    {
                        sceneFound++;
                        chapterHasBundle = true;
                    }
                    else
                    {
                        sceneMissing++;
                        warnings.Add($"Scene bundle missing for {chapter.Cid}.");
                    }

                    if (TryAddTerrainBundle(chapter, batchTargets, queuedPaths))
                    {
                        terrainFound++;
                        chapterHasBundle = true;
                    }
                    else
                    {
                        terrainMissing++;
                        warnings.Add($"Terrain bundle missing for {chapter.Cid}.");
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
                }

                string statusMessage =
                    $"Queued {queuedPaths.Count} unique bundles across {chaptersWithAnyTargets} chapters.\n" +
                    $"Scenes: {sceneFound} found, {sceneMissing} missing. Terrains: {terrainFound} found, {terrainMissing} missing.";

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
                Debug.LogWarning("[Chapter Dumper] One or more batches reported issues. See the window message for details.");
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
                    Debug.LogWarning($"[Chapter Dumper] Batch extraction reported failures for {label} ({bundlePaths.Count} bundles).");
                }
                else
                {
                    Debug.Log($"[Chapter Dumper] Extracted {bundlePaths.Count} bundles for {label}.");
                }

                return success;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Chapter Dumper] Exception while extracting {label}: {ex.Message}");
                return false;
            }
        }

        private void EnsureSupportingAssets(List<string> extractionTargets, HashSet<string> queued = null)
        {
            foreach (var support in GetSupportingAssets())
            {
                string projectPath = Path.Combine(ProjectRootPath(), support.ExpectedAssetPath).Replace("\\", "/");
                if (!File.Exists(projectPath) && File.Exists(support.BundlePath))
                {
                    if (queued == null || queued.Add(support.BundlePath))
                    {
                        extractionTargets.Add(support.BundlePath);
                    }
                }
            }
        }

        private bool TryAddSceneBundle(ChapterRecord chapter, List<string> extractionTargets, HashSet<string> queued = null)
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
                    if (queued == null || queued.Add(bundlePath))
                    {
                        extractionTargets.Add(bundlePath);
                    }
                    chapter.ResolvedSceneBundle = Path.GetFileName(bundlePath);
                    chapter.DisplaySceneBundle = chapter.ResolvedSceneBundle;
                    return true;
                }
            }
            return false;
        }

        private bool TryAddTerrainBundle(ChapterRecord chapter, List<string> extractionTargets, HashSet<string> queued = null)
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
                    if (queued == null || queued.Add(bundlePath))
                    {
                        extractionTargets.Add(bundlePath);
                    }
                    chapter.ResolvedTerrainBundle = Path.GetFileName(bundlePath);
                    chapter.DisplayTerrainBundle = chapter.ResolvedTerrainBundle;
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
                    string cid = (string)param.Attribute("Cid");
                    ChapterRecord record = new ChapterRecord
                    {
                        Cid = cid,
                        Title = GetLocalizedChapterTitle(cid) ?? (string)param.Attribute("Help"),
                        RawField = (string)param.Attribute("Field"),
                        RawTerrain = (string)param.Attribute("Terrain")
                    };

                    PopulateDisplayIds(record);
                    chapters.Add(record);
                }

                RefreshDumpStatus();
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
            }
            else
            {
                record.DisplaySceneBundle = record.RawField;
                record.DisplayTerrainBundle = record.RawTerrain;
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

        /// <summary>
        /// Convert CID to MCID and look up localized chapter title.
        /// Returns "PREFIX: TITLE" (e.g., "Chapter 1: Awake at Last") or null if not found.
        /// </summary>
        private static string GetLocalizedChapterTitle(string cid)
        {
            if (string.IsNullOrEmpty(cid))
            {
                return null;
            }

            // Convert CID_M001 to MCID_M001
            string mcidBase = cid.StartsWith("CID_", StringComparison.OrdinalIgnoreCase)
                ? "MCID_" + cid.Substring(4)
                : "MCID_" + cid;

            // Look up prefix (e.g., "Chapter 1") and title (e.g., "Awake at Last")
            string prefix = TerrainLocalizer.GetLocalizedName(mcidBase + "_PREFIX");
            string title = TerrainLocalizer.GetLocalizedName(mcidBase);

            // If we got back the key itself, localization wasn't found
            bool hasPrefix = !string.IsNullOrEmpty(prefix) && prefix != mcidBase + "_PREFIX";
            bool hasTitle = !string.IsNullOrEmpty(title) && title != mcidBase;

            if (hasPrefix && hasTitle)
            {
                return $"{prefix}: {title}";
            }
            if (hasTitle)
            {
                return title;
            }
            if (hasPrefix)
            {
                return prefix;
            }

            return null;
        }

        private class ChapterRecord
        {
            public string Cid;
            public string Title;
            public string RawField;
            public string RawTerrain;

            public string DisplaySceneBundle;
            public string DisplayTerrainBundle;
            public string ResolvedSceneBundle;
            public string ResolvedTerrainBundle;

            public bool Checked;
            public bool IsDumped;
            public string DumpedAtPath;
            public string SceneAtPath;
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
