// -----------------------------------------------------------------------
// UPilot Editor - first setup Agent configuration and completion.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot
{
    public sealed partial class UPilotMainWindow
    {
        private GUIStyle _unsafeModeStyle;

        private void DrawSetupAgentStep()
        {
            EditorGUILayout.HelpBox(
                "选择需要写入的 Agent 规则和项目 MCP 配置。UPilot 只更新自身管理的内容，并保留其它配置。",
                MessageType.Info);

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                _setupWriteAgentRules = EditorGUILayout.ToggleLeft(
                    "写入 Agent 识别规则与 Skill（Codex、Claude Code、Cursor）",
                    _setupWriteAgentRules);
                EditorGUILayout.LabelField(
                    "规则保留其它内容；Codex 与 Cursor 共享 .agents/skills，Claude Code 使用 .claude/skills。",
                    EditorStyles.miniLabel);
            }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("MCP 地址", UPilotAgentSetup.GetMcpUrl(_setupHttpPort), EditorStyles.miniLabel);
                _setupWriteCodexConfig = EditorGUILayout.ToggleLeft("Codex 项目配置", _setupWriteCodexConfig);
                _setupWriteClaudeConfig = EditorGUILayout.ToggleLeft("Claude Code 项目配置", _setupWriteClaudeConfig);
                _setupWriteCursorConfig = EditorGUILayout.ToggleLeft("Cursor 项目配置", _setupWriteCursorConfig);
                EditorGUILayout.LabelField("只更新名为 upilot 的服务，保留其它 MCP 服务。", EditorStyles.miniLabel);
            }

            _setupStartAfterSetup = EditorGUILayout.ToggleLeft("完成后启动 UPilot 服务", _setupStartAfterSetup);
            DrawProjectWriteAccessToggle();

            if (!string.IsNullOrWhiteSpace(_setupCompletionMessage))
                EditorGUILayout.HelpBox(_setupCompletionMessage, _setupCompletionMessageType);

            EditorGUILayout.Space(12);
            using (new EditorGUILayout.HorizontalScope())
            {
                DrawSetupButton(
                    "上一步",
                    SetupNeutralColor,
                    112,
                    () => SetSetupStep(1),
                    !_setupCompletionRunning);
                GUILayout.FlexibleSpace();
                DrawSetupButton(
                    _setupStartAfterSetup ? "写入配置并启动" : "保存设置",
                    SetupReadyColor,
                    168,
                    CompleteSetupAsync,
                    !_setupCompletionRunning);
            }
        }

        private void DrawProjectWriteAccessToggle()
        {
            _unsafeModeStyle ??= new GUIStyle(EditorStyles.label)
            {
                normal = { textColor = new Color(1f, 0.35f, 0.28f) },
            };

            using (new EditorGUILayout.HorizontalScope())
            {
                _setupApproveProjectWrites = EditorGUILayout.Toggle(
                    _setupApproveProjectWrites,
                    GUILayout.Width(18));
                EditorGUILayout.LabelField("允许 Agent 通过 MCP 修改当前 Unity 项目", GUILayout.ExpandWidth(false));
                var previousContentColor = GUI.contentColor;
                GUI.contentColor = new Color(1f, 0.32f, 0.25f);
                EditorGUILayout.LabelField("非 safe 模式", _unsafeModeStyle, GUILayout.Width(88));
                GUI.contentColor = previousContentColor;
                GUILayout.FlexibleSpace();
            }
        }

        private async void CompleteSetupAsync()
        {
            if (_setupCompletionRunning)
                return;

            _setupCompletionRunning = true;
            _setupCompletionMessage = "正在写入配置…";
            _setupCompletionMessageType = MessageType.Info;
            try
            {
                if (_setupStartAfterSetup && UPilotUpdateService.Instance.IsServiceStartBlocked)
                {
                    _setupCompletionMessage = UPilotUpdateService.ServiceStartBlockedMessage;
                    _setupCompletionMessageType = MessageType.Warning;
                    return;
                }

                SaveSetupPorts();
                if (_setupApproveProjectWrites)
                    UPilotProjectConfig.ApproveProjectWriteAccess();
                else
                    UPilotProjectConfig.RevokeProjectWriteAccess();

                if (_setupWriteAgentRules)
                    Debug.Log("[UPilot] First setup agent rules:\n" + UPilotAgentSetup.WriteAgentRules(overwriteExisting: false));
                if (_setupWriteCodexConfig)
                    Debug.Log("[UPilot] First setup Codex MCP config:\n" + UPilotAgentSetup.WriteCodexMcpConfig(promptBeforeOverwrite: true));
                if (_setupWriteClaudeConfig)
                    Debug.Log("[UPilot] First setup Claude MCP config:\n" + UPilotAgentSetup.WriteClaudeCodeMcpConfig(promptBeforeOverwrite: true));
                if (_setupWriteCursorConfig)
                    Debug.Log("[UPilot] First setup Cursor MCP config:\n" + UPilotAgentSetup.WriteCursorMcpConfig(promptBeforeOverwrite: true));

                UPilotAgentSetup.MarkAgentRulesHandledForCurrentProject();

                if (_setupStartAfterSetup)
                {
                    _setupCompletionMessage = "配置已写入，正在启动服务…";
                    var manager = UPilotMcpServerManager.Instance;
                    manager.ValidateAndAutoFixPath();
                    UPilotBridge.Instance.Stop();
                    manager.RestartServer(() => UPilotBridge.Instance.EnsureStarted());

                    var expectedVersion = _setupRuntimeChoice == SetupRuntimeChoice.Managed
                        ? UPilotProjectConfig.Current.runtime?.serverVersion ?? ""
                        : UPilotServerRuntimeService.UpmVersion;
                    var runningVersion = await manager.WaitForServerVersionAsync(expectedVersion, 15000);
                    if (string.IsNullOrWhiteSpace(runningVersion) ||
                        (!string.IsNullOrWhiteSpace(expectedVersion) &&
                         UPilotServerRuntimeService.CompareVersions(runningVersion, expectedVersion) < 0))
                    {
                        _setupCompletionMessage = "服务未能按预期启动，请检查服务日志后重试。";
                        _setupCompletionMessageType = MessageType.Error;
                        return;
                    }
                }

                UPilotSetupState.MarkCompleted();
                _setupCompletionMessage = "设置完成";
                _setupCompletionMessageType = MessageType.Info;
                _setupInitialized = false;
                _mainView = UPilotMainView.Dashboard;
                RefreshAgentConfigs(force: true);
                RefreshSnapshot();
                ShowNotice("UPilot 配置完成并已启动");
            }
            catch (Exception ex)
            {
                _setupCompletionMessage = "设置未完成：" + ex.Message;
                _setupCompletionMessageType = MessageType.Error;
                Debug.LogError("[UPilot] First setup failed: " + ex);
            }
            finally
            {
                _setupCompletionRunning = false;
                Repaint();
            }
        }

    }
}
