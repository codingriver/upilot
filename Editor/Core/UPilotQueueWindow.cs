using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace CodingRiver.UPilot
{
    /// <summary>One-shot read-only inventory. No task status advancement or cleanup controls.</summary>
    public sealed class UPilotQueueWindow : EditorWindow
    {
        [Serializable] internal sealed class Row
        {
            public string type, id, name, status, lastProgress, blockingReason, unsupportedReason, operationId, source;
        }
        [Serializable] internal sealed class Snapshot
        {
            public string projectPath;
            public long observedAt;
            public bool connected, complete, isStale;
            public string[] issues;
            public Row[] items;
        }
        [Serializable] private sealed class Response { public bool ok; public Snapshot data; }
        private Snapshot _snapshot;
        private string _error = "";
        private Vector2 _scroll;
        private UnityWebRequest _request;
        private Snapshot _guiSnapshot;
        private string _guiError;
        private bool _guiInitialized, _guiLoading, _guiStale;
        private long _guiAge;

        public static void Open()
        {
            var window = GetWindow<UPilotQueueWindow>("当前任务队列");
            window.minSize = new Vector2(520, 300);
            window.Show();
            window.Refresh();
        }

        private void OnDisable()
        {
            _request?.Abort();
            _request?.Dispose();
            _request = null;
        }

        internal static bool IsVerifiedEmpty(Snapshot value) =>
            value != null && value.connected && value.complete && !value.isStale &&
            value.items != null && value.items.Length == 0;

        private void Refresh()
        {
            if (_request != null) return;
            _error = "";
            var request = UnityWebRequest.Get($"http://127.0.0.1:{UPilotProjectConfig.Current.mcp.httpPort}/queue");
            request.timeout = 10;
            _request = request;
            request.SendWebRequest().completed += _ =>
            {
                if (_request != request) return;
                try
                {
                    if (request.result != UnityWebRequest.Result.Success)
                        throw new IOException("队列读取失败（服务未连接或尚未更新）： " + request.error);
                    var response = JsonUtility.FromJson<Response>(request.downloadHandler.text);
                    if (response == null || !response.ok || response.data == null || response.data.items == null)
                        throw new IOException("队列数据不完整，不能判断为空。");
                    if (!ServiceMaintenanceJournal.SamePath(response.data.projectPath, UPilotProjectConfig.ProjectRoot))
                        throw new IOException("队列来源项目不匹配。");
                    _snapshot = response.data;
                }
                catch (Exception ex) { _error = ex.Message; }
                finally { request.Dispose(); _request = null; Repaint(); }
            };
        }

        private void OnGUI()
        {
            if (UPilotStatusWindow.ShouldRefreshGuiSnapshot(Event.current.type, _guiInitialized))
            {
                _guiSnapshot = _snapshot;
                _guiError = _error;
                _guiLoading = _request != null;
                _guiAge = _snapshot == null ? 0 : Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _snapshot.observedAt);
                _guiStale = _snapshot == null || _snapshot.isStale || _guiAge > 10000 || !string.IsNullOrEmpty(_error);
                _guiInitialized = true;
            }
            var snapshot = _guiSnapshot;
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("只读快照 · 不推进任务", EditorStyles.boldLabel);
                using (new EditorGUI.DisabledScope(_guiLoading))
                    if (GUILayout.Button("刷新", GUILayout.Width(70))) Refresh();
            }
            if (_guiLoading) EditorGUILayout.HelpBox("正在加载；不是空队列。", MessageType.Info);
            if (!string.IsNullOrEmpty(_guiError)) EditorGUILayout.HelpBox(_guiError + " 下方如有数据，仅为上次快照。", MessageType.Error);
            if (snapshot == null) { EditorGUILayout.HelpBox("尚无可确认的队列数据。", MessageType.Warning); return; }
            EditorGUILayout.LabelField($"快照时间：{snapshot.observedAt} · {_guiAge / 1000} 秒前");
            if (!snapshot.complete || !snapshot.connected || _guiStale)
                EditorGUILayout.HelpBox("断连、过期或数据不完整：不能据此判定没有任务。 " +
                    string.Join(", ", snapshot.issues ?? Array.Empty<string>()), MessageType.Warning);
            if (!_guiStale && IsVerifiedEmpty(snapshot)) EditorGUILayout.LabelField("当前没有已知任务或阻塞项。");
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var row in snapshot.items)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField($"{row.type} · {row.name}", EditorStyles.boldLabel);
                    EditorGUILayout.SelectableLabel(row.id ?? "", GUILayout.Height(18));
                    EditorGUILayout.LabelField("状态", row.status ?? "unknown");
                    EditorGUILayout.LabelField("最后进展", row.lastProgress ?? "未知");
                    if (!string.IsNullOrEmpty(row.operationId)) EditorGUILayout.LabelField("关联 Operation", row.operationId);
                    if (!string.IsNullOrEmpty(row.source)) EditorGUILayout.LabelField("来源", row.source);
                    if (!string.IsNullOrEmpty(row.blockingReason)) EditorGUILayout.HelpBox(row.blockingReason, MessageType.Warning);
                    if (!string.IsNullOrEmpty(row.unsupportedReason)) EditorGUILayout.LabelField(row.unsupportedReason, EditorStyles.wordWrappedMiniLabel);
                }
            }
            EditorGUILayout.EndScrollView();
        }
    }
}
