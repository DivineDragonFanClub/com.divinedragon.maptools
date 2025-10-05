using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DivineDragon.MapTools
{
    // Reusable, keyboard-first quick search popup for arbitrary item types.
    public class QuickSearchWindow : EditorWindow
    {
        private string titleText;
        private string search = "";
        private Vector2 scroll;
        private bool requestFocus;

        private Func<string, List<object>> provider;
        private Func<object, string> primaryText;   // left/main label (bigger)
        private Func<object, string> rightText;     // right small label (e.g., id)
        private Action<object> onSelect;

        private List<object> cached = new List<object>();
        private string lastQuery = "";
        private int selectedIndex = 0;

        public static void Show<T>(string title,
                                   Func<string, List<T>> provider,
                                   Func<T, string> primaryText,
                                   Func<T, string> rightText,
                                   Action<T> onSelect,
                                   string initialQuery = "")
        {
            var win = CreateInstance<QuickSearchWindow>();
            win.titleText = title;
            win.titleContent = new GUIContent(title);
            win.minSize = new Vector2(420, 300);
            win.requestFocus = true;
            win.search = initialQuery ?? "";
            // Wrap generics into object delegates
            win.provider = q => provider(q).Cast<object>().ToList();
            win.primaryText = o => primaryText((T)o);
            win.rightText = o => rightText != null ? rightText((T)o) : null;
            win.onSelect = o => onSelect((T)o);
            win.ShowUtility();
        }

        private void RefreshIfNeeded()
        {
            if (search != lastQuery)
            {
                cached = provider != null ? (provider(search) ?? new List<object>()) : new List<object>();
                lastQuery = search;
                selectedIndex = Mathf.Clamp(selectedIndex, 0, Mathf.Max(0, cached.Count - 1));
            }
        }

        private void HandleKeyboard()
        {
            var e = Event.current;
            if (e.type != EventType.KeyDown) return;
            if (e.keyCode == KeyCode.DownArrow) { selectedIndex = Mathf.Clamp(selectedIndex + 1, 0, Mathf.Max(0, cached.Count - 1)); e.Use(); }
            else if (e.keyCode == KeyCode.UpArrow) { selectedIndex = Mathf.Clamp(selectedIndex - 1, 0, Mathf.Max(0, cached.Count - 1)); e.Use(); }
            else if (e.keyCode == KeyCode.PageDown) { selectedIndex = Mathf.Clamp(selectedIndex + 10, 0, Mathf.Max(0, cached.Count - 1)); e.Use(); }
            else if (e.keyCode == KeyCode.PageUp) { selectedIndex = Mathf.Clamp(selectedIndex - 10, 0, Mathf.Max(0, cached.Count - 1)); e.Use(); }
            else if (e.keyCode == KeyCode.Home) { selectedIndex = 0; e.Use(); }
            else if (e.keyCode == KeyCode.End) { selectedIndex = Mathf.Max(0, cached.Count - 1); e.Use(); }
            else if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
            {
                if (cached.Count > 0)
                {
                    onSelect?.Invoke(cached[Mathf.Clamp(selectedIndex, 0, cached.Count - 1)]);
                    Close();
                }
                e.Use();
            }
            else if (e.keyCode == KeyCode.Escape) { Close(); e.Use(); }
        }

        private void OnGUI()
        {
            HandleKeyboard();

            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label("Search:", GUILayout.Width(48));
            GUI.SetNextControlName("SearchField");
            string newSearch = GUILayout.TextField(search, EditorStyles.toolbarTextField, GUILayout.ExpandWidth(true), GUILayout.MinWidth(320));
            if (requestFocus)
            {
                EditorGUI.FocusTextInControl("SearchField");
                requestFocus = false;
            }
            if (newSearch != search)
            {
                search = newSearch;
                selectedIndex = 0;
            }
            if (GUILayout.Button("X", EditorStyles.toolbarButton, GUILayout.Width(24)))
            {
                search = string.Empty;
                GUI.FocusControl(null);
            }
            EditorGUILayout.EndHorizontal();

            RefreshIfNeeded();

            scroll = EditorGUILayout.BeginScrollView(scroll);
            float rowH = 22f;
            for (int i = 0; i < cached.Count; i++)
            {
                var item = cached[i];
                Rect rowRect = EditorGUILayout.BeginHorizontal(GUILayout.Height(rowH));
                if (i == selectedIndex)
                {
                    EditorGUI.DrawRect(rowRect, new Color(0.25f, 0.45f, 0.85f, 0.25f));
                }
                string left = primaryText != null ? primaryText(item) : item?.ToString();
                if (GUILayout.Button(left ?? "(null)", GUILayout.MinWidth(220)))
                {
                    onSelect?.Invoke(item);
                    Close();
                }
                string right = rightText != null ? rightText(item) : null;
                if (!string.IsNullOrEmpty(right))
                {
                    GUILayout.FlexibleSpace();
                    GUILayout.Label(right, EditorStyles.miniLabel);
                }
                EditorGUILayout.EndHorizontal();

                if (i == selectedIndex)
                {
                    float top = i * rowH;
                    float bottom = top + rowH;
                    float viewTop = scroll.y;
                    float viewBottom = scroll.y + position.height - 40f;
                    if (top < viewTop) scroll.y = top;
                    else if (bottom > viewBottom) scroll.y = bottom - (position.height - 40f);
                }
            }
            EditorGUILayout.EndScrollView();
        }
    }
}

