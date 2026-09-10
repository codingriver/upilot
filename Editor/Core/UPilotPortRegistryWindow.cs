using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot
{
    internal sealed class UPilotPortRegistryWindow : EditorWindow
    {
        private List<UPilotPortReservation> _entries = new List<UPilotPortReservation>();
        private readonly Dictionary<string, string> _states = new Dictionary<string, string>();
        private Vector2 _scroll;
        private string _error = "";
        private string _path = "";

        [MenuItem("UPilot/端口登记管理", false, 102)]
        internal static void Open()
        {
            var window = GetWindow<UPilotPortRegistryWindow>("UPilot 端口登记");
            window.minSize = new Vector2(480, 280);
            window.RefreshEntries();
            window.Show();
        }

        private void OnEnable() => RefreshEntries();

        private void RefreshEntries()
        {
            try
            {
                var registry = UPilotPortRegistry.ForUser();
                _path = registry.RegistryPath;
                _entries = registry.Snapshot();
                _states.Clear();
                foreach (var entry in _entries)
                {
                    var state = entry.pendingPorts.Length > 0 ? "待提交，旧端口仍保留" : "已预留";
                    try
                    {
                        var config = UPilotPortRegistry.ReadConfig(entry.projectPath).mcp;
                        if (config.wsPort != entry.wsPort || config.httpPort != entry.httpPort)
                            state += $"；工程配置已变更：WS {config.wsPort} / HTTP {config.httpPort}";
                    }
                    catch (Exception ex) { state += "；工程配置不可读：" + ex.Message; }
                    state += UPilotPortAllocator.IsPortAvailable(entry.wsPort) &&
                             UPilotPortAllocator.IsPortAvailable(entry.httpPort)
                        ? "；当前无监听" : "；端口正在监听（不代表已确认进程身份）";
                    _states[entry.projectPath] = state;
                }
                _error = "";
            }
            catch (Exception ex)
            {
                _error = ex.Message;
                UPilotPortRegistration.Report("读取端口登记列表", ex);
            }
        }

        private void OnGUI()
        {
            EditorGUILayout.SelectableLabel(_path, EditorStyles.wordWrappedMiniLabel, GUILayout.Height(35));
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("刷新")) RefreshEntries();
                if (GUILayout.Button("同步当前工程"))
                {
                    UPilotPortRegistration.TrySyncCurrent();
                    RefreshEntries();
                }
                if (GUILayout.Button("导入已有工程"))
                {
                    var folder = EditorUtility.OpenFolderPanel("选择 Unity 工程目录", "", "");
                    if (!string.IsNullOrEmpty(folder))
                    {
                        try
                        {
                            if (!Directory.Exists(Path.Combine(folder, "Assets")) ||
                                !Directory.Exists(Path.Combine(folder, "ProjectSettings")))
                                throw new IOException("所选目录不是 Unity 工程根目录。");
                            var config = UPilotPortRegistry.ReadConfig(folder).mcp;
                            if (EditorUtility.DisplayDialog("登记工程端口？",
                                    $"{folder}\nWS {config.wsPort} / HTTP {config.httpPort}\n不会修改该工程的配置。",
                                    "登记", "取消"))
                                UPilotPortRegistry.ForUser().Sync(folder);
                            RefreshEntries();
                        }
                        catch (Exception ex) { _error = ex.Message; UPilotPortRegistration.Report("导入工程端口", ex); }
                    }
                }
            }
            if (!string.IsNullOrEmpty(_error)) EditorGUILayout.HelpBox(_error, MessageType.Error);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var entry in _entries.ToArray())
            {
                EditorGUILayout.Space(8);
                EditorGUILayout.SelectableLabel(entry.projectPath, EditorStyles.wordWrappedLabel, GUILayout.Height(36));
                EditorGUILayout.LabelField($"WS {entry.wsPort} / HTTP {entry.httpPort}   {entry.syncedAtUtc}");
                EditorGUILayout.LabelField(_states.TryGetValue(entry.projectPath, out var state) ? state : "",
                    EditorStyles.wordWrappedMiniLabel);
                using (new EditorGUI.DisabledScope(
                           UPilotPortRegistry.NormalizeProject(entry.projectPath) ==
                           UPilotPortRegistry.NormalizeProject(UPilotProjectConfig.ProjectRoot)))
                {
                    if (GUILayout.Button("释放预留", GUILayout.Width(100)) &&
                        EditorUtility.DisplayDialog("确认释放端口预留？",
                            $"{entry.projectPath}\nWS {entry.wsPort} / HTTP {entry.httpPort}\n" +
                            "该工程配置不会被删除；其他工程将可以分配这些端口，该工程再次打开时可能冲突。",
                            "释放", "取消"))
                    {
                        try
                        {
                            UPilotPortRegistry.ForUser().Release(entry, UPilotProjectConfig.ProjectRoot);
                            Debug.LogWarning($"[UPilotPorts] 已手动释放预留：{entry.projectPath}; WS={entry.wsPort}; HTTP={entry.httpPort}");
                            RefreshEntries();
                        }
                        catch (Exception ex) { _error = ex.Message; UPilotPortRegistration.Report("释放端口预留", ex); }
                    }
                }
            }
            EditorGUILayout.EndScrollView();
        }
    }
}
