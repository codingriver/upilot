// -----------------------------------------------------------------------
// UPilot Editor - Quick Debug Window
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CodingRiver.UPilot.Execution;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot
{
    public sealed class UPilotQuickDebugWindow : EditorWindow
    {
        private const int MaxLogEntries = 200;

        private int _activeTab;
        private string _evalCode = "";
        private string _evalSessionId = "";
        private string _evalVariablesJson = "";
        private string _evalLimitsJson = "";
        private string _evalResultMode = "inline";
        private string _evalBackend = "auto";
        private bool _evalShowOptions;

        private string _reflectTypeName = "";
        private string _reflectMethodName = "";
        private string _reflectArgumentsJson = "";
        private string _reflectTargetHandle = "";
        private string _reflectParameterTypeNames = "";
        private string _reflectGenericTypeArguments = "";
        private string _reflectSessionId = "";
        private string _reflectResultMode = "inline";
        private string _reflectAwaitMode = "auto";
        private int _reflectAwaitTimeoutMs = 3000;
        private bool _reflectShowOptions;

        private string _dumpSessionId = "";
        private string _dumpHandle = "";
        private int _dumpMaxDepth = 3;
        private int _dumpMaxFieldsPerNode = 100;
        private int _dumpMaxTotalNodes = 5000;
        private bool _dumpIncludeStatic;
        private string _dumpOutputFormat = "json";
        private string _dumpIndentation = "  ";
        private string _dumpIgnoreTypes = "";
        private bool _dumpShowOptions;

        private readonly List<string> _logEntries = new();
        private Vector2 _logScroll;
        private bool _autoScroll = true;

        private CancellationTokenSource _evalCts;
        private CancellationTokenSource _reflectCts;
        private CancellationTokenSource _dumpCts;
        private bool _evalRunning;
        private bool _reflectRunning;
        private bool _dumpRunning;

        private double _lastRepaint;

        private GUIStyle _styleBox;
        private GUIStyle _styleLog;
        private GUIStyle _styleLogError;
        private bool _stylesInit;

        [MenuItem("UPilot/快捷调试", false, 102)]
        public static void Open()
        {
            try
            {
                var win = GetWindow<UPilotQuickDebugWindow>("UPilot 快捷调试");
                win.minSize = new Vector2(580, 480);
                win.Show();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UPilot] 打开快捷调试窗口失败: {ex.Message}");
            }
        }

        private void OnEnable()
        {
            EditorApplication.update -= OnEditorUpdate;
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            CancelAll();
        }

        private void OnEditorUpdate()
        {
            if (EditorApplication.timeSinceStartup - _lastRepaint > 0.3)
            {
                _lastRepaint = EditorApplication.timeSinceStartup;
                Repaint();
            }
        }

        private void InitStyles()
        {
            if (_stylesInit) return;
            _stylesInit = true;
            _styleBox = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(10, 10, 8, 8),
                margin = new RectOffset(4, 4, 3, 3),
            };
            _styleLog = new GUIStyle(EditorStyles.label)
            {
                fontSize = 11,
                wordWrap = true,
                normal = { textColor = EditorGUIUtility.isProSkin ? new Color(0.75f, 0.75f, 0.75f) : Color.black },
            };
            _styleLogError = new GUIStyle(_styleLog)
            {
                normal = { textColor = new Color(1f, 0.35f, 0.35f) },
            };
        }

        private void OnGUI()
        {
            InitStyles();
            try
            {
                DrawTabBar();
                EditorGUILayout.Space(4);
                switch (_activeTab)
                {
                    case 0: DrawCSharpEvalTab(); break;
                    case 1: DrawReflectionCallTab(); break;
                    case 2: DrawObjectDumpTab(); break;
                }
                EditorGUILayout.Space(6);
                DrawLogArea();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UPilot QuickDebug] OnGUI error: {ex}");
            }
        }

        private void DrawTabBar()
        {
            var tabs = new[] { "csharp_eval", "unity_reflection_call", "csharp_object_dump" };
            _activeTab = GUILayout.Toolbar(_activeTab, tabs);
        }

        // ── csharp_eval tab ──────────────────────────────────────────────────

        private void DrawCSharpEvalTab()
        {
            using (new EditorGUILayout.VerticalScope(_styleBox))
            {
                EditorGUILayout.LabelField("C# 代码", EditorStyles.boldLabel);
                _evalCode = EditorGUILayout.TextArea(_evalCode, GUILayout.Height(140));

                EditorGUILayout.Space(2);
                _evalShowOptions = EditorGUILayout.BeginFoldoutHeaderGroup(_evalShowOptions, "可选参数");
                if (_evalShowOptions)
                {
                    EditorGUI.indentLevel++;
                    _evalSessionId = EditorGUILayout.TextField("sessionId", _evalSessionId);
                    _evalResultMode = EditorGUILayout.TextField("resultMode", _evalResultMode);
                    _evalBackend = EditorGUILayout.TextField("executionBackend", _evalBackend);
                    _evalVariablesJson = EditorGUILayout.TextField("variablesJson", _evalVariablesJson);
                    _evalLimitsJson = EditorGUILayout.TextField("limitsJson", _evalLimitsJson);
                    EditorGUI.indentLevel--;
                }
                EditorGUILayout.EndFoldoutHeaderGroup();

                EditorGUILayout.Space(4);
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUI.enabled = !_evalRunning && !string.IsNullOrWhiteSpace(_evalCode);
                    if (GUILayout.Button("执行", GUILayout.Height(28), GUILayout.Width(80)))
                        RunCSharpEval();
                    GUI.enabled = _evalRunning;
                    if (GUILayout.Button("取消", GUILayout.Height(28), GUILayout.Width(80)))
                        CancelEval();
                    GUI.enabled = true;
                    GUILayout.FlexibleSpace();
                    if (_evalRunning)
                        EditorGUILayout.LabelField("执行中...", GUILayout.Width(60));
                }
            }
        }

        private async void RunCSharpEval()
        {
            var bridge = UPilotBridge.Instance;
            var service = bridge.ExecutionService;
            if (service == null)
            {
                AppendLog("UPilot Bridge 未就绪，无法执行 csharp_eval。", true);
                return;
            }

            _evalCts = new CancellationTokenSource();
            _evalRunning = true;
            Repaint();

            var id = Guid.NewGuid().ToString("N");
            var payload = new CSharpEvalPayload
            {
                code = _evalCode,
                sessionId = _evalSessionId ?? "",
                variablesJson = _evalVariablesJson ?? "",
                limitsJson = _evalLimitsJson ?? "",
                resultMode = string.IsNullOrWhiteSpace(_evalResultMode) ? "inline" : _evalResultMode,
                executionBackend = string.IsNullOrWhiteSpace(_evalBackend) ? "auto" : _evalBackend,
            };
            var json = JsonUtility.ToJson(new CSharpEvalMessage { payload = payload });

            try
            {
                await service.HandleCSharpEvalAsync(id, json, _evalCts.Token,
                    enqueue: action => action(),
                    sendResult: result =>
                    {
                        var sb = new StringBuilder();
                        sb.AppendLine("[csharp_eval 完成]");
                        sb.AppendLine($"  modeUsed: {result.modeUsed}");
                        sb.AppendLine($"  backendUsed: {result.backendUsed}");
                        sb.AppendLine($"  resultType: {result.resultType}");
                        sb.AppendLine($"  result: {result.result}");
                        if (result.resultValue != null)
                            sb.AppendLine($"  resultValue.kind: {result.resultValue.kind}  summary: {result.resultValue.summary}");
                        if (result.resultHandle != null)
                            sb.AppendLine($"  resultHandle: {result.resultHandle}");
                        if (result.sessionId != null)
                            sb.AppendLine($"  sessionId: {result.sessionId}");
                        if (result.budget != null)
                            sb.AppendLine($"  budget: statements={result.budget.statements} loopIterations={result.budget.loopIterations} calls={result.budget.calls} allocations={result.budget.allocations} elapsedMs={result.budget.elapsedMs}");
                        if (result.diagnostics != null && result.diagnostics.Length > 0)
                            sb.AppendLine($"  diagnostics: {string.Join(", ", result.diagnostics)}");
                        AppendLog(sb.ToString().TrimEnd());
                        return Task.CompletedTask;
                    },
                    sendError: err =>
                    {
                        AppendLog($"[csharp_eval 错误] [{err.Code}] {err.Message}", true);
                        return Task.CompletedTask;
                    });
            }
            catch (Exception ex)
            {
                AppendLog($"[csharp_eval 异常] {ex.GetType().Name}: {ex.Message}", true);
            }
            finally
            {
                _evalRunning = false;
                _evalCts?.Dispose();
                _evalCts = null;
                Repaint();
            }
        }

        private void CancelEval()
        {
            try { _evalCts?.Cancel(); } catch { }
        }

        // ── unity_reflection_call tab ────────────────────────────────────────

        private void DrawReflectionCallTab()
        {
            using (new EditorGUILayout.VerticalScope(_styleBox))
            {
                EditorGUILayout.LabelField("方法调用（如有表达式需求请使用 csharp_eval 页签）", EditorStyles.miniLabel);
                EditorGUILayout.Space(2);
                _reflectTypeName = EditorGUILayout.TextField("typeName", _reflectTypeName);
                _reflectMethodName = EditorGUILayout.TextField("methodName", _reflectMethodName);
                _reflectArgumentsJson = EditorGUILayout.TextField("argumentsJson", _reflectArgumentsJson);

                EditorGUILayout.Space(2);
                _reflectShowOptions = EditorGUILayout.BeginFoldoutHeaderGroup(_reflectShowOptions, "可选参数");
                if (_reflectShowOptions)
                {
                    EditorGUI.indentLevel++;
                    _reflectTargetHandle = EditorGUILayout.TextField("targetHandle", _reflectTargetHandle);
                    _reflectParameterTypeNames = EditorGUILayout.TextField("parameterTypeNames (逗号分隔)", _reflectParameterTypeNames);
                    _reflectGenericTypeArguments = EditorGUILayout.TextField("genericTypeArguments (逗号分隔)", _reflectGenericTypeArguments);
                    _reflectSessionId = EditorGUILayout.TextField("sessionId", _reflectSessionId);
                    _reflectResultMode = EditorGUILayout.TextField("resultMode", _reflectResultMode);
                    _reflectAwaitMode = EditorGUILayout.TextField("awaitMode", _reflectAwaitMode);
                    _reflectAwaitTimeoutMs = EditorGUILayout.IntField("awaitTimeoutMs", _reflectAwaitTimeoutMs);
                    EditorGUI.indentLevel--;
                }
                EditorGUILayout.EndFoldoutHeaderGroup();

                EditorGUILayout.Space(4);
                using (new EditorGUILayout.HorizontalScope())
                {
                    var hasInput = !string.IsNullOrWhiteSpace(_reflectTypeName) && !string.IsNullOrWhiteSpace(_reflectMethodName);
                    GUI.enabled = !_reflectRunning && hasInput;
                    if (GUILayout.Button("执行", GUILayout.Height(28), GUILayout.Width(80)))
                        RunReflectionCall();
                    GUI.enabled = _reflectRunning;
                    if (GUILayout.Button("取消", GUILayout.Height(28), GUILayout.Width(80)))
                        CancelReflect();
                    GUI.enabled = true;
                    GUILayout.FlexibleSpace();
                    if (_reflectRunning)
                        EditorGUILayout.LabelField("执行中...", GUILayout.Width(60));
                }
            }
        }

        private async void RunReflectionCall()
        {
            var bridge = UPilotBridge.Instance;
            var service = bridge.ReflectionService;
            if (service == null)
            {
                AppendLog("UPilot Bridge 未就绪，无法执行 unity_reflection_call。", true);
                return;
            }

            _reflectCts = new CancellationTokenSource();
            _reflectRunning = true;
            Repaint();

            var id = Guid.NewGuid().ToString("N");
            var payload = new ReflectionCallPayload
            {
                typeName = _reflectTypeName,
                methodName = _reflectMethodName,
                argumentsJson = _reflectArgumentsJson,
                targetHandle = string.IsNullOrWhiteSpace(_reflectTargetHandle) ? "" : _reflectTargetHandle,
                parameterTypeNames = string.IsNullOrWhiteSpace(_reflectParameterTypeNames)
                    ? Array.Empty<string>()
                    : _reflectParameterTypeNames.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries),
                genericTypeArguments = string.IsNullOrWhiteSpace(_reflectGenericTypeArguments)
                    ? Array.Empty<string>()
                    : _reflectGenericTypeArguments.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries),
                sessionId = _reflectSessionId ?? "",
                resultMode = string.IsNullOrWhiteSpace(_reflectResultMode) ? "inline" : _reflectResultMode,
                awaitMode = string.IsNullOrWhiteSpace(_reflectAwaitMode) ? "auto" : _reflectAwaitMode,
                awaitTimeoutMs = _reflectAwaitTimeoutMs,
            };
            var json = JsonUtility.ToJson(new ReflectionCallMessage { payload = payload });

            try
            {
                await service.HandleCallAsync(id, json, _reflectCts.Token,
                    enqueue: action => action(),
                    sendResult: result =>
                    {
                        var sb = new StringBuilder();
                        sb.AppendLine("[unity_reflection_call 完成]");
                        if (result.typeName != null) sb.AppendLine($"  typeName: {result.typeName}");
                        if (result.methodName != null) sb.AppendLine($"  methodName: {result.methodName}");
                        if (result.invokedSignature != null) sb.AppendLine($"  invokedSignature: {result.invokedSignature}");
                        sb.AppendLine($"  result: {result.result}");
                        if (result.resultValue != null)
                            sb.AppendLine($"  resultValue.kind: {result.resultValue.kind}  summary: {result.resultValue.summary}");
                        if (result.wasAwaitable)
                            sb.AppendLine($"  wasAwaitable: true  awaitableStatus: {result.awaitableStatus}");
                        if (result.refOutArguments != null && result.refOutArguments.Count > 0)
                        {
                            sb.AppendLine("  refOutArguments:");
                            foreach (var ra in result.refOutArguments)
                                sb.AppendLine($"    {ra.name} ({ra.direction}): {ra.value?.summary ?? "(null)"}");
                        }
                        AppendLog(sb.ToString().TrimEnd());
                        return Task.CompletedTask;
                    },
                    sendError: err =>
                    {
                        AppendLog($"[unity_reflection_call 错误] [{err.Code}] {err.Message}", true);
                        return Task.CompletedTask;
                    });
            }
            catch (Exception ex)
            {
                AppendLog($"[unity_reflection_call 异常] {ex.GetType().Name}: {ex.Message}", true);
            }
            finally
            {
                _reflectRunning = false;
                _reflectCts?.Dispose();
                _reflectCts = null;
                Repaint();
            }
        }

        private void CancelReflect()
        {
            try { _reflectCts?.Cancel(); } catch { }
        }

        // ── csharp_object_dump tab ───────────────────────────────────────────

        private void DrawObjectDumpTab()
        {
            using (new EditorGUILayout.VerticalScope(_styleBox))
            {
                EditorGUILayout.LabelField("对象转储（需先通过 csharp_eval 获取 handle）", EditorStyles.miniLabel);
                EditorGUILayout.Space(2);
                _dumpSessionId = EditorGUILayout.TextField("sessionId", _dumpSessionId);
                _dumpHandle = EditorGUILayout.TextField("handle", _dumpHandle);

                EditorGUILayout.Space(2);
                _dumpShowOptions = EditorGUILayout.BeginFoldoutHeaderGroup(_dumpShowOptions, "可选参数");
                if (_dumpShowOptions)
                {
                    EditorGUI.indentLevel++;
                    _dumpMaxDepth = EditorGUILayout.IntSlider("maxDepth", _dumpMaxDepth, 0, 64);
                    _dumpMaxFieldsPerNode = EditorGUILayout.IntSlider("maxFieldsPerNode", _dumpMaxFieldsPerNode, 1, 500);
                    _dumpMaxTotalNodes = EditorGUILayout.IntSlider("maxTotalNodes", _dumpMaxTotalNodes, 1, 20000);
                    _dumpIncludeStatic = EditorGUILayout.Toggle("includeStatic", _dumpIncludeStatic);
                    _dumpOutputFormat = EditorGUILayout.TextField("outputFormat", _dumpOutputFormat);
                    _dumpIndentation = EditorGUILayout.TextField("indentation", _dumpIndentation);
                    _dumpIgnoreTypes = EditorGUILayout.TextField("ignoreTypes (逗号分隔)", _dumpIgnoreTypes);
                    EditorGUI.indentLevel--;
                }
                EditorGUILayout.EndFoldoutHeaderGroup();

                EditorGUILayout.Space(4);
                using (new EditorGUILayout.HorizontalScope())
                {
                    var hasInput = !string.IsNullOrWhiteSpace(_dumpSessionId) && !string.IsNullOrWhiteSpace(_dumpHandle);
                    GUI.enabled = !_dumpRunning && hasInput;
                    if (GUILayout.Button("执行", GUILayout.Height(28), GUILayout.Width(80)))
                        RunObjectDump();
                    GUI.enabled = _dumpRunning;
                    if (GUILayout.Button("取消", GUILayout.Height(28), GUILayout.Width(80)))
                        CancelDump();
                    GUI.enabled = true;
                    GUILayout.FlexibleSpace();
                    if (_dumpRunning)
                        EditorGUILayout.LabelField("执行中...", GUILayout.Width(60));
                }
            }
        }

        private async void RunObjectDump()
        {
            var bridge = UPilotBridge.Instance;
            var service = bridge.ExecutionService;
            if (service == null)
            {
                AppendLog("UPilot Bridge 未就绪，无法执行 csharp_object_dump。", true);
                return;
            }

            _dumpCts = new CancellationTokenSource();
            _dumpRunning = true;
            Repaint();

            var id = Guid.NewGuid().ToString("N");
            var ignoreTypes = string.IsNullOrWhiteSpace(_dumpIgnoreTypes)
                ? Array.Empty<string>()
                : _dumpIgnoreTypes.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            var payload = new ObjectDumpPayload
            {
                sessionId = _dumpSessionId,
                handle = _dumpHandle,
                maxDepth = _dumpMaxDepth,
                maxFieldsPerNode = _dumpMaxFieldsPerNode,
                maxTotalNodes = _dumpMaxTotalNodes,
                includeStatic = _dumpIncludeStatic,
                outputFormat = string.IsNullOrWhiteSpace(_dumpOutputFormat) ? "json" : _dumpOutputFormat,
                indentation = string.IsNullOrWhiteSpace(_dumpIndentation) ? "  " : _dumpIndentation,
                ignoreTypes = ignoreTypes,
            };
            var json = JsonUtility.ToJson(new ObjectDumpMessage { payload = payload });

            try
            {
                await service.HandleObjectDumpAsync(id, json, _dumpCts.Token,
                    enqueue: action => action(),
                    sendResult: result =>
                    {
                        var sb = new StringBuilder();
                        sb.AppendLine("[csharp_object_dump 完成]");
                        sb.AppendLine($"  typeName: {result.typeName}");
                        sb.AppendLine($"  totalNodes: {result.totalNodes}");
                        if (result.truncated)
                            sb.AppendLine($"  truncated: true  reason: {result.truncateReason}");
                        sb.AppendLine("---");
                        sb.AppendLine(result.text ?? "");
                        AppendLog(sb.ToString().TrimEnd());
                        return Task.CompletedTask;
                    },
                    sendError: err =>
                    {
                        AppendLog($"[csharp_object_dump 错误] [{err.Code}] {err.Message}", true);
                        return Task.CompletedTask;
                    });
            }
            catch (Exception ex)
            {
                AppendLog($"[csharp_object_dump 异常] {ex.GetType().Name}: {ex.Message}", true);
            }
            finally
            {
                _dumpRunning = false;
                _dumpCts?.Dispose();
                _dumpCts = null;
                Repaint();
            }
        }

        private void CancelDump()
        {
            try { _dumpCts?.Cancel(); } catch { }
        }

        // ── 共享日志区 ────────────────────────────────────────────────────────

        private void DrawLogArea()
        {
            using (new EditorGUILayout.VerticalScope(_styleBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("执行日志", EditorStyles.boldLabel);
                    GUILayout.FlexibleSpace();
                    _autoScroll = EditorGUILayout.ToggleLeft("自动滚动", _autoScroll, GUILayout.Width(80));
                    if (GUILayout.Button("清空", GUILayout.Width(50)))
                    {
                        _logEntries.Clear();
                        _logScroll = Vector2.zero;
                    }
                }

                EditorGUILayout.Space(2);

                var logHeight = Mathf.Clamp(position.height * 0.28f, 100f, 400f);
                var vBarW = GUI.skin.verticalScrollbar.fixedWidth > 0f
                    ? GUI.skin.verticalScrollbar.fixedWidth : 16f;

                _logScroll = EditorGUILayout.BeginScrollView(_logScroll, GUI.skin.box,
                    GUILayout.Height(logHeight), GUILayout.ExpandWidth(true));

                foreach (var entry in _logEntries)
                {
                    var style = entry.StartsWith("[", StringComparison.Ordinal) &&
                        (entry.Contains("错误") || entry.Contains("异常") || entry.Contains("error", StringComparison.OrdinalIgnoreCase))
                        ? _styleLogError : _styleLog;
                    EditorGUILayout.LabelField(entry, style, GUILayout.ExpandWidth(true));
                }

                EditorGUILayout.EndScrollView();

                if (_autoScroll && _logEntries.Count > 0 && Event.current.type == EventType.Repaint)
                    _logScroll.y = float.MaxValue;
            }
        }

        private void AppendLog(string message, bool isError = false)
        {
            _logEntries.Add(message);
            if (_logEntries.Count > MaxLogEntries)
                _logEntries.RemoveRange(0, _logEntries.Count - MaxLogEntries);
            if (isError)
                Debug.LogError($"[UPilot QuickDebug] {message}");
            else
                Debug.Log($"[UPilot QuickDebug] {message}");
            Repaint();
        }

        private void CancelAll()
        {
            CancelEval();
            CancelReflect();
            CancelDump();
        }
    }
}