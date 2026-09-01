// -----------------------------------------------------------------------
// UPilot Editor - first setup port selection.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot
{
    public sealed partial class UPilotMainWindow
    {
        private void DrawSetupPortStep()
        {
            RefreshSetupPortsForRunningService();
            EditorGUILayout.HelpBox(
                "为当前 Unity 项目设置独立端口。同一台电脑运行多个 Unity 项目时，每个项目需要使用不同端口。",
                MessageType.Info);

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUI.BeginChangeCheck();
                _setupHost = EditorGUILayout.TextField("Host", _setupHost);
                _setupWsPort = EditorGUILayout.IntField("Unity Bridge WS 端口", _setupWsPort);
                _setupHttpPort = EditorGUILayout.IntField(McpPortLabel, _setupHttpPort);
                if (EditorGUI.EndChangeCheck())
                    EvaluateSetupPorts();

                if (!string.IsNullOrEmpty(_setupPortMessage))
                    EditorGUILayout.HelpBox(_setupPortMessage, _setupPortMessageType);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("重新检测", GUILayout.Height(26)))
                        EvaluateSetupPorts();

                    using (new EditorGUI.DisabledScope(_setupPortsReady || _recommendedWsPort <= 0 || _recommendedHttpPort <= 0))
                    {
                        if (GUILayout.Button("使用推荐端口", GUILayout.Height(26)))
                            ApplyRecommendedSetupPorts();
                    }
                }
            }

            EditorGUILayout.Space(12);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                DrawSetupButton(
                    "下一步",
                    _setupPortsReady ? SetupReadyColor : SetupBlockedColor,
                    132,
                    ContinueFromPortStep);
            }
        }

        private void RefreshSetupPortsForRunningService()
        {
            if (_setupPortsReady)
                return;

            var manager = UPilotMcpServerManager.Instance;
            if (_setupWsPort != manager.WsPort || _setupHttpPort != manager.HttpPort)
                return;

            var status = manager.GetStatus();
            if (status.WsPortListening && status.HttpPortListening)
                EvaluateSetupPorts();
        }

        private void EvaluateSetupPorts()
        {
            _setupPortsReady = false;
            _recommendedWsPort = 0;
            _recommendedHttpPort = 0;

            if (_setupWsPort <= 0 || _setupHttpPort <= 0 || _setupWsPort > 65535 || _setupHttpPort > 65535)
            {
                _setupPortMessage = "端口必须在 1 到 65535 之间。";
                _setupPortMessageType = MessageType.Error;
                FindRecommendedSetupPorts();
                return;
            }

            if (_setupWsPort == _setupHttpPort)
            {
                _setupPortMessage = "WS 端口和 HTTP 端口不能相同。";
                _setupPortMessageType = MessageType.Error;
                FindRecommendedSetupPorts();
                return;
            }

            var manager = UPilotMcpServerManager.Instance;
            var serviceStatus = manager.GetStatus();
            var wsAvailable = UPilotPortAllocator.IsPortAvailable(_setupWsPort) ||
                              (_setupWsPort == manager.WsPort && serviceStatus.WsPortListening);
            var httpAvailable = UPilotPortAllocator.IsPortAvailable(_setupHttpPort) ||
                                (_setupHttpPort == manager.HttpPort && serviceStatus.HttpPortListening);
            _setupPortsReady = wsAvailable && httpAvailable;
            if (_setupPortsReady)
            {
                _setupPortMessage = $"当前端口可用：WS {_setupWsPort}，HTTP {_setupHttpPort}";
                _setupPortMessageType = MessageType.Info;
                return;
            }

            FindRecommendedSetupPorts();
            var conflicts = !wsAvailable && !httpAvailable
                ? $"WS {_setupWsPort} 和 HTTP {_setupHttpPort}"
                : !wsAvailable ? $"WS {_setupWsPort}" : $"HTTP {_setupHttpPort}";
            _setupPortMessage =
                $"检测到端口冲突：{conflicts} 已被占用。推荐使用 WS {_recommendedWsPort}，HTTP {_recommendedHttpPort}。";
            _setupPortMessageType = MessageType.Error;
        }

        private void FindRecommendedSetupPorts()
        {
            var startWs = _setupWsPort > 0 && _setupWsPort <= 65535
                ? _setupWsPort
                : UPilotBridge.DefaultWsPort;
            var startHttp = _setupHttpPort > 0 && _setupHttpPort <= 65535
                ? _setupHttpPort
                : UPilotBridge.DefaultHttpPort;
            var pair = UPilotPortAllocator.FindAvailablePair(startWs, startHttp);
            _recommendedWsPort = pair.wsPort;
            _recommendedHttpPort = pair.httpPort;
        }

        private void ApplyRecommendedSetupPorts()
        {
            if (_recommendedWsPort <= 0 || _recommendedHttpPort <= 0)
                FindRecommendedSetupPorts();
            _setupWsPort = _recommendedWsPort;
            _setupHttpPort = _recommendedHttpPort;
            EvaluateSetupPorts();
        }

        private void ContinueFromPortStep()
        {
            if (!_setupPortsReady)
            {
                if (!EditorUtility.DisplayDialog(
                        "自动分配可用端口？",
                        $"当前端口存在冲突。UPilot 将重新检测并使用推荐端口 WS {_recommendedWsPort}、HTTP {_recommendedHttpPort}。",
                        "使用推荐端口并继续",
                        "取消"))
                    return;

                ApplyRecommendedSetupPorts();
                if (!_setupPortsReady)
                    return;
            }

            SaveSetupPorts();
            SetSetupStep(1);
        }
    }
}
