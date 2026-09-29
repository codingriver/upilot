using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace CodingRiver.UPilot
{
    /// <summary>Explicit finite preview/apply. Closing the window never cancels or replays a request.</summary>
    public sealed class UPilotQueueCleanupWindow : EditorWindow
    {
        [Serializable] private sealed class Request
        {
            public string targetType = "All", targetId = "*", action = "force_clear_all";
            public string reason, confirmToken, expectedProjectPath;
            public bool dryRun = true;
        }
        [Serializable] private sealed class Target { public string type, id, action, status, reason, resultStatus; }
        [Serializable] private sealed class Data
        {
            public string projectPath, confirmToken, requestId, status, error;
            public bool dryRun, allowed, complete, allCleared, dispatchComplete;
            public Target[] targets;
            public string[] issues;
        }
        [Serializable] private sealed class Response { public bool ok; public Data data; public string error; }
        [SerializeField] private string _requestId = "";
        private string _reason = "手动清理当前项目阻塞任务", _error = "", _previewReason;
        private Data _data;
        private double _expiresAt;
        private Vector2 _scroll;
        private UnityWebRequest _request;
        public static void Open() { var w = GetWindow<UPilotQueueCleanupWindow>("批量清理可安全处理项"); w.minSize = new Vector2(600, 380); w.Show(); }
        private string SessionKey => "UPilot.QueueCleanup." + UPilotProjectConfig.ProjectRoot;
        private void OnEnable() { if (string.IsNullOrEmpty(_requestId)) _requestId = SessionState.GetString(SessionKey, ""); }
        private void OnDisable() { _request?.Abort(); _request?.Dispose(); _request = null; }
        private void Send(Request body)
        {
            if (_request != null) return;
            body.expectedProjectPath = UPilotProjectConfig.ProjectRoot;
            _error = "";
            var req = new UnityWebRequest($"http://127.0.0.1:{UPilotProjectConfig.Current.mcp.httpPort}/queue/cleanup", "POST");
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(JsonUtility.ToJson(body)));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json"); req.SetRequestHeader("X-UPilot-Queue", "1");
            req.timeout = 120; _request = req;
            req.SendWebRequest().completed += _ =>
            {
                if (_request != req) return;
                try
                {
                    if (req.result != UnityWebRequest.Result.Success) throw new IOException("请求未确认，不自动重发：" + req.error);
                    var response = JsonUtility.FromJson<Response>(req.downloadHandler.text);
                    if (response == null || !response.ok || response.data == null) throw new IOException(response?.error ?? "响应未知");
                    if (!ServiceMaintenanceJournal.SamePath(response.data.projectPath, UPilotProjectConfig.ProjectRoot))
                        throw new IOException("响应项目不匹配");
                    _data = response.data;
                    if (_data.dryRun) { _expiresAt = EditorApplication.timeSinceStartup + 110; _previewReason = body.reason; }
                    else if (!string.IsNullOrEmpty(_data.requestId))
                    { _requestId = _data.requestId; SessionState.SetString(SessionKey, _requestId); }
                }
                catch (Exception ex) { _error = ex.Message; Debug.LogError("[UPilot][QueueCleanup] " + _error); }
                finally { req.Dispose(); _request = null; Repaint(); }
            };
        }
        private void OnGUI()
        {
            EditorGUILayout.HelpBox("对预览中的任务逐项请求取消、恢复或有证据的行政处置；不删除记录，不伪造测试成功。无法安全处理的任务会保留并列出原因。关闭窗口不取消后台清理。", MessageType.Warning);
            _reason = EditorGUILayout.TextField("清理原因", _reason);
            using (new EditorGUI.DisabledScope(_request != null))
            {
                if (GUILayout.Button("1. 预览当前全部任务（无副作用）"))
                    Send(new Request { reason = _reason });
                using (new EditorGUI.DisabledScope(_data == null || !_data.dryRun || !_data.allowed
                    || _reason != _previewReason || EditorApplication.timeSinceStartup >= _expiresAt))
                    if (GUILayout.Button("2. 确认执行预览中的可处理项"))
                    {
                        if (EditorUtility.DisplayDialog("批量清理可安全处理项", "将影响当前项目其他聊天提交的任务。未确认安全的项目不会强行释放，行政处置不代表业务完成。继续？", "执行", "取消"))
                        {
                            // Remember the preview's original identity before dispatch: a lost response is not retry permission.
                            _requestId = _data.requestId;
                            SessionState.SetString(SessionKey, _requestId);
                            var token = _data.confirmToken; _data.confirmToken = ""; _expiresAt = 0;
                            Send(new Request { reason = _previewReason, dryRun = false, confirmToken = token });
                        }
                    }
                _requestId = EditorGUILayout.TextField("原批量请求 ID", _requestId);
                using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_requestId)))
                    if (GUILayout.Button("查询原请求（不重发）"))
                        Send(new Request { targetId = _requestId, action = "force_clear_status" });
            }
            if (_request != null) EditorGUILayout.LabelField("正在请求；不是任务已停止。");
            if (!string.IsNullOrEmpty(_error)) EditorGUILayout.HelpBox(_error, MessageType.Error);
            if (_data == null) return;
            if (_data.dryRun && !_data.allowed) EditorGUILayout.HelpBox("独立队列清理授权未启用；仅可查看。", MessageType.Warning);
            EditorGUILayout.LabelField(_data.dryRun ? "预览（一次性，有效期约 2 分钟）" : "状态：" + _data.status);
            if (!_data.dryRun) EditorGUILayout.HelpBox(_data.allCleared ? "已确认队列完整且无阻塞项。" : "尚未确认全部清理：可能仍在停止、存在不支持项或数据不完整。", _data.allCleared ? MessageType.Info : MessageType.Warning);
            if (_data.issues != null) EditorGUILayout.LabelField(string.Join(", ", _data.issues), EditorStyles.wordWrappedMiniLabel);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var row in _data.targets ?? Array.Empty<Target>())
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField($"{row.type} · {row.status} · {row.action}");
                    EditorGUILayout.SelectableLabel(row.id ?? "", GUILayout.Height(18));
                    EditorGUILayout.LabelField((row.resultStatus ?? "") + " " + (row.reason ?? ""), EditorStyles.wordWrappedLabel);
                }
            }
            EditorGUILayout.EndScrollView();
        }
    }
}
