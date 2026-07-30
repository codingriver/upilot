// -----------------------------------------------------------------------
// UPilot Editor - user-facing update center.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Reflection;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace CodingRiver.UPilot
{
    public sealed class UPilotUpdateWindow : EditorWindow
    {
        private UPilotReleaseManifest _manifest;
        private Action<string, MessageType> _externalNotice;
        private bool _isChecking;
        private bool _operationRunning;
        private bool _showDetails;
        private string _error = "";
        private string _notice = "";
        private MessageType _noticeType = MessageType.Info;

        public static void Open(Action<string, MessageType> notice = null)
        {
            var window = GetWindow<UPilotUpdateWindow>(true, "UPilot 更新中心", true);
            window.minSize = new Vector2(440, 360);
            window._externalNotice = notice;
            window.Show();
            window.Focus();
            window.CheckForUpdates();
        }

        private void OnEnable()
        {
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
        }

        private void OnEditorUpdate()
        {
            var status = UPilotUpdateService.Instance.GetOperationStatus();
            var downloadRunning = UPilotServerRuntimeService.Instance.DownloadState.IsRunning;
            if (_operationRunning && !status.IsRunning && !downloadRunning)
                _operationRunning = false;

            if (_operationRunning || status.IsRunning || downloadRunning)
                Repaint();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(8);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(10);
                EditorGUILayout.LabelField("UPilot 更新中心", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(_isChecking || IsUpdateBusy()))
                {
                    if (GUILayout.Button("重新检查", EditorStyles.miniButton, GUILayout.Width(72)))
                        CheckForUpdates();
                }
                GUILayout.Space(10);
            }

            EditorGUILayout.Space(4);
            using (new EditorGUILayout.VerticalScope(new GUIStyle { padding = new RectOffset(12, 12, 0, 8) }))
            {
                if (_isChecking)
                {
                    EditorGUILayout.HelpBox("正在检查更新…", MessageType.Info);
                    return;
                }

                if (!string.IsNullOrEmpty(_error))
                {
                    EditorGUILayout.HelpBox("检查更新失败：" + _error, MessageType.Error);
                    DrawFooter();
                    return;
                }

                if (_manifest == null)
                {
                    EditorGUILayout.HelpBox("尚未获取更新信息。", MessageType.Info);
                    DrawFooter();
                    return;
                }

                DrawUpdateContent();
                DrawFooter();
            }
        }

        private void DrawUpdateContent()
        {
            var runtime = UPilotServerRuntimeService.Instance;
            var operationStatus = UPilotUpdateService.Instance.GetOperationStatus();
            var mode = runtime.GetConfiguredMode();
            var package = PackageInfo.FindForAssembly(typeof(UPilotBridge).Assembly);
            var currentUpm = UPilotServerRuntimeService.UpmVersion;
            var managedRuntime = mode == UPilotServerRuntimeMode.StandaloneExe;
            var currentServer = managedRuntime ? GetCurrentServerVersion(mode) : currentUpm;
            var mainChannel = IsMainChannel(_manifest.Channel) ||
                              IsMainChannel(UPilotServerRuntimeService.ResolveUpdateChannel());
            var localPackage = package != null &&
                               (package.source == PackageSource.Local || package.source == PackageSource.Embedded);
            var upmNeedsUpdate = NeedsPackageUpdate(package, currentUpm, mainChannel, localPackage);
            var serverVersionUnknown = managedRuntime && string.IsNullOrWhiteSpace(currentServer);
            var serverNeedsUpdate = managedRuntime && !string.IsNullOrWhiteSpace(currentServer) &&
                                    UPilotServerRuntimeService.IsVersionNewer(_manifest.ServerVersion, currentServer);
            var serverBlockedByPackage = managedRuntime && (serverNeedsUpdate || serverVersionUnknown) &&
                                         !UPilotServerRuntimeService.IsVersionAtLeast(
                                             currentUpm,
                                             _manifest.MinCompatibleUpm);
            var managedServerNeedsAction = managedRuntime && (serverNeedsUpdate || serverVersionUnknown);
            var includeManagedServerInPrimaryAction = managedServerNeedsAction &&
                                                      (!serverBlockedByPackage || upmNeedsUpdate);
            var primaryActionAvailable = upmNeedsUpdate || includeManagedServerInPrimaryAction;
            var primaryActionBlocked = managedServerNeedsAction && serverBlockedByPackage && !upmNeedsUpdate;
            var updateBusy = IsUpdateBusy() || operationStatus.IsRunning;

            DrawInfoRow("更新通道", mainChannel ? "Main 分支版" : "正式版");
            EditorGUILayout.Space(4);

            var hasAction = primaryActionAvailable || primaryActionBlocked;
            if (localPackage && !managedRuntime)
            {
                EditorGUILayout.HelpBox("当前使用本地开发包，未执行远程版本比较", MessageType.Info);
            }
            else
            {
                var summary = hasAction
                    ? BuildSummary(
                        upmNeedsUpdate,
                        managedServerNeedsAction,
                        serverBlockedByPackage,
                        serverVersionUnknown)
                    : localPackage
                        ? "当前使用本地开发包，MCP 服务已是最新"
                        : "已是最新版本";
                EditorGUILayout.HelpBox(summary, hasAction ? MessageType.Warning : MessageType.Info);
            }

            DrawSectionTitle("UPilot 包");
            if (localPackage)
            {
                DrawInfoRow("当前版本", currentUpm + "（本地开发）");
                DrawStatusAction("更新状态", "本地开发包，不自动检查", null, null, false);
            }
            else
            {
                DrawInfoRow("当前版本", BuildPackageVersion(currentUpm, package, mainChannel));
                DrawInfoRow("最新版本", BuildLatestPackageVersion(mainChannel));
                if (upmNeedsUpdate)
                {
                    DrawStatusAction(
                        "状态",
                        mainChannel ? "可同步 Main 分支最新版本" : "有新版本可用",
                        null,
                        null,
                        false);
                }
                else
                {
                    DrawStatusAction("状态", "已是最新", null, null, false);
                }
            }

            DrawSeparator();
            DrawSectionTitle("MCP 服务");
            DrawInfoRow("运行方式", managedRuntime ? "自动管理（推荐）" : "本机 Python");

            if (!managedRuntime)
            {
                DrawInfoRow("服务来源", "当前 UPilot 包");
                DrawInfoRow("当前版本", currentUpm);
                DrawInfoRow("更新方式", "随 UPilot 包更新");
                DrawStatusAction("状态", "无需单独更新", null, null, false);
            }
            else
            {
                DrawInfoRow("当前版本", string.IsNullOrWhiteSpace(currentServer) ? "暂未获取" : currentServer);
                DrawInfoRow("最新版本", _manifest.ServerVersion);

                if (serverBlockedByPackage)
                {
                    DrawStatusAction("状态", "请先更新 UPilot 包", null, null, false);
                }
                else if (serverNeedsUpdate)
                {
                    DrawStatusAction("状态", "有新版本可用", null, null, false);
                }
                else if (serverVersionUnknown)
                {
                    DrawStatusAction("状态", "暂无法确认当前版本", null, null, false);
                }
                else
                {
                    DrawStatusAction("状态", "已是最新", null, null, false);
                }
            }

            DrawPrimaryUpdateAction(
                upmNeedsUpdate,
                includeManagedServerInPrimaryAction,
                primaryActionBlocked,
                mainChannel,
                updateBusy);
            DrawOperationStatus(operationStatus);
            DrawDownloadProgress();

            if (!string.IsNullOrWhiteSpace(_notice))
                EditorGUILayout.HelpBox(_notice, _noticeType);

            EditorGUILayout.Space(3);
            _showDetails = EditorGUILayout.Foldout(_showDetails, "详细信息", true);
            if (_showDetails)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    DrawInfoRow("最低包版本", EmptyAsDash(_manifest.MinCompatibleUpm));
                    DrawInfoRow("最低服务版本", EmptyAsDash(_manifest.MinCompatibleServer));
                    DrawInfoRow("协议版本", EmptyAsDash(_manifest.ProtocolVersion));
                    DrawInfoRow("构建提交", ShortCommit(_manifest.CommitSha));
                    DrawCopyableAddressRow();
                }
            }
        }

        private void DrawDownloadProgress()
        {
            var state = UPilotServerRuntimeService.Instance.DownloadState;
            if (!state.IsRunning)
                return;

            EditorGUILayout.Space(4);
            var rect = EditorGUILayout.GetControlRect(false, 18);
            EditorGUI.ProgressBar(rect, state.Progress, UPilotUpdateService.FormatDownloadProgressLabel(state));
            EditorGUILayout.LabelField(UPilotUpdateService.FormatDownloadProgressDetail(state), EditorStyles.miniLabel);
        }

        private void DrawFooter()
        {
            EditorGUILayout.Space(8);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("关闭", GUILayout.Width(72)))
                    Close();
            }
        }

        private async void CheckForUpdates()
        {
            if (_isChecking)
                return;

            _isChecking = true;
            _error = "";
            _notice = "";
            Repaint();
            try
            {
                var manifestUrl = UPilotServerRuntimeService.ResolveManifestUrl();
                Debug.Log($"[UPilot] CheckForUpdates channel={UPilotServerRuntimeService.ResolveUpdateChannel()}, manifestUrl={manifestUrl}");
                _manifest = await UPilotServerRuntimeService.Instance.FetchReleaseManifestAsync(manifestUrl);
            }
            catch (Exception ex)
            {
                _manifest = null;
                _error = ex.Message;
            }
            finally
            {
                _isChecking = false;
                Repaint();
            }
        }

        private void UpdatePackage()
        {
            _operationRunning = true;
            UPilotUpdateService.Instance.UpdateUpmFromManifest(HandleOperationNotice);
        }

        private void UpdateManagedService()
        {
            _operationRunning = true;
            UPilotUpdateService.Instance.UpdateManagedServerAndRestart(HandleOperationNotice);
        }

        private void UpdateSelected(bool updatePackage, bool updateManagedServer)
        {
            _operationRunning = true;
            UPilotUpdateService.Instance.UpdateFromManifest(
                updatePackage,
                updateManagedServer,
                HandleOperationNotice);
        }

        private void HandleOperationNotice(string message, MessageType type)
        {
            _notice = message ?? "";
            _noticeType = type;
            _externalNotice?.Invoke(message, type);

            var status = UPilotUpdateService.Instance.GetOperationStatus();
            if (type == MessageType.Error ||
                (type == MessageType.Warning && !status.IsRunning) ||
                (!string.IsNullOrWhiteSpace(message) &&
                 !status.IsRunning &&
                 (message.IndexOf("已更新", StringComparison.Ordinal) >= 0 ||
                  message.IndexOf("已取消", StringComparison.Ordinal) >= 0)))
            {
                _operationRunning = false;
                if (type != MessageType.Error && message.IndexOf("已取消", StringComparison.Ordinal) < 0)
                    _ = RefreshAfterOperationAsync();
            }

            Repaint();
        }

        private async Task RefreshAfterOperationAsync()
        {
            await Task.Delay(500);
            CheckForUpdates();
        }

        private bool NeedsPackageUpdate(
            PackageInfo package,
            string currentVersion,
            bool mainChannel,
            bool localPackage)
        {
            if (localPackage)
                return false;
            if (!mainChannel)
                return UPilotServerRuntimeService.IsVersionNewer(_manifest.UpmVersion, currentVersion);

            var currentRevision = GetPackageRevision(package);
            if (string.IsNullOrWhiteSpace(currentRevision) || string.IsNullOrWhiteSpace(_manifest.CommitSha))
                return true;
            return !RevisionsMatch(currentRevision, _manifest.CommitSha);
        }

        private static string GetCurrentServerVersion(UPilotServerRuntimeMode mode)
        {
            var statusVersion = UPilotMcpServerManager.Instance.GetStatus().ServerVersion;
            if (!string.IsNullOrWhiteSpace(statusVersion))
                return statusVersion;

            if (mode == UPilotServerRuntimeMode.StandaloneExe)
                return UPilotProjectConfig.Current.runtime?.serverVersion ?? "";
            return UPilotServerRuntimeService.UpmVersion;
        }

        private string BuildPackageVersion(string version, PackageInfo package, bool mainChannel)
        {
            if (!mainChannel)
                return version;
            var revision = GetPackageRevision(package);
            return string.IsNullOrWhiteSpace(revision) ? version + "（Main 分支）" : version + " · " + ShortCommit(revision);
        }

        private string BuildLatestPackageVersion(bool mainChannel)
        {
            if (!mainChannel)
                return _manifest.UpmVersion;
            return _manifest.UpmVersion + " · " + ShortCommit(_manifest.CommitSha);
        }

        private static string BuildSummary(
            bool packageUpdate,
            bool managedServerAction,
            bool serverBlocked,
            bool serverVersionUnknown)
        {
            if (packageUpdate && managedServerAction)
                return "检查完成，UPilot 包和 MCP 服务都有新版本可更新";
            if (serverBlocked)
                return "有新版本可用，请先更新 UPilot 包";
            if (packageUpdate)
                return "有新版本需要更新";
            if (serverVersionUnknown)
                return "暂无法确认 MCP 服务版本，可安装最新服务";
            return "MCP 服务有新版本可用";
        }

        private void DrawPrimaryUpdateAction(
            bool updatePackage,
            bool updateManagedServer,
            bool blocked,
            bool mainChannel,
            bool busy)
        {
            if (!updatePackage && !updateManagedServer && !blocked)
                return;

            EditorGUILayout.Space(10);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                var label = BuildPrimaryUpdateLabel(updatePackage, updateManagedServer, mainChannel);
                var enabled = !busy && !blocked;
                var previous = GUI.backgroundColor;
                try
                {
                    GUI.backgroundColor = enabled
                        ? new Color(0.22f, 0.62f, 0.28f)
                        : new Color(0.45f, 0.45f, 0.45f);
                    using (new EditorGUI.DisabledScope(!enabled))
                    {
                        if (GUILayout.Button(label, GUILayout.Width(178), GUILayout.Height(48)))
                            UpdateSelected(updatePackage, updateManagedServer);
                    }
                }
                finally
                {
                    GUI.backgroundColor = previous;
                }
            }

            if (blocked)
                EditorGUILayout.HelpBox("当前 UPilot 包版本过低，请先更新 UPilot 包后再更新 MCP 服务。", MessageType.Warning);
        }

        private static string BuildPrimaryUpdateLabel(bool updatePackage, bool updateManagedServer, bool mainChannel)
        {
            if (updatePackage && updateManagedServer)
                return "更新 UPilot 和服务";
            if (updatePackage)
                return mainChannel ? "同步 Main 分支" : "更新 UPilot";
            if (updateManagedServer)
                return "更新服务";
            return "已是最新";
        }

        private static void DrawOperationStatus(UPilotUpdateOperationStatus status)
        {
            if (!status.IsRunning)
                return;

            var message = string.IsNullOrWhiteSpace(status.Message)
                ? "请等待更新完成…"
                : status.Message;
            EditorGUILayout.Space(6);
            EditorGUILayout.HelpBox(message, MessageType.Info);
        }

        private bool IsUpdateBusy()
        {
            return _operationRunning ||
                   UPilotUpdateService.Instance.GetOperationStatus().IsRunning ||
                   UPilotServerRuntimeService.Instance.DownloadState.IsRunning;
        }

        private void DrawCopyableAddressRow()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("更新地址", EditorStyles.miniLabel, GUILayout.Width(86));
                EditorGUILayout.LabelField("查看更新清单", EditorStyles.label);
                if (GUILayout.Button("复制地址", GUILayout.Width(88)))
                {
                    EditorGUIUtility.systemCopyBuffer = UPilotServerRuntimeService.ResolveManifestUrl();
                    _notice = "更新地址已复制";
                    _noticeType = MessageType.Info;
                }
            }
        }

        private static void DrawSectionTitle(string title)
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
        }

        private static void DrawInfoRow(string label, string value)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(label, EditorStyles.miniLabel, GUILayout.Width(86));
                EditorGUILayout.SelectableLabel(value ?? "", EditorStyles.label, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            }
        }

        private static void DrawStatusAction(
            string label,
            string value,
            string buttonLabel,
            Action action,
            bool enabled)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(label, EditorStyles.miniLabel, GUILayout.Width(86));
                EditorGUILayout.LabelField(value, EditorStyles.label);
                if (!string.IsNullOrWhiteSpace(buttonLabel))
                {
                    using (new EditorGUI.DisabledScope(!enabled))
                    {
                        var buttonWidth = buttonLabel.Length > 5 ? 112f : 88f;
                        if (GUILayout.Button(buttonLabel, GUILayout.Width(buttonWidth)))
                            action?.Invoke();
                    }
                }
            }
        }

        private static void DrawSeparator()
        {
            EditorGUILayout.Space(6);
            var rect = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(
                rect,
                EditorGUIUtility.isProSkin
                    ? new Color(1f, 1f, 1f, 0.1f)
                    : new Color(0f, 0f, 0f, 0.14f));
        }

        private static string GetPackageRevision(PackageInfo package)
        {
            if (package == null)
                return "";
            try
            {
                var git = package.GetType().GetProperty("git", BindingFlags.Instance | BindingFlags.Public)?.GetValue(package);
                return git?.GetType().GetProperty("hash", BindingFlags.Instance | BindingFlags.Public)?.GetValue(git) as string ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static bool RevisionsMatch(string left, string right)
        {
            return left.StartsWith(right, StringComparison.OrdinalIgnoreCase) ||
                   right.StartsWith(left, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsMainChannel(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   value.IndexOf("main", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ShortCommit(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "-";
            return value.Length <= 7 ? value : value.Substring(0, 7);
        }

        private static string EmptyAsDash(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "-" : value;
        }
    }
}
