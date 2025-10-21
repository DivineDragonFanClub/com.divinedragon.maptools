using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEditor;

namespace DivineDragon.MapTools
{
    public class DisposToolWindow : EditorWindow
    {
        private static DisposToolWindow instance;
        public static DisposToolWindow Instance => instance;
        // Ensure a backing instance exists so split windows can delegate rendering/state.
        public static DisposToolWindow EnsureMainWindow()
        {
            if (instance == null)
            {
                var window = GetWindow<DisposToolWindow>("Dispos Tool");
                window.minSize = new Vector2(260, 240);
            }
            return instance;
        }

        // Repaint helper so split windows update immediately on selection/edits
        public static void RequestRepaintAll(bool repaintScene = false)
        {
            try
            {
                instance?.Repaint();
                foreach (var w in Resources.FindObjectsOfTypeAll<DisposGroupWindow>()) w.Repaint();
                foreach (var w in Resources.FindObjectsOfTypeAll<DisposUnitInspectorWindow>()) w.Repaint();
                // Settings window removed; actions live in main window
                if (repaintScene) SceneView.RepaintAll();
            }
            catch { /* ignore if called during domain reload */ }
        }
        
        private DisposDocument currentDocument;
        private DisposSceneRenderer sceneRenderer;
        private DisposEntry selectedEntry;
        private TerrainAssetAdapter selectedTerrain;
        private DisposUndoProxy undoProxy;
        
        private Vector2 leftPanelScroll;
        private Vector2 rightPanelScroll;

        // UI state for creating groups
        private string newGroupName = "";
        
        private string[] availableFiles;
        private int selectedFileIndex = -1;
        private string disposFolderPath = "Assets/Share/Addressables/GameData/Dispos";
        
        private bool showGrid = true;
        private bool showLabels = true;
        private bool showDirections = true;
        private bool showIcons = true;
        private bool showSimplifiedNames = true;
        
        private bool isDraggingUnit = false;
        private DisposEntry draggedEntry = null;
        private bool documentIsDirty = false;
        // Empty-tile selection state (for placing new units)
        private bool hasSelectedTile = false;
        private Vector2Int selectedTile;
        
        // Difficulty filter toggles
        private bool filterNormal = true;
        private bool filterHard = true;
        private bool filterLunatic = true;

        // Quick Picker state
        private bool pickerOpen = false;
        private List<DisposEntry> pickerEntries = new List<DisposEntry>();
        private Rect pickerRect;
        private int pickerHoverIndex = -1;
        private Vector2Int pickerTile;
        private DisposEntry pickerHighlightEntry;
        // Hover state from Groups panel to manage scene hover highlight
        private bool groupsPanelAnyHover = false;
        // Smooth pan state for SceneView (pivot-only, keeps zoom)
        private bool isPanningScene = false;
        private Vector3 panStartPivot;
        private Vector3 panTargetPivot;
        private double panStartTime;
        private float panDuration = 0.3f;

        // Undo grouping for drag-move (single step per drag)
        private bool dragUndoActive = false;
        private int dragUndoGroup = -1;
        private bool dragUndoRecorded = false;

        private enum DisposOverlayMode { Hide, Show, Edit }
        private DisposOverlayMode overlayMode = DisposOverlayMode.Edit;
        private SharedEditorMode? previousSharedMode = null;

        private void RefreshSharedEditorModeLock()
        {
            var currentShared = TerrainPaintToolWindow.GetSharedEditorMode();
            if (overlayMode == DisposOverlayMode.Edit)
            {
                if (!previousSharedMode.HasValue && currentShared != SharedEditorMode.Dispos)
                {
                    previousSharedMode = currentShared;
                }
                if (currentShared != SharedEditorMode.Dispos)
                {
                    TerrainPaintToolWindow.SetSharedEditorMode(SharedEditorMode.Dispos);
                }
            }
            else
            {
                ReleaseSharedEditorModeLock();
            }
        }

        private void ReleaseSharedEditorModeLock()
        {
            var currentShared = TerrainPaintToolWindow.GetSharedEditorMode();
            if (previousSharedMode.HasValue && currentShared == SharedEditorMode.Dispos)
            {
                TerrainPaintToolWindow.SetSharedEditorMode(previousSharedMode.Value);
            }
            previousSharedMode = null;
        }
        
        [MenuItem("Window/Dispos Tool")]
        public static void ShowWindow()
        {
            var window = GetWindow<DisposToolWindow>("Dispos Tool");
            window.minSize = new Vector2(260, 240);
        }
        
        private void OnEnable()
        {
            instance = this;
            
            sceneRenderer = new DisposSceneRenderer();
            sceneRenderer.Initialize();
            // Initialize renderer flags with current settings
            sceneRenderer.SetShowGrid(showGrid);
            sceneRenderer.SetShowLabels(showLabels);
            sceneRenderer.SetShowDirections(showDirections);
            sceneRenderer.SetShowIcons(showIcons);
            sceneRenderer.SetShowSimplifiedNames(showSimplifiedNames);
            
            // Create undo proxy
            undoProxy = ScriptableObject.CreateInstance<DisposUndoProxy>();
            
            SceneView.duringSceneGui += OnSceneGUI;
            Undo.undoRedoPerformed += OnUndoRedoPerformed;

            // Try to ensure our scene GUI draws after others
            EditorApplication.delayCall += () =>
            {
                SceneView.duringSceneGui -= OnSceneGUI;
                SceneView.duringSceneGui += OnSceneGUI;
            };

            // Load view prefs
            showGrid = EditorPrefs.GetBool("Dispos_ShowGrid", true);
            showLabels = EditorPrefs.GetBool("Dispos_ShowLabels", true);
            showDirections = EditorPrefs.GetBool("Dispos_ShowDirections", true);
            showIcons = EditorPrefs.GetBool("Dispos_ShowIcons", true);
            showSimplifiedNames = EditorPrefs.GetBool("Dispos_ShowSimplifiedNames", true);

            // Apply loaded prefs to renderer
            sceneRenderer.SetShowGrid(showGrid);
            sceneRenderer.SetShowLabels(showLabels);
            sceneRenderer.SetShowDirections(showDirections);
            sceneRenderer.SetShowIcons(showIcons);
            sceneRenderer.SetShowSimplifiedNames(showSimplifiedNames);

            // Reflect current overlay mode in TerrainPaint tool
            RefreshSharedEditorModeLock();
            
            RefreshFileList();
            
            if (availableFiles != null && availableFiles.Length > 0)
            {
                int lastIndex = Mathf.Clamp(EditorPrefs.GetInt("Dispos_SelectedFileIndex", 0), 0, availableFiles.Length - 1);
                LoadFile(lastIndex);
            }

            // Load difficulty filter prefs and apply
            filterNormal = EditorPrefs.GetBool("Dispos_Filter_N", true);
            filterHard = EditorPrefs.GetBool("Dispos_Filter_H", true);
            filterLunatic = EditorPrefs.GetBool("Dispos_Filter_L", true);
            sceneRenderer.SetDifficultyFilter(filterNormal, filterHard, filterLunatic);
        }
        
        private void OnDisable()
        {
            instance = null;
            
            SceneView.duringSceneGui -= OnSceneGUI;
            // No GameObject selection integration
            Undo.undoRedoPerformed -= OnUndoRedoPerformed;
            
            // Restore tools visibility
            Tools.hidden = false;

            // Release any external locks
            ReleaseSharedEditorModeLock();

            if (sceneRenderer != null)
            {
                sceneRenderer.Cleanup();
            }
            
            if (undoProxy != null)
            {
                DestroyImmediate(undoProxy);
                undoProxy = null;
            }

            // Stop any ongoing smooth pan
            if (isPanningScene)
            {
                EditorApplication.update -= UpdateScenePan;
                isPanningScene = false;
            }
        }
        
        private void OnUndoRedoPerformed()
        {
            if (undoProxy != null && currentDocument != null)
            {
                // Restore document state from undo proxy
                undoProxy.RestoreFromSerializedState();
                
                // Mark document as dirty
                documentIsDirty = true;
                
                // Refresh the scene
                sceneRenderer?.RenderDocument(currentDocument, selectedTerrain);
                SceneView.RepaintAll();
                RequestRepaintAll();
            }
        }
        
        private void OnGUI()
        {
            // Also allow closing the quick picker with Escape when the editor window has focus
            var evt = Event.current;
            if (pickerOpen && evt != null && (evt.type == EventType.KeyDown || evt.type == EventType.KeyUp) && evt.keyCode == KeyCode.Escape)
            {
                pickerOpen = false;
                evt.Use();
                SceneView.RepaintAll();
                RequestRepaintAll();
                // Early return to avoid drawing one more frame with picker state
                return;
            }

            // Dispos Tool main window focuses on configuration + launchers
            DrawConfigurationOnlyPanel();
        }
        
        // (Removed) Old toolbar retained in history; actions now live in DrawActionsPanel()

        // Compact actions block for use in panels/windows
        private void DrawActionsPanel()
        {
            EditorGUILayout.LabelField("Actions", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Refresh Files", GUILayout.Width(110)))
            {
                RefreshFileList();
            }
            if (GUILayout.Button("Reload Data", GUILayout.Width(110)))
            {
                DisposDataLoader.Instance.ReloadData();
                if (currentDocument != null)
                {
                    sceneRenderer.RenderDocument(currentDocument, selectedTerrain);
                }
            }
            GUI.enabled = currentDocument != null && documentIsDirty;
            if (GUILayout.Button("Save", GUILayout.Width(90)))
            {
                SaveDocument();
                documentIsDirty = false;
            }
            GUI.enabled = true;
            if (GUILayout.Button("Debug Refresh", GUILayout.Width(120)))
            {
                Debug.Log($"Debug: Document={currentDocument != null}, Terrain={selectedTerrain != null}");
                if (currentDocument != null)
                {
                    Debug.Log($"Groups: {currentDocument.Groups.Count}");
                    foreach (var group in currentDocument.Groups)
                    {
                        Debug.Log($"  Group {group.GroupName}: {group.Entries.Count} entries");
                    }
                }
                sceneRenderer?.RenderDocument(currentDocument, selectedTerrain);
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            if (currentDocument != null && documentIsDirty)
            {
                EditorGUILayout.LabelField("Modified (unsaved changes)", EditorStyles.miniLabel);
            }
        }
        
        // Removed old left/right combined layout; panels now live in dedicated windows

        // New: Configuration + launchers view for the simplified Dispos Tool window
        private void DrawConfigurationOnlyPanel()
        {
            EditorGUILayout.BeginVertical();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Configuration", EditorStyles.boldLabel);

            // Terrain selection (for coordinate reference)
            EditorGUI.BeginChangeCheck();
            Type terrainType = TerrainAssetAdapter.MapTerrainType ?? typeof(ScriptableObject);
            ScriptableObject terrainAsset = selectedTerrain?.Asset;
            var newTerrain = EditorGUILayout.ObjectField("Map Terrain", terrainAsset, terrainType, false) as ScriptableObject;
            if (EditorGUI.EndChangeCheck())
            {
                if (newTerrain != terrainAsset)
                {
                    selectedTerrain = newTerrain != null ? TerrainAssetAdapter.FromObject(newTerrain) : null;
                    if (currentDocument != null)
                    {
                        sceneRenderer.RenderDocument(currentDocument, selectedTerrain);
                        SceneView.RepaintAll();
                    }
                }
            }

            if (selectedTerrain?.IsValid == true)
            {
                EditorGUILayout.LabelField($"Map: {selectedTerrain.Width}x{selectedTerrain.Height}", EditorStyles.miniLabel);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Dispos Files", EditorStyles.boldLabel);

            if (availableFiles != null && availableFiles.Length > 0)
            {
                int newIndex = EditorGUILayout.Popup("File:", selectedFileIndex, availableFiles);
                if (newIndex != selectedFileIndex)
                {
                    LoadFile(newIndex);
                }
            }
            else
            {
                EditorGUILayout.HelpBox("No Dispos files found in " + disposFolderPath, MessageType.Warning);
            }

            // Launchers placed after the Dispos Files dropdown
            EditorGUILayout.Space();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Open Groups", GUILayout.Width(120)))
            {
                DisposGroupWindow.ShowWindow();
            }
            if (GUILayout.Button("Open Unit Inspector", GUILayout.Width(160)))
            {
                DisposUnitInspectorWindow.ShowWindow();
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            // File/Data actions (moved from the top toolbar)
            EditorGUILayout.Space();
            DrawActionsPanel();

            // View & filter settings moved out of toolbar to reduce required width
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("View & Filters", EditorStyles.boldLabel);

            // Overlay mode selector
            EditorGUI.BeginChangeCheck();
            var modeNames = new[] { "Hide", "Show", "Edit" };
            int newMode = EditorGUILayout.Popup("Overlay", (int)overlayMode, modeNames);
            if (EditorGUI.EndChangeCheck())
            {
                overlayMode = (DisposOverlayMode)newMode;
                RefreshSharedEditorModeLock();
                if (overlayMode == DisposOverlayMode.Hide)
                {
                    sceneRenderer.Cleanup();
                }
                else if (currentDocument != null)
                {
                    sceneRenderer.RenderDocument(currentDocument, selectedTerrain);
                }
                SceneView.RepaintAll();
            }

            // Vertical view toggles so they don't force a wide window
            bool prevGrid = showGrid, prevLabels = showLabels, prevDirs = showDirections, prevIcons = showIcons, prevSimple = showSimplifiedNames;
            showGrid = EditorGUILayout.ToggleLeft("Grid", showGrid);
            showLabels = EditorGUILayout.ToggleLeft("Labels", showLabels);
            if (showLabels)
            {
                EditorGUI.indentLevel++;
                showSimplifiedNames = EditorGUILayout.ToggleLeft("Simple Names", showSimplifiedNames);
                EditorGUI.indentLevel--;
            }
            showDirections = EditorGUILayout.ToggleLeft("Directions", showDirections);
            showIcons = EditorGUILayout.ToggleLeft("Icons", showIcons);

            if (showGrid != prevGrid) EditorPrefs.SetBool("Dispos_ShowGrid", showGrid);
            if (showLabels != prevLabels) EditorPrefs.SetBool("Dispos_ShowLabels", showLabels);
            if (showDirections != prevDirs) EditorPrefs.SetBool("Dispos_ShowDirections", showDirections);
            if (showIcons != prevIcons) EditorPrefs.SetBool("Dispos_ShowIcons", showIcons);
            if (showSimplifiedNames != prevSimple) EditorPrefs.SetBool("Dispos_ShowSimplifiedNames", showSimplifiedNames);

            // Apply immediately
            sceneRenderer?.SetShowGrid(showGrid);
            sceneRenderer?.SetShowLabels(showLabels);
            sceneRenderer?.SetShowDirections(showDirections);
            sceneRenderer?.SetShowIcons(showIcons);
            sceneRenderer?.SetShowSimplifiedNames(showSimplifiedNames);

            // Difficulty filters, spread but compact
            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("Difficulty Filter", EditorStyles.miniBoldLabel);
            EditorGUILayout.BeginHorizontal();
            bool n = GUILayout.Toggle(filterNormal, "Normal (N)", GUI.skin.button, GUILayout.Width(100));
            bool h = GUILayout.Toggle(filterHard, "Hard (H)", GUI.skin.button, GUILayout.Width(100));
            bool l = GUILayout.Toggle(filterLunatic, "Lunatic (L)", GUI.skin.button, GUILayout.Width(100));
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            if (n != filterNormal || h != filterHard || l != filterLunatic)
            {
                filterNormal = n; filterHard = h; filterLunatic = l;
                EditorPrefs.SetBool("Dispos_Filter_N", filterNormal);
                EditorPrefs.SetBool("Dispos_Filter_H", filterHard);
                EditorPrefs.SetBool("Dispos_Filter_L", filterLunatic);
                sceneRenderer.SetDifficultyFilter(filterNormal, filterHard, filterLunatic);
            }

            EditorGUILayout.EndVertical();
        }

        // Standalone render of the Groups panel (for separate dockable window)
        public void DrawGroupsPanelStandalone()
        {
            // Focus this window purely on group management
            EditorGUILayout.BeginVertical(GUILayout.ExpandWidth(true));
            // Reset groups hover flag each frame
            groupsPanelAnyHover = false;
            EditorGUILayout.LabelField("Groups", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            newGroupName = EditorGUILayout.TextField("New Group", newGroupName);
            if (GUILayout.Button("Add", GUILayout.Width(60)))
            {
                TryAddGroup(newGroupName);
            }
            EditorGUILayout.EndHorizontal();

            leftPanelScroll = EditorGUILayout.BeginScrollView(leftPanelScroll);

            if (currentDocument != null)
            {
                foreach (var group in currentDocument.Groups)
                {
                    DrawGroupItem(group);
                }
            }

            EditorGUILayout.EndScrollView();
            // If nothing hovered in groups list and no picker open, clear hover highlight
            if (!pickerOpen && !groupsPanelAnyHover)
            {
                sceneRenderer?.SetHoverEntry(null);
            }
            EditorGUILayout.EndVertical();
        }
        
        private void DrawGroupItem(DisposGroup group)
        {
            EditorGUILayout.BeginHorizontal();
            
            bool newExpanded = EditorGUILayout.Foldout(group.IsExpanded, group.GroupName);
            if (newExpanded != group.IsExpanded)
            {
                group.IsExpanded = newExpanded;
                EditorPrefs.SetBool($"Dispos_Group_{group.GroupName}_Expanded", group.IsExpanded);
            }
            
            GUILayout.FlexibleSpace();

            // Add Unit / Move Selected Here quick actions
            if (GUILayout.Button("+ Unit", EditorStyles.miniButton, GUILayout.Width(60)))
            {
                AddUnitToGroup(group);
            }
            bool canMoveSelected = selectedEntry != null && !selectedEntry.IsGroupHeader && selectedEntry.Group != group.GroupName;
            GUI.enabled = canMoveSelected;
            if (GUILayout.Button("Move Here", EditorStyles.miniButton, GUILayout.Width(80)))
            {
                MoveSelectedToGroup(group);
            }
            GUI.enabled = true;
            
            bool newVisible = EditorGUILayout.Toggle(group.IsVisible, GUILayout.Width(20));
            if (newVisible != group.IsVisible)
            {
                group.IsVisible = newVisible;
                EditorPrefs.SetBool($"Dispos_Group_{group.GroupName}_Visible", group.IsVisible);
                sceneRenderer.RenderDocument(currentDocument);
            }
            
            EditorGUILayout.EndHorizontal();
            
            if (group.IsExpanded)
            {
                EditorGUI.indentLevel++;
                
                int playerCount = 0, enemyCount = 0, allyCount = 0;
                foreach (var entry in group.Entries)
                {
                    if (!entry.IsGroupHeader)
                    {
                        switch (entry.Force)
                        {
                            case 0: playerCount++; break;
                            case 1: enemyCount++; break;
                            case 2: allyCount++; break;
                        }
                    }
                }
                
                EditorGUILayout.LabelField($"  Units: {group.Entries.Count} (P:{playerCount} E:{enemyCount} A:{allyCount})", 
                                          EditorStyles.miniLabel);
                
                // Show all entries (no truncation)
                foreach (var entry in group.Entries)
                {
                    if (!entry.IsGroupHeader)
                    {
                        // Reserve a full-width single-line rect for the row
                        Rect rowRectRaw = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
                        // Apply current indent to the row
                        Rect rowRect = EditorGUI.IndentedRect(rowRectRaw);

                        // Split into name area and coords area
                        const float coordsWidth = 60f;
                        Rect coordsRect = new Rect(rowRect.xMax - coordsWidth, rowRect.y, coordsWidth, rowRect.height);
                        Rect nameRect = new Rect(rowRect.x, rowRect.y, Mathf.Max(0, rowRect.width - coordsWidth - 4f), rowRect.height);

                        // Early-handle double-click and right-click so buttons don't swallow them
                        var e = Event.current;
                        if (e.type == EventType.MouseDown && e.button == 0 && e.clickCount == 2 && rowRect.Contains(e.mousePosition))
                        {
                            // Double-click anywhere on the row: select and pan
                            SelectEntry(entry);
                            PanSceneToEntry(entry);
                            e.Use();
                        }
                        if ((e.type == EventType.ContextClick || (e.type == EventType.MouseDown && e.button == 1)) && rowRect.Contains(e.mousePosition))
                        {
                            var capturedEntry = entry;
                            GenericMenu menu = new GenericMenu();
                            // Duplicate
                            menu.AddItem(new GUIContent("Duplicate Unit"), false, () => DuplicateUnitEntry(capturedEntry));
                            // Move to Group submenu
                            if (currentDocument != null && currentDocument.Groups != null && currentDocument.Groups.Count > 0)
                            {
                                foreach (var g in currentDocument.Groups)
                                {
                                    string path = $"Move To Group/{g.GroupName}";
                                    if (g.GroupName == capturedEntry.Group)
                                    {
                                        menu.AddDisabledItem(new GUIContent(path));
                                    }
                                    else
                                    {
                                        var target = g;
                                        menu.AddItem(new GUIContent(path), false, () => MoveEntryToGroup(capturedEntry, target));
                                    }
                                }
                            }
                            menu.AddSeparator("");
                            // Delete
                            menu.AddItem(new GUIContent("Delete Unit"), false, () => DeleteUnitEntry(capturedEntry));
                            menu.ShowAsContext();
                            e.Use();
                        }

                        // Draw background styled like a mini button, tinted by force color
                        Color forceColor = DisposDataLoader.Instance.GetForceColor(entry.Force);
                        string displayName = DisposDataLoader.Instance.GetUnitDisplayName(entry);
                        bool isSelected = (selectedEntry == entry);
                        GUIStyle rowStyle = new GUIStyle(EditorStyles.miniButton);
                        rowStyle.alignment = TextAnchor.MiddleLeft;
                        if (isSelected) rowStyle.fontStyle = FontStyle.Bold;

                        // Subtle hover tint for name button
                        bool nameHover = nameRect.Contains(Event.current.mousePosition);
                        Color prevBg = GUI.backgroundColor;
                        Color nameBg = forceColor;
                        if (nameHover) nameBg = Color.Lerp(nameBg, Color.white, 0.12f);
                        GUI.backgroundColor = nameBg;
                        if (GUI.Button(nameRect, displayName, rowStyle))
                        {
                            // If already selected, clicking name pans; otherwise select only
                            if (selectedEntry == entry) PanSceneToEntry(entry);
                            else SelectEntry(entry);
                        }
                        GUI.backgroundColor = prevBg;

                        // Coordinates shown as a button on the right
                        string coordsText = $"({entry.DisposX},{entry.DisposY})";
                        GUIStyle coordsStyle = EditorStyles.miniButton;
                        if (GUI.Button(coordsRect, coordsText, coordsStyle))
                        {
                            // Pan without changing selection
                            PanSceneToEntry(entry);
                        }

                        // Cursor hints
                        EditorGUIUtility.AddCursorRect(nameRect, MouseCursor.Link);
                        EditorGUIUtility.AddCursorRect(coordsRect, MouseCursor.Link);

                        // Handle input over the row, with split behavior
                        if (rowRect.Contains(e.mousePosition))
                        {
                            // Hover feedback + scene hover highlight
                            if (group.IsVisible && EntryPassesCurrentFilter(entry))
                            {
                                sceneRenderer?.SetHoverEntry(entry);
                                groupsPanelAnyHover = true;
                                SceneView.RepaintAll();
                            }

                            // Name clicks handled by GUI.Button above

                            // Left-click coords handled by GUI.Button above

                            // Right-click handled earlier before drawing controls
                        }

                        // Selection outline highlight only around the name area
                        if (isSelected)
                        {
                            Color sel = new Color(1f, 0.9f, 0.2f, 0.95f);
                            // 2px border around just the name rect
                            EditorGUI.DrawRect(new Rect(nameRect.xMin, nameRect.yMin, nameRect.width, 2f), sel);
                            EditorGUI.DrawRect(new Rect(nameRect.xMin, nameRect.yMax - 2f, nameRect.width, 2f), sel);
                            EditorGUI.DrawRect(new Rect(nameRect.xMin, nameRect.yMin, 2f, nameRect.height), sel);
                            EditorGUI.DrawRect(new Rect(nameRect.xMax - 2f, nameRect.yMin, 2f, nameRect.height), sel);
                        }
                    }
                }
                
                // No truncation footer — we show all rows within the scroll view
                
                EditorGUI.indentLevel--;
            }
        }

        // Mirror the renderer's difficulty logic locally for hover checks
        private bool EntryPassesCurrentFilter(DisposEntry entry)
        {
            if (entry == null) return false;
            int mask = 0;
            if (filterNormal) mask |= (int)DisposFlags.Normal;
            if (filterHard) mask |= (int)DisposFlags.Hard;
            if (filterLunatic) mask |= (int)DisposFlags.Lunatic;
            int diff = entry.Flag & (int)DisposFlags.MaskDifficulty;
            if (diff == 0) return true; // visible in all if no diff bits set
            return (diff & mask) != 0;
        }

        // Pan the Scene view to the given entry without changing zoom
        private void PanSceneToEntry(DisposEntry entry)
        {
            if (entry == null || sceneRenderer == null) return;
            if (sceneRenderer.TryGetEntryWorldBounds(entry, out var b))
            {
                StartScenePan(b.center, 0.28f);
            }
        }

        private void DeleteUnitEntry(DisposEntry entry)
        {
            if (currentDocument == null || entry == null) return;
            // Prevent deleting group headers via this path
            if (entry.IsGroupHeader) return;

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Delete Unit");
            undoProxy?.RecordUndo("Delete Unit");

            // Remove from its group container
            var group = currentDocument.Groups.FirstOrDefault(g => g.GroupName == entry.Group);
            if (group != null)
            {
                group.Entries.Remove(entry);
            }

            // Remove from the global list
            currentDocument.AllEntries.Remove(entry);

            // Clear selection if it was this entry
            if (selectedEntry == entry)
            {
                selectedEntry = null;
                sceneRenderer.SelectedEntry = null;
            }

            documentIsDirty = true;
            sceneRenderer.RenderDocument(currentDocument, selectedTerrain);
            SceneView.RepaintAll();
            RequestRepaintAll();
            undoProxy?.CapturePostState();
        }

        private void DuplicateUnitEntry(DisposEntry src)
        {
            if (currentDocument == null || src == null) return;
            if (src.IsGroupHeader) return;

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Duplicate Unit");
            undoProxy?.RecordUndo("Duplicate Unit");

            // Create a deep copy
            var e = new DisposEntry
            {
                Group = src.Group,
                Pid = src.Pid,
                Force = src.Force,
                Flag = src.Flag,
                AppearX = src.AppearX,
                AppearY = src.AppearY,
                DisposX = src.DisposX,
                DisposY = src.DisposY,
                Direction = src.Direction,
                LevelN = src.LevelN,
                LevelH = src.LevelH,
                LevelL = src.LevelL,
                Jid = src.Jid,
                Sid = src.Sid,
                Bid = src.Bid,
                Gid = src.Gid,
                HpStockCount = src.HpStockCount,
            };
            e.IsGroupHeader = false;
            for (int i = 0; i < 6; i++)
            {
                e.States[i] = src.States[i];
                e.Items[i].Iid = src.Items[i].Iid;
                e.Items[i].Drop = src.Items[i].Drop;
            }
            // Copy AI fields
            e.AI_ActionName = src.AI_ActionName;
            e.AI_ActionVal = src.AI_ActionVal;
            e.AI_MindName = src.AI_MindName;
            e.AI_MindVal = src.AI_MindVal;
            e.AI_AttackName = src.AI_AttackName;
            e.AI_AttackVal = src.AI_AttackVal;
            e.AI_MoveName = src.AI_MoveName;
            e.AI_MoveVal = src.AI_MoveVal;
            e.AI_BattleRate = src.AI_BattleRate;
            e.AI_Priority = src.AI_Priority;
            e.AI_HealRateA = src.AI_HealRateA;
            e.AI_HealRateB = src.AI_HealRateB;
            e.AI_BandNo = src.AI_BandNo;
            e.AI_MoveLimit = src.AI_MoveLimit;
            e.AI_Flag = src.AI_Flag;
            e.AI_Active = src.AI_Active;
            e.AI_ActiveTurn = src.AI_ActiveTurn;
            e.AI_ActiveFlag = src.AI_ActiveFlag;

            // Copy additional attributes
            var extras = src.GetAllAdditionalAttributes();
            foreach (var kv in extras) e.SetAdditionalAttribute(kv.Key, kv.Value);

            // Insert into AllEntries immediately after the source
            int srcIndex = currentDocument.AllEntries.IndexOf(src);
            if (srcIndex >= 0 && srcIndex + 1 <= currentDocument.AllEntries.Count)
                currentDocument.AllEntries.Insert(srcIndex + 1, e);
            else
                currentDocument.AllEntries.Add(e);

            // Insert into group list after the source
            var group = currentDocument.Groups.FirstOrDefault(g => g.GroupName == src.Group);
            if (group != null)
            {
                int idx = group.Entries.IndexOf(src);
                if (idx >= 0 && idx + 1 <= group.Entries.Count) group.Entries.Insert(idx + 1, e);
                else group.Entries.Add(e);
            }

            documentIsDirty = true;
            sceneRenderer.RenderDocument(currentDocument, selectedTerrain);
            SelectEntry(e);
            SceneView.RepaintAll();
            RequestRepaintAll();
            undoProxy?.CapturePostState();
        }

        private void MoveEntryToGroup(DisposEntry entry, DisposGroup target)
        {
            if (currentDocument == null || target == null || entry == null) return;
            if (entry.IsGroupHeader) return;
            if (entry.Group == target.GroupName) return;

            undoProxy?.RecordUndo("Move Unit To Group");

            // Remove from old group container
            var oldGroup = currentDocument.Groups.FirstOrDefault(g => g.GroupName == entry.Group);
            if (oldGroup != null) oldGroup.Entries.Remove(entry);

            // Update the entry's group field
            entry.Group = target.GroupName;

            // Remove from current position in AllEntries
            int currentIndex = currentDocument.AllEntries.IndexOf(entry);
            if (currentIndex >= 0) currentDocument.AllEntries.RemoveAt(currentIndex);

            // Insert into AllEntries after target group's last entry to preserve order
            int insertIndex = -1;
            for (int i = 0; i < currentDocument.AllEntries.Count; i++)
            {
                var ae = currentDocument.AllEntries[i];
                if (ae.Group == target.GroupName) insertIndex = i;
            }
            if (insertIndex >= 0 && insertIndex + 1 <= currentDocument.AllEntries.Count)
                currentDocument.AllEntries.Insert(insertIndex + 1, entry);
            else
                currentDocument.AllEntries.Add(entry);

            // Add to target group container
            target.Entries.Add(entry);

            documentIsDirty = true;
            sceneRenderer.RenderDocument(currentDocument, selectedTerrain);
            SelectEntry(entry);
            SceneView.RepaintAll();
            RequestRepaintAll();
        }

        // Begin a smooth pan (pivot only) over duration seconds
        private void StartScenePan(Vector3 targetPivot, float duration)
        {
            var sv = SceneView.lastActiveSceneView;
            if (sv == null && SceneView.sceneViews != null && SceneView.sceneViews.Count > 0)
                sv = SceneView.sceneViews[0] as SceneView;
            if (sv == null) return;

            panStartPivot = sv.pivot;
            panTargetPivot = targetPivot;
            panStartTime = EditorApplication.timeSinceStartup;
            panDuration = Mathf.Max(0.01f, duration);

            if (!isPanningScene)
            {
                isPanningScene = true;
                EditorApplication.update += UpdateScenePan;
            }
            sv.Repaint();
        }

        private void UpdateScenePan()
        {
            var sv = SceneView.lastActiveSceneView;
            if (sv == null && SceneView.sceneViews != null && SceneView.sceneViews.Count > 0)
                sv = SceneView.sceneViews[0] as SceneView;
            if (sv == null)
            {
                EditorApplication.update -= UpdateScenePan;
                isPanningScene = false;
                return;
            }

            double elapsed = EditorApplication.timeSinceStartup - panStartTime;
            float t = panDuration <= 0f ? 1f : Mathf.Clamp01((float)(elapsed / panDuration));
            // Smoothstep easing for a nicer glide
            float s = t * t * (3f - 2f * t);
            sv.pivot = Vector3.Lerp(panStartPivot, panTargetPivot, s);
            sv.Repaint();

            if (t >= 1f)
            {
                EditorApplication.update -= UpdateScenePan;
                isPanningScene = false;
            }
        }

        private void TryAddGroup(string name)
        {
            if (currentDocument == null) return;
            string trimmed = (name ?? "").Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                EditorUtility.DisplayDialog("Add Group", "Please enter a non-empty group name.", "OK");
                return;
            }
            // Ensure uniqueness
            if (currentDocument.Groups.Any(g => g.GroupName == trimmed))
            {
                EditorUtility.DisplayDialog("Add Group", $"Group '{trimmed}' already exists.", "OK");
                return;
            }

            // Record undo
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Add Group");
            undoProxy?.RecordUndo("Add Group");

            // Create header entry to mark the group boundary in XML
            var headerEntry = new DisposEntry
            {
                Group = trimmed,
                Pid = "" // header marker via empty PID
            };

            // Create group container
            var newGroup = new DisposGroup
            {
                GroupName = trimmed,
                Entries = new List<DisposEntry>(), // header not included in group entries
                IsExpanded = true,
                IsVisible = true
            };

            currentDocument.Groups.Add(newGroup);
            currentDocument.AllEntries.Add(headerEntry);
            documentIsDirty = true;
            newGroupName = "";
            sceneRenderer.RenderDocument(currentDocument);
            RequestRepaintAll();
            undoProxy?.CapturePostState();
        }

        private void AddUnitToGroup(DisposGroup group)
        {
            if (currentDocument == null || group == null) return;
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Add Unit to Group");
            undoProxy?.RecordUndo("Add Unit to Group");

            var e = new DisposEntry
            {
                Group = group.GroupName,
                // Empty PID allowed in-memory; this remains a unit (not a header)
                Pid = "",
                Force = 0,
                Flag = 0,
                AppearX = hasSelectedTile ? selectedTile.x : 0,
                AppearY = hasSelectedTile ? selectedTile.y : 0,
                DisposX = hasSelectedTile ? selectedTile.x : 0,
                DisposY = hasSelectedTile ? selectedTile.y : 0,
                Direction = 0,
                LevelN = 0,
                LevelH = 0,
                LevelL = 0,
                Jid = "",
            };
            e.IsGroupHeader = false;

            // Insert into AllEntries after the group's last entry if possible
            int insertIndex = -1;
            for (int i = 0; i < currentDocument.AllEntries.Count; i++)
            {
                var ae = currentDocument.AllEntries[i];
                if (ae.Group == group.GroupName)
                    insertIndex = i; // keep updating to last occurrence
            }
            if (insertIndex >= 0 && insertIndex + 1 <= currentDocument.AllEntries.Count)
                currentDocument.AllEntries.Insert(insertIndex + 1, e);
            else
                currentDocument.AllEntries.Add(e);

            group.Entries.Add(e);
            documentIsDirty = true;
            sceneRenderer.RenderDocument(currentDocument);
            SelectEntry(e);
            SceneView.RepaintAll();
            // Keep the tile highlight for further adds if desired
            undoProxy?.CapturePostState();
        }

        private void MoveSelectedToGroup(DisposGroup target)
        {
            if (currentDocument == null || target == null || selectedEntry == null) return;
            var entry = selectedEntry;
            if (entry.IsGroupHeader) return;
            if (entry.Group == target.GroupName) return;

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Move Unit To Group");
            undoProxy?.RecordUndo("Move Unit To Group");

            // Remove from old group container
            var oldGroup = currentDocument.Groups.FirstOrDefault(g => g.GroupName == entry.Group);
            if (oldGroup != null)
                oldGroup.Entries.Remove(entry);

            // Update the entry's group field
            entry.Group = target.GroupName;

            // Insert into AllEntries after target group's last entry to preserve order
            int currentIndex = currentDocument.AllEntries.IndexOf(entry);
            if (currentIndex >= 0)
                currentDocument.AllEntries.RemoveAt(currentIndex);

            int insertIndex = -1;
            for (int i = 0; i < currentDocument.AllEntries.Count; i++)
            {
                var ae = currentDocument.AllEntries[i];
                if (ae.Group == target.GroupName)
                    insertIndex = i;
            }
            if (insertIndex >= 0 && insertIndex + 1 <= currentDocument.AllEntries.Count)
                currentDocument.AllEntries.Insert(insertIndex + 1, entry);
            else
                currentDocument.AllEntries.Add(entry);

            // Add to target group container
            target.Entries.Add(entry);

            documentIsDirty = true;
            sceneRenderer.RenderDocument(currentDocument);
            SelectEntry(entry);
            SceneView.RepaintAll();
            undoProxy?.CapturePostState();
        }
        
        // (Removed) Old combined right panel; Unit Inspector is now a separate window via DrawUnitInspectorStandalone()

        // Standalone render of the Unit Inspector (for separate dockable window)
        public void DrawUnitInspectorStandalone()
        {
            EditorGUILayout.BeginVertical(GUILayout.ExpandWidth(true));
            EditorGUILayout.LabelField("Unit Inspector", EditorStyles.boldLabel);

            rightPanelScroll = EditorGUILayout.BeginScrollView(rightPanelScroll);

            if (selectedEntry != null && !selectedEntry.IsGroupHeader)
            {
                DrawEntryInspector(selectedEntry);
            }
            else
            {
                EditorGUILayout.HelpBox("Select a unit to inspect", MessageType.Info);
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        // (Removed) Settings window; actions are available in the main panel
        
        private void DrawEntryInspector(DisposEntry entry)
        {
            EditorGUILayout.LabelField("Basic Info", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            
            // Dispos identifier (only PID is editable here)
            EditorGUILayout.BeginHorizontal();
            GUI.SetNextControlName("PIDField");
            string newPid = EditorGUILayout.TextField("PID", entry.Pid);
            if (newPid != entry.Pid)
            {
                entry.Pid = newPid;
            }
            if (GUILayout.Button("Browse", GUILayout.Width(70)))
            {
                QuickSearchWindow.Show<PersonInfo>(
                    title: "Select Person",
                    provider: (string q) =>
                    {
                        var persons = DisposDataLoader.Instance.GetAllPersons();
                        if (persons == null) return new System.Collections.Generic.List<PersonInfo>();
                        q = (q ?? "").Trim();
                        var list = persons.Where(p =>
                                        string.IsNullOrEmpty(q) ||
                                        (p.Pid?.IndexOf(q, System.StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
                                        (p.Name?.IndexOf(q, System.StringComparison.OrdinalIgnoreCase) ?? -1) >= 0)
                                        .OrderBy(p => p.Pid)
                                        .ToList();
                        return list;
                    },
                    primaryText: (PersonInfo p) =>
                    {
                        var nice = (!string.IsNullOrEmpty(p.Name) && p.Name.StartsWith("MPID_")) ? DisposDataLoader.Instance.GetDisplayNameFromMpid(p.Name) : p.Name;
                        return string.IsNullOrEmpty(nice) ? p.Pid : nice;
                    },
                    rightText: (PersonInfo p) => string.IsNullOrEmpty(p.Name) ? p.Pid : (p.Pid + (p.Name.StartsWith("MPID_") ? "  (" + p.Name + ")" : "")),
                    onSelect: (PersonInfo p) => ApplyPidToSelected(p.Pid),
                    initialQuery: entry.Pid
                );
            }
            EditorGUILayout.EndHorizontal();
            // Inline suggestions removed; use Browse popup instead
            
            // Intentionally omit Person/Job readouts like Name/Gender to keep focus on Dispos data
            
            entry.Force = EditorGUILayout.IntPopup("Force", entry.Force, 
                new string[] { "Player", "Enemy", "Ally", "Other" }, 
                new int[] { 0, 1, 2, 3 });
            
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Position", EditorStyles.boldLabel);
            
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Dispos", GUILayout.Width(60));
            entry.DisposX = EditorGUILayout.IntField(entry.DisposX, GUILayout.Width(50));
            entry.DisposY = EditorGUILayout.IntField(entry.DisposY, GUILayout.Width(50));
            EditorGUILayout.EndHorizontal();
            
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Appear", GUILayout.Width(60));
            entry.AppearX = EditorGUILayout.IntField(entry.AppearX, GUILayout.Width(50));
            entry.AppearY = EditorGUILayout.IntField(entry.AppearY, GUILayout.Width(50));
            EditorGUILayout.EndHorizontal();
            
            entry.Direction = EditorGUILayout.IntSlider("Direction", entry.Direction, 0, 8);
            
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Class & Level", EditorStyles.boldLabel);
            
            EditorGUILayout.BeginHorizontal();
            string newJid = EditorGUILayout.TextField("Job ID", entry.Jid);
            if (newJid != entry.Jid)
            {
                entry.Jid = newJid;
            }
            if (GUILayout.Button("Browse", GUILayout.Width(70)))
            {
                QuickSearchWindow.Show<JobInfo>(
                    title: "Select Job",
                    provider: (string q) =>
                    {
                        var jobs = DisposDataLoader.Instance.GetAllJobs();
                        if (jobs == null) return new System.Collections.Generic.List<JobInfo>();
                        q = (q ?? "").Trim();
                        var list = jobs.Where(j =>
                                        string.IsNullOrEmpty(q) ||
                                        (j.Jid?.IndexOf(q, System.StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
                                        (j.Name?.IndexOf(q, System.StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
                                        (DisposDataLoader.Instance.GetJobDisplayName(j)?.IndexOf(q, System.StringComparison.OrdinalIgnoreCase) ?? -1) >= 0)
                                      .OrderBy(j => DisposDataLoader.Instance.GetJobDisplayName(j))
                                      .ThenBy(j => j.Jid)
                                      .ToList();
                        return list;
                    },
                    primaryText: (JobInfo j) => DisposDataLoader.Instance.GetJobDisplayName(j),
                    rightText: (JobInfo j) => string.IsNullOrEmpty(j.Name) ? j.Jid : (j.Jid + "  (" + j.Name + ")"),
                    onSelect: (JobInfo j) => ApplyJidToSelected(j.Jid),
                    initialQuery: entry.Jid
                );
            }
            EditorGUILayout.EndHorizontal();
            
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Level N/H/L", GUILayout.Width(80));
            entry.LevelN = EditorGUILayout.IntField(entry.LevelN, GUILayout.Width(40));
            entry.LevelH = EditorGUILayout.IntField(entry.LevelH, GUILayout.Width(40));
            entry.LevelL = EditorGUILayout.IntField(entry.LevelL, GUILayout.Width(40));
            EditorGUILayout.EndHorizontal();

            // Icon resolution readout
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Icon", EditorStyles.boldLabel);
            string resolvedIcon = DisposDataLoader.Instance.GetUnitIconPath(entry);
            EditorGUILayout.LabelField("Resolved File", string.IsNullOrEmpty(resolvedIcon) ? "(none)" : resolvedIcon + ".png");
            
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("IDs & Stock", EditorStyles.boldLabel);
            entry.Sid = EditorGUILayout.TextField("Sid", entry.Sid);
            entry.Bid = EditorGUILayout.TextField("Bid", entry.Bid);
            entry.Gid = EditorGUILayout.TextField("Gid", entry.Gid);
            entry.HpStockCount = EditorGUILayout.IntField("HP Stock Count", entry.HpStockCount);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("States", EditorStyles.boldLabel);
            // Draw as 2 rows of 3 with comfortable spacing
            float prevLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 20f;
            EditorGUILayout.BeginVertical();
            for (int row = 0; row < 2; row++)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(6);
                for (int col = 0; col < 3; col++)
                {
                    int idx = row * 3 + col;
                    entry.States[idx] = EditorGUILayout.IntField($"S{idx}", entry.States[idx], GUILayout.Width(90));
                    GUILayout.Space(10);
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndVertical();
            EditorGUIUtility.labelWidth = prevLabelWidth;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Items", EditorStyles.boldLabel);
            
            for (int i = 0; i < 6; i++)
            {
                EditorGUILayout.BeginHorizontal();
                entry.Items[i].Iid = EditorGUILayout.TextField($"Item {i+1}", entry.Items[i].Iid);
                entry.Items[i].Drop = EditorGUILayout.IntField(entry.Items[i].Drop, GUILayout.Width(40));
                EditorGUILayout.EndHorizontal();
            }
            
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("AI Settings", EditorStyles.boldLabel);

            entry.AI_ActionName = EditorGUILayout.TextField("Action", entry.AI_ActionName);
            entry.AI_ActionVal = EditorGUILayout.TextField("Action Arg", entry.AI_ActionVal);
            entry.AI_MindName = EditorGUILayout.TextField("Mind", entry.AI_MindName);
            entry.AI_MindVal = EditorGUILayout.TextField("Mind Arg", entry.AI_MindVal);
            entry.AI_AttackName = EditorGUILayout.TextField("Attack", entry.AI_AttackName);
            entry.AI_AttackVal = EditorGUILayout.TextField("Attack Arg", entry.AI_AttackVal);
            entry.AI_MoveName = EditorGUILayout.TextField("Move", entry.AI_MoveName);
            entry.AI_MoveVal = EditorGUILayout.TextField("Move Arg", entry.AI_MoveVal);
            entry.AI_BattleRate = EditorGUILayout.TextField("Battle Rate", entry.AI_BattleRate);
            entry.AI_Priority = EditorGUILayout.IntField("Priority", entry.AI_Priority);
            entry.AI_HealRateA = EditorGUILayout.IntField("Heal Rate A", entry.AI_HealRateA);
            entry.AI_HealRateB = EditorGUILayout.IntField("Heal Rate B", entry.AI_HealRateB);
            entry.AI_BandNo = EditorGUILayout.IntField("Band No", entry.AI_BandNo);
            entry.AI_MoveLimit = EditorGUILayout.TextField("Move Limit", entry.AI_MoveLimit);
            entry.AI_Flag = EditorGUILayout.IntField("AI Flag", entry.AI_Flag);
            entry.AI_Active = EditorGUILayout.TextField("AI Active", entry.AI_Active);
            entry.AI_ActiveTurn = EditorGUILayout.IntField("AI Active Turn", entry.AI_ActiveTurn);
            entry.AI_ActiveFlag = EditorGUILayout.TextField("AI Active Flag", entry.AI_ActiveFlag);

            // Additional attributes (if present)
            var extras = entry.GetAllAdditionalAttributes();
            if (extras != null && extras.Count > 0)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Additional Attributes", EditorStyles.boldLabel);
                foreach (var kv in extras)
                {
                    string newVal = EditorGUILayout.TextField(kv.Key, kv.Value);
                    if (newVal != kv.Value)
                    {
                        entry.SetAdditionalAttribute(kv.Key, newVal);
                    }
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Flags", EditorStyles.boldLabel);
            // Show numeric view
            EditorGUILayout.LabelField($"Value: {entry.Flag} (0x{entry.Flag:X})", EditorStyles.miniLabel);

            // Difficulty flags
            EditorGUILayout.LabelField("Difficulty", EditorStyles.miniBoldLabel);
            entry.Flag = DrawFlagToggle(entry.Flag, "Normal", (int)DisposFlags.Normal);
            entry.Flag = DrawFlagToggle(entry.Flag, "Hard", (int)DisposFlags.Hard);
            entry.Flag = DrawFlagToggle(entry.Flag, "Lunatic", (int)DisposFlags.Lunatic);

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("Sortie Mask", EditorStyles.miniBoldLabel);
            entry.Flag = DrawFlagToggle(entry.Flag, "Pos", (int)DisposFlags.Pos);
            entry.Flag = DrawFlagToggle(entry.Flag, "Must", (int)DisposFlags.Must);
            entry.Flag = DrawFlagToggle(entry.Flag, "Fix", (int)DisposFlags.Fix);

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("Properties", EditorStyles.miniBoldLabel);
            entry.Flag = DrawFlagToggle(entry.Flag, "Create", (int)DisposFlags.Create);
            entry.Flag = DrawFlagToggle(entry.Flag, "Leader", (int)DisposFlags.Leader);
            entry.Flag = DrawFlagToggle(entry.Flag, "Not Move", (int)DisposFlags.NotMove);
            entry.Flag = DrawFlagToggle(entry.Flag, "Edge", (int)DisposFlags.Edge);
            entry.Flag = DrawFlagToggle(entry.Flag, "Guest", (int)DisposFlags.Guest);

            if (EditorGUI.EndChangeCheck())
            {
                Undo.IncrementCurrentGroup();
                Undo.SetCurrentGroupName("Change Unit Properties");
                undoProxy?.RecordUndo("Change Unit Properties");
                documentIsDirty = true;
                sceneRenderer.RenderDocument(currentDocument);
                SceneView.RepaintAll();
                undoProxy?.CapturePostState();
            }
        }

        private void ApplyPidToSelected(string pid)
        {
            if (selectedEntry == null) return;
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Change PID");
            undoProxy?.RecordUndo("Change PID");
            selectedEntry.Pid = pid;
            // If Jid is empty, seed with person default Jid
            var person = DisposDataLoader.Instance.GetPerson(pid);
            if (person != null && string.IsNullOrEmpty(selectedEntry.Jid))
            {
                selectedEntry.Jid = person.Jid;
            }
            MarkDocumentDirty();
            sceneRenderer.RenderDocument(currentDocument);
            RequestRepaintAll();
            undoProxy?.CapturePostState();
        }

        private void ApplyJidToSelected(string jid)
        {
            if (selectedEntry == null) return;
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Change Job ID");
            undoProxy?.RecordUndo("Change Job ID");
            selectedEntry.Jid = jid;
            MarkDocumentDirty();
            sceneRenderer.RenderDocument(currentDocument);
            SceneView.RepaintAll();
            RequestRepaintAll();
            undoProxy?.CapturePostState();
        }

        private int DrawFlagToggle(int flags, string label, int bit)
        {
            bool has = (flags & bit) != 0;
            bool newHas = EditorGUILayout.ToggleLeft(label, has);
            if (newHas != has)
            {
                if (newHas) flags |= bit; else flags &= ~bit;
                MarkDocumentDirty();
            }
            return flags;
        }
        
        // Splitter removed: main window no longer hosts two panels
        
        private void OnSceneGUI(SceneView sceneView)
        {
            if (sceneRenderer != null && currentDocument != null)
            {
                var sharedMode = TerrainPaintToolWindow.GetSharedEditorMode();
                if (sharedMode == SharedEditorMode.Off)
                {
                    if (Tools.hidden)
                    {
                        Tools.hidden = false;
                    }
                    return;
                }

                if (overlayMode != DisposOverlayMode.Hide)
                {
                    // Reposition picker to follow camera movement
                    if (pickerOpen)
                    {
                        UpdatePickerRectPlacement();
                    }
                    // Inform renderer about picker to avoid label overlap (uses updated rect)
                    sceneRenderer.SetGuiOcclusionRect(pickerOpen ? pickerRect : Rect.zero);
                    sceneRenderer.DrawSceneGUI();
                    if (overlayMode == DisposOverlayMode.Edit)
                    {
                        HandleSceneInput();
                        DrawQuickPicker();
                    }
                }

                // Disable default transform handles in Edit mode
                Tools.hidden = overlayMode == DisposOverlayMode.Edit;
                if (Tools.hidden) Tools.current = Tool.None;
            }
        }

        private void UpdatePickerRectPlacement()
        {
            if (!pickerOpen || pickerEntries == null || pickerEntries.Count <= 1) return;
            Rect tileRect = sceneRenderer.GetTileScreenRect(pickerTile);
            float width = 240f;
            float rowH = 22f;
            float headerH = 18f;
            int rowsWanted = Mathf.Clamp(pickerEntries.Count, 2, 8);
            float height = headerH + rowsWanted * rowH + 8f; // header + rows + padding
            float x = tileRect.xMax + 8f;
            if (x + width > Screen.width) x = tileRect.xMin - 8f - width;
            float y = tileRect.yMin;
            // Clamp to screen bounds to avoid going off-screen vertically
            y = Mathf.Clamp(y, 0, Screen.height - height - 8f);
            pickerRect = new Rect(x, y, width, height);
        }
        
        private void HandleSceneInput()
        {
            Event e = Event.current;
            
            // Handle Escape key to close picker (scene view focus)
            if (pickerOpen && (e.type == EventType.KeyDown || e.type == EventType.KeyUp) && e.keyCode == KeyCode.Escape)
            {
                pickerOpen = false;
                e.Use();
                SceneView.RepaintAll();
                return;
            }
            
            // Keyboard nudging disabled per request; movement via drag handle only

            // If the quick picker is open and the mouse is over it, let the picker consume events
            if (pickerOpen && pickerRect.Contains(e.mousePosition))
            {
                if (e.type == EventType.MouseDrag)
                {
                    e.Use();
                }
                return;
            }

            // Claim mouse focus to make icon dragging reliable in Edit mode
            if (e.type == EventType.Layout)
            {
                HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
            }

            if (e.type == EventType.MouseDown && e.button == 0)
            {
                // First check if clicking on a stack badge
                if (sceneRenderer.TryGetStackBadgeClick(e.mousePosition, out var badgeTile, out var badgeEntries))
                {
                    pickerOpen = true;
                    pickerEntries = badgeEntries;
                    pickerTile = badgeTile;
                    Rect tileRect = sceneRenderer.GetTileScreenRect(badgeTile);
                    float width = 240f;
                    float rowH = 22f;
                    float headerH = 18f;
                    int rowsWanted = Mathf.Clamp(badgeEntries.Count, 2, 8);
                    float height = headerH + rowsWanted * rowH + 8f;
                    float x = tileRect.xMax + 8f;
                    if (x + width > Screen.width) x = tileRect.xMin - 8f - width;
                    float y = tileRect.yMin;
                    pickerRect = new Rect(x, y, width, height);
                    pickerHoverIndex = -1;
                    // Pre-highlight: prefer current selection on this tile, otherwise tile's current top entry, otherwise first
                    if (selectedEntry != null && selectedEntry.DisposX == pickerTile.x && selectedEntry.DisposY == pickerTile.y && pickerEntries.Contains(selectedEntry))
                        pickerHighlightEntry = selectedEntry;
                    else
                        pickerHighlightEntry = sceneRenderer.GetTopEntryOnTile(pickerTile) ?? pickerEntries[0];
                    e.Use();
                    return;
                }
                
                // Then try screen-space hit test to get all entries at cursor
                var entries = sceneRenderer.GetEntriesAtScreenPosition(e.mousePosition);
                if (entries == null || entries.Count == 0)
                {
                    // Fallback to plane-space tile hit
                    Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
                    float planeY = sceneRenderer != null ? sceneRenderer.GetBasePlaneY() : 0f;
                    float distance = (planeY - ray.origin.y) / ray.direction.y;
                    if (distance > 0)
                    {
                        Vector3 worldPos = ray.origin + ray.direction * distance;
                        var e1 = sceneRenderer.GetEntryAtPosition(worldPos);
                        if (e1 != null) {
                            entries = new List<DisposEntry> { e1 };
                        } else if (sceneRenderer.TryGetTileFromWorld(worldPos, out var emptyTile)) {
                            // Select an empty tile for placement
                            hasSelectedTile = true;
                            selectedTile = emptyTile;
                            sceneRenderer.SetSelectedTile(emptyTile);
                            SelectEntry(null);
                            e.Use();
                            return;
                        }
                    }
                }

                if (entries != null && entries.Count > 0)
                {
                    if (entries.Count == 1)
                    {
                        SelectEntry(entries[0]); // no pan when selecting via scene
                        if (!e.shift)
                        {
                            isDraggingUnit = true;
                            draggedEntry = entries[0];
                        }
                        e.Use();
                    }
                    else
                    {
                        // Multiple entries on this tile; default to editing the current top unit
                        var tile = new Vector2Int(entries[0].DisposX, entries[0].DisposY);
                        pickerTile = tile; // keep updated for potential future picker opens

                        // Choose the renderer's top entry, or fall back to the first
                        DisposEntry target = sceneRenderer.GetTopEntryOnTile(tile) ?? entries[0];

                        if (target != null)
                        {
                            SelectEntry(target); // no pan when selecting via scene
                            if (!e.shift)
                            {
                                isDraggingUnit = true;
                                draggedEntry = target;
                            }
                        }
                        // Clear any empty tile highlight when selecting a unit
                        hasSelectedTile = false;
                        sceneRenderer.SetSelectedTile(null);
                        e.Use();
                    }
                }
                else
                {
                    // Clicked on empty space - clear selection and any tile highlight
                    SelectEntry(null);
                    hasSelectedTile = false;
                    sceneRenderer.SetSelectedTile(null);
                    e.Use();
                }
            }
            else if (e.type == EventType.MouseDrag && isDraggingUnit && draggedEntry != null)
            {
                Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
                float planeY = sceneRenderer != null ? sceneRenderer.GetBasePlaneY() : 0f;
                float distance = (planeY - ray.origin.y) / ray.direction.y;
                if (distance > 0)
                {
                    Vector3 worldPos = ray.origin + ray.direction * distance;
                    // Begin a grouped undo on first drag event
                    if (!dragUndoActive)
                    {
                        Undo.IncrementCurrentGroup();
                        Undo.SetCurrentGroupName("Move Unit");
                        dragUndoGroup = Undo.GetCurrentGroup();
                        dragUndoActive = true;
                        dragUndoRecorded = false;
                    }
                    // Record only once per drag; subsequent steps skip recording but still move
                    sceneRenderer.MoveEntry(draggedEntry, worldPos, dragUndoRecorded ? null : undoProxy);
                    dragUndoRecorded = true;
                    documentIsDirty = true;
                    RequestRepaintAll();
                    e.Use();
                }
            }
            else if (e.type == EventType.MouseUp)
            {
                isDraggingUnit = false;
                draggedEntry = null;
                if (dragUndoActive)
                {
                    if (dragUndoGroup >= 0)
                    {
                        Undo.CollapseUndoOperations(dragUndoGroup);
                    }
                    dragUndoActive = false;
                    dragUndoGroup = -1;
                    dragUndoRecorded = false;
                }
            }
        }

        private void DrawQuickPicker()
        {
            if (!pickerOpen || pickerEntries == null || pickerEntries.Count <= 1) return;
            
            Handles.BeginGUI();
            // Opaque background and header
            EditorGUI.DrawRect(pickerRect, new Color(0.12f, 0.12f, 0.12f, 1f));
            Rect headerRect = new Rect(pickerRect.x, pickerRect.y, pickerRect.width, 18f);
            EditorGUI.DrawRect(headerRect, new Color(0.18f, 0.18f, 0.18f, 1f));
            GUIStyle headerStyle = new GUIStyle(GUI.skin.label);
            headerStyle.alignment = TextAnchor.MiddleLeft;
            headerStyle.fontStyle = FontStyle.Bold;
            headerStyle.normal.textColor = Color.white;
            headerStyle.padding = new RectOffset(6, 4, 0, 0);
            // Header text (no tooltip) and reserved space for close button
            string headerText = $"{pickerEntries.Count} units here - choose which to edit";
            Rect headerTextRect = new Rect(headerRect.x, headerRect.y, headerRect.width - 22f, headerRect.height);
            GUI.Label(headerTextRect, headerText, headerStyle);
            // Close button on header (top-right)
            Rect closeRect = new Rect(headerRect.xMax - 18f, headerRect.y + 1f, 16f, 16f);
            if (GUI.Button(closeRect, "x"))
            {
                pickerOpen = false;
                Event.current.Use();
                Handles.EndGUI();
                return;
            }

            Rect listRect = new Rect(pickerRect.x + 6, pickerRect.y + headerRect.height + 2, pickerRect.width - 12, pickerRect.height - headerRect.height - 8);
            float rowH = 22f;
            int rows = Mathf.Min(pickerEntries.Count, Mathf.Max(1, Mathf.FloorToInt(listRect.height / rowH)));
            // Default: no hover highlight until we detect it
            sceneRenderer.SetHoverEntry(null);
            for (int i = 0; i < rows; i++)
            {
                var pe = pickerEntries[i];
                Rect r = new Rect(listRect.x, listRect.y + i * rowH, listRect.width, rowH - 2);
                bool hover = r.Contains(Event.current.mousePosition);
                if (hover) pickerHoverIndex = i;
                if (hover)
                {
                    // Preview highlight in scene while hovering
                    sceneRenderer.SetHoverEntry(pe);
                }
                if (pe == pickerHighlightEntry)
                {
                    EditorGUI.DrawRect(r, new Color(0.25f, 0.45f, 0.85f, 0.35f));
                }
                else if (hover)
                {
                    EditorGUI.DrawRect(r, new Color(1f,1f,1f,0.12f));
                }
                string label = DisposDataLoader.Instance.GetUnitDisplayName(pe);
                string diff = DiffString(pe.Flag);
                GUIStyle rowStyle = new GUIStyle(GUI.skin.label);
                if (pe == pickerHighlightEntry) rowStyle.fontStyle = FontStyle.Bold;
                GUI.Label(r, $"{label}  [{pe.Group}]  {diff}", rowStyle);
                if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && hover)
                {
                    SelectEntry(pe);
                    pickerOpen = false;
                    sceneRenderer.SetHoverEntry(null);
                    Event.current.Use();
                }
            }
            if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && !pickerRect.Contains(Event.current.mousePosition))
            {
                pickerOpen = false;
                sceneRenderer.SetHoverEntry(null);
                Event.current.Use();
            }
            Handles.EndGUI();
        }

        private string DiffString(int flag)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            if ((flag & (int)DisposFlags.Normal) != 0) sb.Append("N");
            if ((flag & (int)DisposFlags.Hard) != 0) sb.Append("H");
            if ((flag & (int)DisposFlags.Lunatic) != 0) sb.Append("L");
            return sb.Length == 0 ? "ALL" : sb.ToString();
        }
        
        private void RefreshFileList()
        {
            if (!Directory.Exists(disposFolderPath))
            {
                Debug.LogError($"Dispos folder not found: {disposFolderPath}");
                availableFiles = new string[0];
                return;
            }
            
            var files = Directory.GetFiles(disposFolderPath, "*.xml")
                                .Select(Path.GetFileName)
                                .OrderBy(f => f)
                                .ToArray();
            
            availableFiles = files;
            
            if (selectedFileIndex >= availableFiles.Length)
            {
                selectedFileIndex = availableFiles.Length - 1;
            }
        }
        
        private void LoadFile(int index)
        {
            if (availableFiles == null || index < 0 || index >= availableFiles.Length)
                return;
            
            selectedFileIndex = index;
            EditorPrefs.SetInt("Dispos_SelectedFileIndex", selectedFileIndex);
            string filePath = Path.Combine(disposFolderPath, availableFiles[index]);
            
            currentDocument = DisposDocument.LoadFromFile(filePath);
            documentIsDirty = false;
            
            // Reset Dispos undo proxy so previous file's undo snapshots don't affect the new file,
            // and avoid clearing the global editor undo history (which interferes with other tools).
            if (undoProxy != null)
            {
                DestroyImmediate(undoProxy);
            }
            undoProxy = ScriptableObject.CreateInstance<DisposUndoProxy>();
            undoProxy.hideFlags = HideFlags.HideAndDontSave;
            undoProxy.Document = currentDocument;
            
            if (currentDocument != null && sceneRenderer != null)
            {
                // Try to auto-match terrain based on file name
                AutoSelectTerrain(availableFiles[index]);
                // Restore group UI state
                foreach (var g in currentDocument.Groups)
                {
                    g.IsExpanded = EditorPrefs.GetBool($"Dispos_Group_{g.GroupName}_Expanded", true);
                    g.IsVisible = EditorPrefs.GetBool($"Dispos_Group_{g.GroupName}_Visible", true);
                }
                
                sceneRenderer.RenderDocument(currentDocument, selectedTerrain);
                SceneView.RepaintAll();
            }
            
            selectedEntry = null;
        }
        
        private void SaveDocument()
        {
            if (currentDocument != null)
            {
                currentDocument.SaveToFile();
                documentIsDirty = false;
                EditorUtility.DisplayDialog("Save Complete", "Dispos file saved successfully.", "OK");
            }
        }
        
        // Removed GameObject-driven position sync; moves are handled by scene input
        
        public void MarkDocumentDirty()
        {
            documentIsDirty = true;
            RequestRepaintAll();
        }
        
        // Removed GameObject-based selection sync; selection is managed within the tool
        
        // Removed component-to-entry mapping; no runtime GameObjects are used
        
        private void SelectEntry(DisposEntry entry)
        {
            selectedEntry = entry;
            sceneRenderer.SelectedEntry = entry;
            // Clear any empty tile selection when focusing a unit
            hasSelectedTile = false;
            sceneRenderer.SetSelectedTile(null);
            
            RequestRepaintAll();
        }
        
        private void AutoSelectTerrain(string disposFileName)
        {
            // Try to find a terrain with matching name pattern
            // e.g., S069.xml -> look for terrain containing "S069" or "Fld_S069"
            string baseName = System.IO.Path.GetFileNameWithoutExtension(disposFileName);
            
            string[] guids = AssetDatabase.FindAssets("t:MapTerrain", new[] { "Assets" });
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var adapter = TerrainAssetAdapter.Load(path);
                if (adapter?.Asset != null && adapter.Asset.name.Contains(baseName))
                {
                    selectedTerrain = adapter;
                    break;
                }
            }

            if (currentDocument != null && selectedTerrain != null)
            {
                sceneRenderer.RenderDocument(currentDocument, selectedTerrain);
            }
        }
    }
}
