using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace CodingRiver.UPilot.Flow.Examples
{
    /// <summary>
    /// A simple IMGUI editor window for demonstrating IMGUI automation capabilities.
    /// </summary>
    public sealed class ImguiExampleWindow : EditorWindow
    {
        private string _projectName = "";
        private bool _enableFeature = false;
        private int _qualityIndex = 0;
        private float _scaleValue = 1.0f;
        private string _statusLabel = "Ready";
        private int _doubleClickCount = 0;
        private float _lastClickTime = 0f;
        private const float DoubleClickThreshold = 0.3f;

        private readonly string[] _qualityOptions = { "Low", "Medium", "High", "Ultra" };

        [MenuItem("UPilot/Flow/Examples/IMGUI Example Window")]
        public static void ShowWindow()
        {
            var window = GetWindow<ImguiExampleWindow>();
            window.titleContent = new GUIContent("IMGUI Example");
            window.minSize = new Vector2(400, 300);
            window.Show();
        }

        private void OnGUI()
        {
            Codingriver.Logger.Log($"[ImguiExampleWindow] OnGUI event={Event.current?.type} mousePos={Event.current?.mousePosition}");
            GUILayout.BeginVertical("box");
            GUILayout.Label("IMGUI Automation Demo", EditorStyles.boldLabel);
            GUILayout.EndVertical();

            GUILayout.Space(10);

            // Project name field
            GUILayout.BeginHorizontal();
            GUILayout.Label("Project Name:", GUILayout.Width(100));
            GUI.SetNextControlName("project-name-field");
            _projectName = GUILayout.TextField(_projectName, GUILayout.Width(250));
            GUILayout.EndHorizontal();

            GUILayout.Space(8);

            // Feature toggle
            GUILayout.BeginHorizontal();
            GUILayout.Label("Enable Feature:", GUILayout.Width(100));
            GUI.SetNextControlName("feature-toggle");
            _enableFeature = GUILayout.Toggle(_enableFeature, "");
            GUILayout.EndHorizontal();

            GUILayout.Space(8);

            // Quality popup
            GUILayout.BeginHorizontal();
            GUILayout.Label("Quality:", GUILayout.Width(100));
            GUI.SetNextControlName("quality-popup");
            _qualityIndex = EditorGUILayout.Popup(_qualityIndex, _qualityOptions, GUILayout.Width(250));
            GUILayout.EndHorizontal();

            GUILayout.Space(8);

            // Scale slider
            GUILayout.BeginHorizontal();
            GUILayout.Label("Scale:", GUILayout.Width(100));
            GUI.SetNextControlName("scale-slider");
            _scaleValue = GUILayout.HorizontalSlider(_scaleValue, 0.5f, 2.0f, GUILayout.Width(200));
            GUILayout.Label(_scaleValue.ToString("F2"), GUILayout.Width(50));
            GUILayout.EndHorizontal();

            GUILayout.Space(16);

            // Action buttons
            GUILayout.BeginHorizontal();
            GUI.SetNextControlName("generate-button");
            bool generateClicked = GUILayout.Button("Generate", GUILayout.Width(120), GUILayout.Height(30));
            Codingriver.Logger.Log($"[ImguiExampleWindow] GUILayout.Button Generate returned {generateClicked}, event={Event.current?.type}, hotControl={GUIUtility.hotControl}");
            if (generateClicked)
            {
                float now = Time.realtimeSinceStartup;
                if (now - _lastClickTime < DoubleClickThreshold)
                {
                    _doubleClickCount++;
                }
                _lastClickTime = now;
                OnGenerateClicked();
            }

            GUI.SetNextControlName("reset-button");
            if (GUILayout.Button("Reset", GUILayout.Width(120), GUILayout.Height(30)))
            {
                OnResetClicked();
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(16);

            // Status label
            GUILayout.BeginVertical("box");
            GUI.SetNextControlName("status-label");
            GUILayout.Label($"Status: {_statusLabel}", EditorStyles.wordWrappedLabel);
            GUILayout.EndVertical();

            // Scroll view demo
            GUILayout.Space(10);
            GUILayout.Label("Log Output", EditorStyles.boldLabel);
            _scrollPosition = GUILayout.BeginScrollView(_scrollPosition, GUILayout.Height(80));
            GUILayout.Label(_logText, EditorStyles.miniLabel);
            GUILayout.EndScrollView();
        }

        private Vector2 _scrollPosition;
        private string _logText = "Waiting for actions...\n";

        private void OnGenerateClicked()
        {
            _statusLabel = $"Generated: {_projectName}, Quality={_qualityOptions[_qualityIndex]}, Scale={_scaleValue:F2}, DoubleClicks={_doubleClickCount}";
            _logText += $"[{System.DateTime.Now:HH:mm:ss}] Generated project: {_projectName}\n";
            if (_enableFeature)
            {
                _logText += $"  Feature enabled.\n";
            }
            Codingriver.Logger.Log($"[ImguiExampleWindow] OnGenerateClicked called! status={_statusLabel}");
        }

        private void OnResetClicked()
        {
            _projectName = "";
            _enableFeature = false;
            _qualityIndex = 0;
            _scaleValue = 1.0f;
            _statusLabel = "Reset";
            _logText += $"[{System.DateTime.Now:HH:mm:ss}] Reset all fields.\n";
        }

        /// <summary>
        /// Resets the window to initial state for automated tests.
        /// </summary>
        public void ResetForTest()
        {
            _projectName = "";
            _enableFeature = false;
            _qualityIndex = 0;
            _scaleValue = 1.0f;
            _statusLabel = "Ready";
            _doubleClickCount = 0;
            _lastClickTime = 0f;
            _logText = "";
            _scrollPosition = Vector2.zero;
            Repaint();
        }
    }
}
