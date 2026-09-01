// -----------------------------------------------------------------------
// UPilot Editor - shared scrollable message and confirmation dialog.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot
{
    /// <summary>
    /// UPilot shared scrollable message and confirmation dialog.
    /// </summary>
    public sealed class UPilotScrollableDialog : EditorWindow
    {
        private string _message;
        private string _copyText;
        private string _confirmButtonText = "OK";
        private string _cancelButtonText;
        private bool _richText;
        private bool _showCancelButton;
        private bool _confirmed;
        private Vector2 _scroll;

        public static void ShowDialog(string title, string message)
        {
            ShowDialog(title, message, false, null);
        }

        /// <summary>
        /// Shows a non-modal scrollable message window.
        /// When richText is true, message supports Unity rich-text tags.
        /// copyText is the plain text copied by the Copy All button; when omitted,
        /// it falls back to the message with rich-text tags removed.
        /// </summary>
        public static void ShowDialog(string title, string message, bool richText, string copyText)
        {
            var window = CreateDialog(title, message, richText, copyText);
            window.ShowUtility();
        }

        /// <summary>
        /// Shows a modal scrollable confirmation window. Returns true only when
        /// the confirm button is clicked; canceling or closing the window returns false.
        /// </summary>
        public static bool ShowConfirmDialog(
            string title,
            string message,
            string confirmButtonText,
            string cancelButtonText,
            bool richText = false,
            string copyText = null)
        {
            var window = CreateDialog(title, message, richText, copyText);
            window._confirmButtonText = string.IsNullOrEmpty(confirmButtonText) ? "确定" : confirmButtonText;
            window._cancelButtonText = string.IsNullOrEmpty(cancelButtonText) ? "取消" : cancelButtonText;
            window._showCancelButton = true;
            window.ShowModalUtility();
            return window._confirmed;
        }

        private static UPilotScrollableDialog CreateDialog(
            string title,
            string message,
            bool richText,
            string copyText)
        {
            var window = CreateInstance<UPilotScrollableDialog>();
            window.titleContent = new GUIContent(title);
            window._message = message ?? string.Empty;
            window._richText = richText;
            window._copyText = copyText ?? StripRichText(window._message);
            window.minSize = new Vector2(640f, 420f);
            window.position = new Rect(
                Screen.currentResolution.width * 0.5f - 320f,
                Screen.currentResolution.height * 0.5f - 260f,
                720f,
                520f);
            return window;
        }

        private static string StripRichText(string text)
        {
            return string.IsNullOrEmpty(text)
                ? string.Empty
                : System.Text.RegularExpressions.Regex.Replace(text, "<.*?>", string.Empty);
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(8f);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(8f);
                EditorGUILayout.LabelField(titleContent.text, EditorStyles.boldLabel);
                GUILayout.Space(8f);
            }

            EditorGUILayout.Space(4f);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(8f);
                _scroll = EditorGUILayout.BeginScrollView(
                    _scroll,
                    GUI.skin.box,
                    GUILayout.ExpandHeight(true));

                var style = new GUIStyle(EditorStyles.wordWrappedLabel)
                {
                    richText = _richText,
                    padding = new RectOffset(8, 8, 8, 8),
                };
                var content = new GUIContent(_message);
                var width = Mathf.Max(100f, position.width - 48f);
                var height = Mathf.Max(position.height - 116f, style.CalcHeight(content, width) + 20f);
                if (_richText)
                {
                    // SelectableLabel does not render rich text reliably. Rich-text
                    // messages use a read-only label and remain copyable via Copy All.
                    EditorGUILayout.LabelField(_message, style, GUILayout.MinHeight(height));
                }
                else
                {
                    EditorGUILayout.SelectableLabel(_message, style, GUILayout.MinHeight(height));
                }
                EditorGUILayout.EndScrollView();
                GUILayout.Space(8f);
            }

            EditorGUILayout.Space(8f);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("复制全部", GUILayout.Width(90f), GUILayout.Height(26f)))
                    EditorGUIUtility.systemCopyBuffer = _copyText;

                if (GUILayout.Button(_confirmButtonText, GUILayout.Width(90f), GUILayout.Height(26f)))
                {
                    _confirmed = true;
                    Close();
                }

                if (_showCancelButton &&
                    GUILayout.Button(_cancelButtonText, GUILayout.Width(90f), GUILayout.Height(26f)))
                {
                    _confirmed = false;
                    Close();
                }
                GUILayout.Space(8f);
            }
            EditorGUILayout.Space(8f);
        }
    }
}
