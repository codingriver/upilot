// -----------------------------------------------------------------------
// UPilot Editor - first setup view hosted by the main window.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot
{
    public sealed partial class UPilotMainWindow
    {
        private enum UPilotMainView
        {
            Dashboard,
            Setup,
        }

        private enum SetupRuntimeChoice
        {
            Managed,
            Python,
        }

        private static readonly Color SetupReadyColor = new(0.30f, 0.72f, 0.36f);
        private static readonly Color SetupBlockedColor = new(0.90f, 0.30f, 0.25f);
        private static readonly Color SetupNeutralColor = new(0.65f, 0.65f, 0.65f);
        internal const bool SetupApprovesProjectWritesByDefault = true;

        private UPilotMainView _mainView;
        private int _setupStep;
        private bool _setupInitialized;
        private bool _setupCompletionRunning;
        private string _setupCompletionMessage = "";
        private MessageType _setupCompletionMessageType = MessageType.None;
        private Vector2 _setupScroll;

        private string _setupHost = UPilotBridge.DefaultWsHost;
        private int _setupWsPort = UPilotBridge.DefaultWsPort;
        private int _setupHttpPort = UPilotBridge.DefaultHttpPort;
        private bool _setupPortsReady;
        private int _recommendedWsPort;
        private int _recommendedHttpPort;
        private string _setupPortMessage = "";
        private MessageType _setupPortMessageType = MessageType.None;

        private SetupRuntimeChoice _setupRuntimeChoice = SetupRuntimeChoice.Managed;
        private UPilotReleaseManifest _setupManifest;
        private bool _setupManifestLoading;
        private string _setupManifestError = "";
        private UPilotPythonProbeResult _setupPythonProbe;
        private bool _showPythonAdvanced;

        private bool _setupWriteAgentRules = true;
        private bool _setupWriteCodexConfig = true;
        private bool _setupWriteClaudeConfig = true;
        private bool _setupWriteCursorConfig = true;
        private bool _setupStartAfterSetup = true;
        private bool _setupApproveProjectWrites = SetupApprovesProjectWritesByDefault;

        private void EnterSetupView()
        {
            _mainView = UPilotMainView.Setup;
            if (!_setupInitialized)
                InitializeSetupState();
            Repaint();
        }

        private void InitializeSetupState()
        {
            _setupInitialized = true;
            _setupStep = 0;
            _setupRuntimeChoice = UPilotServerRuntimeService.IsSourceUpdateChannel()
                ? SetupRuntimeChoice.Python
                : SetupRuntimeChoice.Managed;
            _showPythonAdvanced = _setupRuntimeChoice == SetupRuntimeChoice.Python;
            _setupCompletionMessage = "";
            _setupCompletionMessageType = MessageType.None;

            var bridge = UPilotBridge.Instance;
            _setupHost = string.IsNullOrWhiteSpace(bridge.WsHost)
                ? UPilotBridge.DefaultWsHost
                : bridge.WsHost;
            _setupWsPort = bridge.WsPort > 0 ? bridge.WsPort : UPilotBridge.DefaultWsPort;
            _setupHttpPort = bridge.HttpPort > 0 ? bridge.HttpPort : UPilotBridge.DefaultHttpPort;
            EvaluateSetupPorts();
        }

        private void DrawSetupView()
        {
            _setupScroll = EditorGUILayout.BeginScrollView(_setupScroll);
            try
            {
                using (new EditorGUILayout.VerticalScope(new GUIStyle { padding = new RectOffset(10, 10, 8, 10) }))
                {
                    DrawSetupHeader();
                    EditorGUILayout.Space(8);
                    DrawSetupProgress();
                    EditorGUILayout.Space(8);

                    if (_setupStep == 0)
                        DrawSetupPortStep();
                    else if (_setupStep == 1)
                        DrawSetupRuntimeStep();
                    else
                        DrawSetupAgentStep();
                }
            }
            finally
            {
                EditorGUILayout.EndScrollView();
            }
        }

        private void DrawSetupHeader()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("UPilot 首次设置", _titleStyle);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("返回", GUILayout.Width(64), GUILayout.Height(24)))
                {
                    _mainView = UPilotMainView.Dashboard;
                    RefreshSnapshot();
                }
            }
            EditorGUILayout.LabelField("完成端口、服务和 Agent 配置后即可开始使用。", _messageStyle);
        }

        private void DrawSetupProgress()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                DrawSetupProgressItem(0, "1  网络端口");
                DrawSetupProgressItem(1, "2  服务安装");
                DrawSetupProgressItem(2, "3  Agent 配置");
            }
        }

        private void DrawSetupProgressItem(int step, string label)
        {
            var previous = GUI.backgroundColor;
            GUI.backgroundColor = step == _setupStep
                ? SetupReadyColor
                : step < _setupStep ? new Color(0.45f, 0.65f, 0.48f) : SetupNeutralColor;
            GUILayout.Label(label, GUI.skin.button, GUILayout.Height(28));
            GUI.backgroundColor = previous;
        }

        private void SetSetupStep(int step)
        {
            _setupStep = Mathf.Clamp(step, 0, 2);
            _setupScroll = Vector2.zero;
            if (_setupStep == 1)
                BeginRuntimeInspection();
        }

        private static void DrawSetupButton(
            string label,
            Color color,
            float width,
            Action action,
            bool enabled = true)
        {
            using (new EditorGUI.DisabledScope(!enabled))
            {
                var previous = GUI.backgroundColor;
                try
                {
                    GUI.backgroundColor = color;
                    if (GUILayout.Button(label, GUILayout.Width(width), GUILayout.Height(52)))
                        action?.Invoke();
                }
                finally
                {
                    GUI.backgroundColor = previous;
                }
            }
        }

        private void SaveSetupPorts()
        {
            if (string.IsNullOrWhiteSpace(_setupHost))
                _setupHost = UPilotBridge.DefaultWsHost;

            var bridge = UPilotBridge.Instance;
            bridge.SetWsEndpoint(_setupHost, _setupWsPort);
            bridge.HttpPort = _setupHttpPort;
        }

        private static string FormatSetupBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " KB";
            return (bytes / 1024.0 / 1024.0).ToString("F1") + " MB";
        }
    }
}
