// -----------------------------------------------------------------------
// UPilot Editor - Quick Debug Window
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
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
        private const string ObjectDumpLogRelativePath = "Logs/UPilot/csharp_object_dump.log";

        private static readonly string[] EvalModeValues = { "auto", "expression", "statements" };
        private static readonly GUIContent[] EvalModeOptions =
        {
            new GUIContent("自动判断（auto）", "先尝试把完整代码解析为单个表达式，否则按语句程序解析。"),
            new GUIContent("单个表达式（expression）", "仅接受一个表达式；末尾分号可选。"),
            new GUIContent("语句程序（statements）", "按 C# 子集语句块解析，适合变量、控制流和 return。"),
        };
        private static readonly string[] EvalBackendValues = { "auto", "interpret", "emit", "compiled" };
        private static readonly GUIContent[] EvalBackendOptions =
        {
            new GUIContent("自动选择（auto）", "首次解释执行并预热 Emit 入口缓存；后续相同代码可命中缓存。"),
            new GUIContent("解释执行（interpret）", "直接执行完整 V2 AST。"),
            new GUIContent("Emit 入口缓存（emit）", "通过 DynamicMethod 缓存入口执行 AST，不是源码到 IL 编译。"),
            new GUIContent("直接编译（compiled）", "把有限的同步、可静态绑定子集编译为 Expression Tree delegate；不支持时不会回退。"),
        };
        private static readonly string[] EvalResultModeValues = { "auto", "inline", "handle", "legacyString" };
        private static readonly GUIContent[] EvalResultModeOptions =
        {
            new GUIContent("自动选择（auto）", "优先内联；不能内联且存在 session 时返回 handle。"),
            new GUIContent("内联结果（inline）", "要求结果可编码为受支持且大小受限的 TypedValue。"),
            new GUIContent("对象句柄（handle）", "把结果保存到持久 session 并返回 handle；必须填写 sessionId。"),
            new GUIContent("旧版字符串（legacyString）", "仅用于兼容旧版字符串结果行为。"),
        };

        private int _activeTab;
        private string _evalCode = "";
        private string _evalMode = "auto";
        private string _evalSessionId = "";
        private string _evalVariablesJson = "";
        private string _evalLimitsJson = "";
        private string _evalResultMode = "inline";
        private string _evalBackend = "auto";
        private bool _evalShowOptions;
        private bool _evalShowHelp;

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
        private bool _reflectShowHelp;

        private string _dumpSessionId = "";
        private string _dumpHandle = "";
        private int _dumpMaxDepth = 3;
        private int _dumpMaxFieldsPerNode = 100;
        private int _dumpMaxTotalNodes = 5000;
        private bool _dumpIncludeStatic;
        private bool _dumpIncludeTypeNames;
        private bool _dumpExpandReflectionTypes;
        private string _dumpOutputFormat = "json";
        private string _dumpIndentation = "  ";
        private string _dumpIgnoreTypes = "";
        private bool _dumpShowOptions;
        private bool _dumpShowHelp;

        // ── one-click helpers ─────────────────────────────────────────────────
        private string _dumpQuickEvalCode = "";
        private bool _dumpQuickRunning;
        private CancellationTokenSource _dumpQuickCts;
        private bool _diagnosticRunning;
        private CancellationTokenSource _diagnosticCts;
        private string _diagnosticProgress = "";
        private bool _windowDisabled;
        private readonly HashSet<string> _unavailableDiagnosticLogs = new HashSet<string>();

        private readonly List<string> _logEntries = new();
        private readonly List<bool> _logEntryErrors = new();
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
        private GUIStyle _styleHelpBox;
        private GUIStyle _styleExampleBtn;
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
            _windowDisabled = false;
            EditorApplication.update -= OnEditorUpdate;
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            _windowDisabled = true;
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
            _styleHelpBox = new GUIStyle(EditorStyles.helpBox)
            {
                fontSize = 11,
                wordWrap = true,
                richText = true,
                padding = new RectOffset(8, 8, 6, 6),
            };
            _styleExampleBtn = new GUIStyle(GUI.skin.button)
            {
                fontSize = 10,
                fixedHeight = 22,
                margin = new RectOffset(2, 2, 1, 1),
            };
        }

        private void OnGUI()
        {
            InitStyles();
            try
            {
                DrawMenuBar();
                EditorGUILayout.Space(2);
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

        private void DrawMenuBar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Button(CreateDiagnosticsToolbarContent(), EditorStyles.toolbarDropDown, GUILayout.Width(72)))
                    ShowDiagnosticsMenu();

                GUILayout.FlexibleSpace();
                if (_diagnosticRunning)
                    GUILayout.Label(new GUIContent(_diagnosticProgress, _diagnosticProgress),
                        EditorStyles.miniLabel, GUILayout.MaxWidth(Mathf.Max(100, position.width - 100)));
            }
        }

        private static GUIContent CreateDiagnosticsToolbarContent()
        {
            return new GUIContent("诊断", "运行快捷调试工具的独立诊断检查。");
        }

        private void ShowDiagnosticsMenu()
        {
            var menu = new GenericMenu();
            var bridge = UPilotBridge.Instance;
            foreach (var toolId in UPilotQuickDebugToolCheck.ToolIds)
            {
                var capturedId = toolId;
                var serviceReady = bridge.ExecutionService != null &&
                    (toolId != "unity_reflection_call" || bridge.ReflectionService != null);
                var content = CreateToolCheckMenuContent(toolId);
                if (!AnyExecutionRunning && serviceReady)
                    menu.AddItem(content, false, () => RunToolCheck(capturedId));
                else menu.AddDisabledItem(content);
            }

            menu.AddSeparator("");
            foreach (var toolId in UPilotQuickDebugToolCheck.ToolIds)
            {
                var capturedId = toolId;
                var content = new GUIContent("打开诊断日志/" + UPilotQuickDebugToolCheck.DisplayName(toolId));
                if (File.Exists(GetDiagnosticLogPath(toolId)) && !_unavailableDiagnosticLogs.Contains(toolId))
                    menu.AddItem(content, false, () => OpenDiagnosticLog(capturedId));
                else menu.AddDisabledItem(content);
            }

            if (_diagnosticRunning)
                menu.AddItem(new GUIContent("取消当前诊断"), false, CancelDiagnostic);
            else
                menu.AddDisabledItem(new GUIContent("取消当前诊断"));

            menu.ShowAsContext();
        }

        private void DrawTabBar()
        {
            _activeTab = GUILayout.Toolbar(_activeTab, CreateTabContents());
        }

        private static GUIContent[] CreateTabContents()
        {
            return new[]
            {
                new GUIContent(
                    "运行 C# 代码",
                    "用法：在代码框输入 C# 表达式或语句，然后点击“执行”。\n" +
                    "功能：支持变量、控制流、异常、闭包和 async/await；也可通过 session 保留变量或返回对象 handle。\n" +
                    "工具名：csharp_eval"),
                new GUIContent(
                    "调用现有方法",
                    "用法：填写类型名、方法名和参数后点击“执行”；实例方法还需要 sessionId 和 targetHandle。\n" +
                    "功能：调用已加载的静态、实例或泛型方法，可精确选择重载，并支持等待 Task/ValueTask 结果。\n" +
                    "工具名：unity_reflection_call"),
                new GUIContent(
                    "查看对象信息",
                    "用法：先在“运行 C# 代码”中使用持久 session 和 resultMode=handle 获取对象句柄，再填写 sessionId 和 handle。\n" +
                    "功能：查看对象的字段、属性和嵌套内容，可限制深度、包含静态成员，并选择 JSON 或文本视图。\n" +
                    "工具名：csharp_object_dump"),
            };
        }

        // ── Help panel helpers ────────────────────────────────────────────────

        private void HelpLabel(string text)
        {
            EditorGUILayout.LabelField(text, _styleHelpBox, GUILayout.ExpandWidth(true));
        }

        private void BeginHelpPanel(ref bool showHelp, string title)
        {
            showHelp = EditorGUILayout.BeginFoldoutHeaderGroup(showHelp, title);
            if (showHelp)
            {
                EditorGUI.indentLevel++;
            }
        }

        private void EndHelpPanel(ref bool showHelp)
        {
            if (showHelp)
            {
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        private bool ExampleButton(string label)
        {
            return GUILayout.Button(label, _styleExampleBtn);
        }

        private static GUIContent ParameterLabel(
            string label,
            string parameterName,
            string variableName,
            string description,
            string valueMeanings = null)
        {
            var tooltip = $"参数名：{parameterName}\n窗口变量：{variableName}\n{description}";
            if (!string.IsNullOrWhiteSpace(valueMeanings))
                tooltip += "\n取值含义：\n" + valueMeanings;
            return new GUIContent(label, tooltip);
        }

        private static string DrawStringPopup(
            GUIContent label,
            string currentValue,
            string[] values,
            GUIContent[] options,
            string defaultValue)
        {
            var selectedIndex = FindStringOption(currentValue, values);
            if (selectedIndex < 0)
                selectedIndex = Math.Max(0, FindStringOption(defaultValue, values));
            selectedIndex = EditorGUILayout.Popup(label, selectedIndex, options);
            return values[Mathf.Clamp(selectedIndex, 0, values.Length - 1)];
        }

        private static int FindStringOption(string value, string[] values)
        {
            for (var i = 0; i < values.Length; i++)
            {
                if (string.Equals(value, values[i], StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        // DrawCSharpEvalTab

        private void DrawCSharpEvalTab()
        {
            BeginHelpPanel(ref _evalShowHelp, "ℹ 使用说明");
            if (_evalShowHelp)
            {
                HelpLabel(
                    "<b>csharp_eval</b> 在 Unity 主线程执行受限的 C# 子集。\n" +
                    "支持变量、控制流、异常、闭包以及 async/await。\n" +
                    "<b>用法：</b>在下方输入 C# 代码，然后点击“执行”。\n\n" +
                    "<b>适用场景：</b>\n" +
                    "  - 测试表达式或 API 调用\n" +
                    "  - 为 csharp_object_dump 获取对象 handle\n" +
                    "  - 创建 session 范围内的变量\n" +
                    "  - 临时修改场景中的 GameObject\n\n" +
                    "<b>注意：</b>禁止访问文件系统、进程、网络、线程和程序集加载功能。");

                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("快捷示例（点击填入）：", EditorStyles.miniBoldLabel);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (ExampleButton("基本表达式")) FillEvalExample("expression");
                    if (ExampleButton("列表 + 字典")) FillEvalExample("list-dict");
                    if (ExampleButton("游戏对象")) FillEvalExample("gameobject");
                    if (ExampleButton("async/await")) FillEvalExample("async");
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (ExampleButton("try/catch/finally")) FillEvalExample("try-catch");
                    if (ExampleButton("闭包")) FillEvalExample("closure");
                    if (ExampleButton("Session 变量")) FillEvalExample("session-vars");
                    if (ExampleButton("foreach + 数组")) FillEvalExample("foreach-array");
                }
            }
            EndHelpPanel(ref _evalShowHelp);

            EditorGUILayout.Space(4);

            using (new EditorGUILayout.VerticalScope(_styleBox))
            {
                EditorGUILayout.LabelField(
                    ParameterLabel("C# 代码", "code", "_evalCode", "要执行的一条表达式或受支持的 C# 子集语句块。"),
                    EditorStyles.boldLabel);
                _evalCode = EditorGUILayout.TextArea(_evalCode, GUILayout.Height(140));

                EditorGUILayout.Space(2);
                _evalShowOptions = EditorGUILayout.BeginFoldoutHeaderGroup(_evalShowOptions, "可选参数");
                if (_evalShowOptions)
                {
                    EditorGUI.indentLevel++;
                    _evalMode = DrawStringPopup(
                        ParameterLabel("解析模式", "mode", "_evalMode", "决定将代码解析为单个表达式还是语句程序。", "auto：自动判断\nexpression：只接受一个表达式\nstatements：按语句程序解析"),
                        _evalMode,
                        EvalModeValues,
                        EvalModeOptions,
                        "auto");
                    _evalBackend = DrawStringPopup(
                        ParameterLabel("执行后端", "executionBackend", "_evalBackend", "选择 C# 子集的执行后端；开始执行后不会换后端重放。", "auto：自动选择\ninterpret：使用解释器\nemit：使用解释器入口缓存\ncompiled：直接编译静态同步子集"),
                        _evalBackend,
                        EvalBackendValues,
                        EvalBackendOptions,
                        "auto");
                    _evalResultMode = DrawStringPopup(
                        ParameterLabel("结果模式", "resultMode", "_evalResultMode", "控制结果的编码方式。", "auto：自动选择\ninline：内联返回可序列化值\nhandle：返回 session 对象句柄，必须填 sessionId\nlegacyString：按旧版字符串返回"),
                        _evalResultMode,
                        EvalResultModeValues,
                        EvalResultModeOptions,
                        "inline");
                    _evalSessionId = EditorGUILayout.TextField(
                        ParameterLabel("会话 ID", "sessionId", "_evalSessionId", "可选的持久会话 ID；跨调用变量、handle、事件订阅或 resultMode=handle 时必需。"),
                        _evalSessionId);
                    _evalVariablesJson = EditorGUILayout.TextField(
                        ParameterLabel("输入变量 JSON", "variables", "_evalVariablesJson", "变量名到普通 JSON 值或 TypedValue 的映射。"),
                        _evalVariablesJson);
                    _evalLimitsJson = EditorGUILayout.TextField(
                        ParameterLabel("预算限制 JSON", "limits", "_evalLimitsJson", "可选预算覆盖，例如 timeoutMs、maxStatements、maxLoopIterations、maxCalls 和 maxResultBytes。"),
                        _evalLimitsJson);
                    DrawEvalSelectionNotices();
                    EditorGUI.indentLevel--;
                }
                EditorGUILayout.EndFoldoutHeaderGroup();

                EditorGUILayout.Space(4);
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUI.enabled = CanRunCSharpEval();
                    if (GUILayout.Button("执行", GUILayout.Height(28), GUILayout.Width(80)))
                        RunCSharpEval();
                    GUI.enabled = _evalRunning;
                    if (GUILayout.Button("取消", GUILayout.Height(28), GUILayout.Width(80)))
                        CancelEval();
                    GUI.enabled = true;
                    GUILayout.FlexibleSpace();
                    if (_evalRunning)
                        EditorGUILayout.LabelField("执行中…", GUILayout.Width(60));
                }
            }
        }


        private void FillEvalExample(string key)
        {
            switch (key)
            {
                case "expression":
                    _evalCode = "var a = 2 + 3 * 4;\nreturn a;";
                    _evalMode = "auto";
                    _evalSessionId = "";
                    _evalResultMode = "inline";
                    _evalBackend = "auto";
                    break;
                case "list-dict":
                    _evalCode = "var list = new System.Collections.Generic.List<int>(new int[] { 10, 20, 30 });\nvar dict = new System.Collections.Generic.Dictionary<string, int> { [\"key\"] = 42 };\nlist";
                    _evalMode = "auto";
                    _evalSessionId = "";
                    _evalResultMode = "auto";
                    _evalBackend = "auto";
                    break;
                case "gameobject":
                    _evalCode = "var go = new UnityEngine.GameObject(\"UPilot_Test\");\ngo.transform.position = new UnityEngine.Vector3(0, 5, 0);\nreturn go.name;";
                    _evalMode = "auto";
                    _evalSessionId = "";
                    _evalResultMode = "inline";
                    _evalBackend = "auto";
                    break;
                case "async":
                    _evalCode = "var result = await System.Threading.Tasks.Task.FromResult(42);\nreturn result;";
                    _evalMode = "auto";
                    _evalSessionId = "";
                    _evalResultMode = "inline";
                    _evalBackend = "auto";
                    break;
                case "try-catch":
                    _evalCode = "var r = 0;\ntry { r = 100 / 0; }\ncatch (System.DivideByZeroException) { r = -1; }\nfinally { r += 10; }\nreturn r;";
                    _evalMode = "auto";
                    _evalSessionId = "";
                    _evalResultMode = "inline";
                    _evalBackend = "auto";
                    break;
                case "closure":
                    _evalCode = "var offset = 5;\nvar add = (int x) => { return x + offset; };\noffset = 10;\nreturn add(3);";
                    _evalMode = "auto";
                    _evalSessionId = "";
                    _evalResultMode = "inline";
                    _evalBackend = "auto";
                    break;
                case "session-vars":
                    _evalCode = "var counter = (int)(variables.TryGetValue(\"counter\", out var v) ? v : 0) + 1;\nreturn counter;";
                    _evalMode = "auto";
                    _evalSessionId = "s.<domain>.<id>";
                    _evalResultMode = "inline";
                    _evalBackend = "auto";
                    break;
                case "foreach-array":
                    _evalCode = "int[,] matrix = { { 1, 2 }, { 3, 4 } };\nvar sum = 0;\nforeach (var val in new int[] { 1, 2, 3 }) sum += val;\nreturn new object[] { sum, matrix };";
                    _evalMode = "auto";
                    _evalSessionId = "";
                    _evalResultMode = "inline";
                    _evalBackend = "auto";
                    break;
            }
            Repaint();
        }


        private async void RunCSharpEval()
        {
            if (_diagnosticRunning || _evalRunning) return;
            if (EvalHandleSessionMissing(_evalResultMode, _evalSessionId))
            {
                AppendLog("[csharp_eval 错误] resultMode=handle 必须填写持久会话 ID。", true);
                return;
            }
            var bridge = UPilotBridge.Instance;
            var service = bridge.ExecutionService;
            if (service == null)
            {
                AppendLog("UPilot Bridge 尚未就绪。", true);
                return;
            }

            _evalCts = new CancellationTokenSource();
            _evalRunning = true;
            Repaint();

            var id = System.Guid.NewGuid().ToString("N");
            var payload = CreateEvalPayload();
            var json = UnityEngine.JsonUtility.ToJson(new CSharpEvalMessage { payload = payload });

            try
            {
                await service.HandleCSharpEvalAsync(id, json, _evalCts.Token,
                    enqueue: action => action(),
                    sendResult: result =>
                    {
                        var sb2 = new System.Text.StringBuilder();
                        sb2.AppendLine("[csharp_eval 完成]");
                        sb2.AppendLine($"  modeUsed: {result.modeUsed}");
                        sb2.AppendLine($"  backendUsed: {result.backendUsed}");
                        sb2.AppendLine($"  resultType: {result.resultType}");
                        sb2.AppendLine($"  result: {result.result}");
                        if (result.resultValue != null)
                            sb2.AppendLine($"  resultValue.kind: {result.resultValue.kind}  summary: {result.resultValue.summary}");
                        if (result.resultHandle != null)
                            sb2.AppendLine($"  resultHandle: {result.resultHandle}");
                        if (result.sessionId != null)
                            sb2.AppendLine($"  sessionId: {result.sessionId}");
                        if (result.budget != null)
                            sb2.AppendLine($"  budget: statements={result.budget.statements} loopIterations={result.budget.loopIterations} calls={result.budget.calls} allocations={result.budget.allocations} elapsedMs={result.budget.elapsedMs}");
                        if (result.diagnostics != null && result.diagnostics.Length > 0)
                            sb2.AppendLine($"  diagnostics: {string.Join(", ", result.diagnostics)}");
                        AppendLog(sb2.ToString().TrimEnd());
                        return System.Threading.Tasks.Task.CompletedTask;
                    },
                    sendError: err =>
                    {
                        AppendLog($"[csharp_eval 错误] [{err.Code}] {err.Message}", true);
                        return System.Threading.Tasks.Task.CompletedTask;
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

        private void CancelEval() { try { _evalCts?.Cancel(); } catch { } }

        private CSharpEvalPayload CreateEvalPayload()
        {
            return new CSharpEvalPayload
            {
                code = _evalCode,
                mode = CanonicalOption(_evalMode, EvalModeValues, "auto"),
                sessionId = _evalSessionId ?? "",
                variablesJson = _evalVariablesJson ?? "",
                limitsJson = _evalLimitsJson ?? "",
                resultMode = CanonicalOption(_evalResultMode, EvalResultModeValues, "inline"),
                executionBackend = CanonicalOption(_evalBackend, EvalBackendValues, "auto"),
            };
        }

        private bool CanRunCSharpEval()
        {
            return !_diagnosticRunning &&
                   !_evalRunning &&
                   !string.IsNullOrWhiteSpace(_evalCode) &&
                   !EvalHandleSessionMissing(_evalResultMode, _evalSessionId);
        }

        private static bool EvalHandleSessionMissing(string resultMode, string sessionId)
        {
            return string.Equals(resultMode, "handle", StringComparison.OrdinalIgnoreCase) &&
                   string.IsNullOrWhiteSpace(sessionId);
        }

        private static string CanonicalOption(string value, string[] values, string defaultValue)
        {
            var index = FindStringOption(value, values);
            return index >= 0 ? values[index] : defaultValue;
        }

        private void DrawEvalSelectionNotices()
        {
            if (string.Equals(_evalMode, "auto", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_evalBackend, "auto", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_evalResultMode, "inline", StringComparison.OrdinalIgnoreCase))
                EditorGUILayout.HelpBox("推荐组合：自动解析、自动后端、内联结果，适合日常快捷调试。", MessageType.Info);

            if (string.Equals(_evalMode, "expression", StringComparison.OrdinalIgnoreCase))
                EditorGUILayout.HelpBox("expression 只接受一个表达式；变量声明、控制流或 return 请改用 statements 或 auto。", MessageType.Info);

            if (string.Equals(_evalBackend, "compiled", StringComparison.OrdinalIgnoreCase))
                EditorGUILayout.HelpBox("compiled 仅支持有限的同步、可静态绑定子集；不支持时会直接失败，不会回退到解释器。", MessageType.Warning);

            if (string.Equals(_evalResultMode, "legacyString", StringComparison.OrdinalIgnoreCase))
                EditorGUILayout.HelpBox("legacyString 仅用于旧版结果兼容；新调用建议使用 inline、auto 或 handle。", MessageType.Warning);

            if (EvalHandleSessionMissing(_evalResultMode, _evalSessionId))
                EditorGUILayout.HelpBox("resultMode=handle 必须填写持久会话 ID，填写前不能执行。", MessageType.Error);
        }

        private void DrawReflectionCallTab()
        {
            BeginHelpPanel(ref _reflectShowHelp, "ℹ 使用说明与示例");
            if (_reflectShowHelp)
            {
                HelpLabel(
                    "<b>unity_reflection_call</b> 用于调用已编译的静态或实例方法。\n" +
                    "必须填写 typeName 和 methodName；实例方法可选填写 targetHandle。\n\n" +
                    "<b>用法：</b>填写类型名和方法名，然后点击“执行”。\n" +
                    "表达式请使用 <b>运行 C# 代码</b> 页签。\n\n" +
                    "<b>主要参数：</b>\n" +
                    "  typeName：完整类型名，例如 UnityEngine.Debug\n" +
                    "  methodName：方法名，例如 Log\n" +
                    "  argumentsJson：JSON 数组或 { values: [...] } 对象\n" +
                    "  targetHandle：实例句柄，静态调用留空");

                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("快捷示例：", EditorStyles.miniBoldLabel);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (ExampleButton("Debug.Log")) FillReflectExample("debug-log");
                    if (ExampleButton("DateTime.Now")) FillReflectExample("datetime");
                    if (ExampleButton("查找 GameObject")) FillReflectExample("find");
                    if (ExampleButton("EditorUtility")) FillReflectExample("editor-utility");
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (ExampleButton("场景管理")) FillReflectExample("scene");
                    if (ExampleButton("PlayerPrefs")) FillReflectExample("playerprefs");
                }
            }
            EndHelpPanel(ref _reflectShowHelp);

            EditorGUILayout.Space(4);

            using (new EditorGUILayout.VerticalScope(_styleBox))
            {
                EditorGUILayout.LabelField("方法调用（表达式请使用“运行 C# 代码”页签）", EditorStyles.miniLabel);
                EditorGUILayout.Space(2);
                _reflectTypeName = EditorGUILayout.TextField(
                    ParameterLabel("类型名", "typeName", "_reflectTypeName", "声明目标方法的完整类型名，例如 UnityEngine.Debug。"),
                    _reflectTypeName);
                _reflectMethodName = EditorGUILayout.TextField(
                    ParameterLabel("方法名", "methodName", "_reflectMethodName", "要调用的已编译方法名，例如 Log。"),
                    _reflectMethodName);
                _reflectArgumentsJson = EditorGUILayout.TextField(
                    ParameterLabel("参数 JSON", "arguments", "_reflectArgumentsJson", "方法的 typed/named 参数列表；本窗口接受 JSON 数组或包含 values 的对象。"),
                    _reflectArgumentsJson);

                EditorGUILayout.Space(2);
                _reflectShowOptions = EditorGUILayout.BeginFoldoutHeaderGroup(_reflectShowOptions, "可选参数");
                if (_reflectShowOptions)
                {
                    EditorGUI.indentLevel++;
                    _reflectTargetHandle = EditorGUILayout.TextField(
                        ParameterLabel("目标句柄", "targetHandle", "_reflectTargetHandle", "持久 session 中的实例 handle；填写后强制按实例方法调用。"),
                        _reflectTargetHandle);
                    _reflectParameterTypeNames = EditorGUILayout.TextField(
                        ParameterLabel("参数类型名（逗号分隔）", "parameterTypeNames", "_reflectParameterTypeNames", "用于精确选择重载的参数类型名列表。"),
                        _reflectParameterTypeNames);
                    _reflectGenericTypeArguments = EditorGUILayout.TextField(
                        ParameterLabel("泛型类型实参（逗号分隔）", "genericTypeArguments", "_reflectGenericTypeArguments", "显式闭合泛型方法的类型参数名列表。"),
                        _reflectGenericTypeArguments);
                    _reflectSessionId = EditorGUILayout.TextField(
                        ParameterLabel("会话 ID", "sessionId", "_reflectSessionId", "targetHandle 或 resultMode=handle 所属的持久 session ID。"),
                        _reflectSessionId);
                    _reflectResultMode = EditorGUILayout.TextField(
                        ParameterLabel("结果模式", "resultMode", "_reflectResultMode", "控制结果的编码方式。", "auto：自动选择\ninline：内联返回可序列化值\nhandle：返回 session 对象句柄，必须填 sessionId\nlegacyString：按旧版字符串返回"),
                        _reflectResultMode);
                    _reflectAwaitMode = EditorGUILayout.TextField(
                        ParameterLabel("等待模式", "awaitMode", "_reflectAwaitMode", "决定如何处理 Task/ValueTask 等可等待结果。", "auto：发现可等待结果时自动等待\nalways：要求结果可等待并等待\nnever：不等待，直接处理原始结果"),
                        _reflectAwaitMode);
                    _reflectAwaitTimeoutMs = EditorGUILayout.IntField(
                        ParameterLabel("等待超时（毫秒）", "awaitTimeoutMs", "_reflectAwaitTimeoutMs", "等待 Task/ValueTask 的有界超时时间，默认 3000 毫秒。"),
                        _reflectAwaitTimeoutMs);
                    EditorGUI.indentLevel--;
                }
                EditorGUILayout.EndFoldoutHeaderGroup();

                EditorGUILayout.Space(4);
                using (new EditorGUILayout.HorizontalScope())
                {
                    var hasInput = !string.IsNullOrWhiteSpace(_reflectTypeName) && !string.IsNullOrWhiteSpace(_reflectMethodName);
                    GUI.enabled = !_diagnosticRunning && !_reflectRunning && hasInput;
                    if (GUILayout.Button("执行", GUILayout.Height(28), GUILayout.Width(80)))
                        RunReflectionCall();
                    GUI.enabled = _reflectRunning;
                    if (GUILayout.Button("取消", GUILayout.Height(28), GUILayout.Width(80)))
                        CancelReflect();
                    GUI.enabled = true;
                    GUILayout.FlexibleSpace();
                    if (_reflectRunning)
                        EditorGUILayout.LabelField("执行中…", GUILayout.Width(60));
                }
            }
        }


        private void FillReflectExample(string key)
        {
            switch (key)
            {
                case "debug-log":
                    _reflectTypeName = "UnityEngine.Debug";
                    _reflectMethodName = "Log";
                    _reflectArgumentsJson = "[\"Hello from UPilot!\"]";
                    _reflectParameterTypeNames = "System.String";
                    _reflectTargetHandle = "";
                    _reflectResultMode = "inline";
                    _reflectAwaitMode = "auto";
                    break;
                case "datetime":
                    _reflectTypeName = "System.DateTime";
                    _reflectMethodName = "get_Now";
                    _reflectArgumentsJson = "";
                    _reflectParameterTypeNames = "";
                    _reflectTargetHandle = "";
                    _reflectResultMode = "inline";
                    _reflectAwaitMode = "auto";
                    break;
                case "find":
                    _reflectTypeName = "UnityEngine.GameObject";
                    _reflectMethodName = "Find";
                    _reflectArgumentsJson = "[\"Main Camera\"]";
                    _reflectParameterTypeNames = "System.String";
                    _reflectTargetHandle = "";
                    _reflectResultMode = "inline";
                    _reflectAwaitMode = "auto";
                    break;
                case "editor-utility":
                    _reflectTypeName = "UnityEditor.EditorUtility";
                    _reflectMethodName = "DisplayDialog";
                    _reflectArgumentsJson = "[\"UPilot\", \"方法调用测试\", \"确定\", \"取消\"]";
                    _reflectParameterTypeNames = "System.String,System.String,System.String,System.String";
                    _reflectTargetHandle = "";
                    _reflectResultMode = "inline";
                    _reflectAwaitMode = "auto";
                    break;
                case "scene":
                    _reflectTypeName = "UnityEngine.SceneManagement.SceneManager";
                    _reflectMethodName = "GetActiveScene";
                    _reflectArgumentsJson = "";
                    _reflectParameterTypeNames = "";
                    _reflectTargetHandle = "";
                    _reflectResultMode = "inline";
                    _reflectAwaitMode = "auto";
                    break;
                case "playerprefs":
                    _reflectTypeName = "UnityEngine.PlayerPrefs";
                    _reflectMethodName = "SetInt";
                    _reflectArgumentsJson = "[\"UPilot_TestKey\", 123]";
                    _reflectParameterTypeNames = "System.String,System.Int32";
                    _reflectTargetHandle = "";
                    _reflectResultMode = "inline";
                    _reflectAwaitMode = "auto";
                    break;
            }
            Repaint();
        }

        private async void RunReflectionCall()
        {
            if (_diagnosticRunning || _reflectRunning) return;
            var bridge = UPilotBridge.Instance;
            var service = bridge.ReflectionService;
            if (service == null)
            {
                AppendLog("UPilot Bridge 尚未就绪。", true);
                return;
            }

            _reflectCts = new CancellationTokenSource();
            _reflectRunning = true;
            Repaint();

            var id = System.Guid.NewGuid().ToString("N");
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
            var json = UnityEngine.JsonUtility.ToJson(new ReflectionCallMessage { payload = payload });

            try
            {
                await service.HandleCallAsync(id, json, _reflectCts.Token,
                    enqueue: action => action(),
                    sendResult: result =>
                    {
                        var sb2 = new System.Text.StringBuilder();
                        sb2.AppendLine("[unity_reflection_call 完成]");
                        if (result.typeName != null) sb2.AppendLine($"  typeName: {result.typeName}");
                        if (result.methodName != null) sb2.AppendLine($"  methodName: {result.methodName}");
                        if (result.invokedSignature != null) sb2.AppendLine($"  invokedSignature: {result.invokedSignature}");
                        sb2.AppendLine($"  result: {result.result}");
                        if (result.resultValue != null)
                            sb2.AppendLine($"  resultValue.kind: {result.resultValue.kind}  summary: {result.resultValue.summary}");
                        if (result.wasAwaitable)
                            sb2.AppendLine($"  wasAwaitable: true  awaitableStatus: {result.awaitableStatus}");
                        if (result.refOutArguments != null && result.refOutArguments.Count > 0)
                        {
                            sb2.AppendLine("  refOutArguments:");
                            foreach (var ra in result.refOutArguments)
                                sb2.AppendLine($"    {ra.name} ({ra.direction}): {ra.value?.summary ?? "(null)"}");
                        }
                        AppendLog(sb2.ToString().TrimEnd());
                        return System.Threading.Tasks.Task.CompletedTask;
                    },
                    sendError: err =>
                    {
                        AppendLog($"[unity_reflection_call 错误] [{err.Code}] {err.Message}", true);
                        return System.Threading.Tasks.Task.CompletedTask;
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

        private void CancelReflect() { try { _reflectCts?.Cancel(); } catch { } }


        // ObjectDump tab

        private void DrawObjectDumpTab()
        {
            BeginHelpPanel(ref _dumpShowHelp, "ℹ 使用说明");
            if (_dumpShowHelp)
            {
                HelpLabel(
                    "<b>csharp_object_dump</b> 用于查看对象图，可显示为 JSON 树或缩进文本。\n" +
                    "<b>前置条件：</b>需要持久 sessionId，以及 csharp_eval 通过 resultMode=handle 返回的 handle。\n\n" +
                    "<b>结果文件：</b>每次开始执行时清空 <i>Logs/UPilot/csharp_object_dump.log</i>，完成后仅保存本次结果。\n\n" +
                    "<b>快捷流程：</b>\n" +
                    "  1. 点击下方“一键运行并查看列表”\n" +
                    "  2. 或先在“运行 C# 代码”页签以 resultMode=handle 执行，\n" +
                    "     再把返回的 sessionId 和 resultHandle 粘贴到此处。\n\n" +
                    "工具自检请使用窗口顶部的“诊断”菜单。");

                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("一键操作：", EditorStyles.miniBoldLabel);

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUI.enabled = !_diagnosticRunning && !_dumpQuickRunning && UPilotBridge.Instance.ExecutionService != null;
                    if (GUILayout.Button("一键运行并查看列表", GUILayout.Height(28)))
                        RunOneClickEvalAndDump();
                    GUI.enabled = _dumpQuickRunning;
                    if (GUILayout.Button("取消", GUILayout.Height(28), GUILayout.Width(60)))
                    {
                        try { _dumpQuickCts?.Cancel(); } catch { }
                    }
                    GUI.enabled = true;
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (ExampleButton("查看此 GameObject")) FillDumpExample("gameobject");
                    if (ExampleButton("查看 Transform")) FillDumpExample("transform");
                    if (ExampleButton("查看字典")) FillDumpExample("dict");
                }
            }
            EndHelpPanel(ref _dumpShowHelp);

            EditorGUILayout.Space(4);

            using (new EditorGUILayout.VerticalScope(_styleBox))
            {
                EditorGUILayout.LabelField("查看对象信息（请先通过 csharp_eval 获取 handle）", EditorStyles.miniLabel);
                EditorGUILayout.Space(2);
                _dumpSessionId = EditorGUILayout.TextField(
                    ParameterLabel("会话 ID", "sessionId", "_dumpSessionId", "保存目标 handle 的持久 execution session ID。"),
                    _dumpSessionId);
                _dumpHandle = EditorGUILayout.TextField(
                    ParameterLabel("对象句柄", "handle", "_dumpHandle", "来自同一 session 的对象 handle，例如 csharp_eval 在 resultMode=handle 时返回的 resultHandle。"),
                    _dumpHandle);

                EditorGUILayout.Space(2);
                _dumpShowOptions = EditorGUILayout.BeginFoldoutHeaderGroup(_dumpShowOptions, "可选参数");
                if (_dumpShowOptions)
                {
                    EditorGUI.indentLevel++;
                    _dumpMaxDepth = EditorGUILayout.IntSlider(
                        ParameterLabel("最大深度", "maxDepth", "_dumpMaxDepth", "最大递归深度；0 表示不展开子对象，默认 3，上限 64。"),
                        _dumpMaxDepth, 0, 64);
                    _dumpMaxFieldsPerNode = EditorGUILayout.IntSlider(
                        ParameterLabel("每节点最大字段数", "maxFieldsPerNode", "_dumpMaxFieldsPerNode", "每个对象节点最多展开的字段数，默认 100，上限 500。"),
                        _dumpMaxFieldsPerNode, 1, 500);
                    _dumpMaxTotalNodes = EditorGUILayout.IntSlider(
                        ParameterLabel("总节点上限", "maxTotalNodes", "_dumpMaxTotalNodes", "整个对象图允许访问的最大节点数，默认 5000，上限 20000。"),
                        _dumpMaxTotalNodes, 1, 20000);
                    _dumpIncludeStatic = EditorGUILayout.Toggle(
                        ParameterLabel("包含静态成员", "includeStatic", "_dumpIncludeStatic", "是否同时转储静态字段和属性。", "false：不包含静态成员（默认）\ntrue：包含静态字段和属性"),
                        _dumpIncludeStatic);
                    _dumpIncludeTypeNames = EditorGUILayout.Toggle(
                        ParameterLabel("显示类型名称", "includeTypeNames", "_dumpIncludeTypeNames", "是否在文本结果中显示节点的声明类型和运行时类型；结构化 root 中的类型字段始终保留。", "false：隐藏 (Type) 和 (DeclaredType -> RuntimeType) 注记（默认）\ntrue：显示完整类型注记"),
                        _dumpIncludeTypeNames);
                    _dumpExpandReflectionTypes = EditorGUILayout.Toggle(
                        ParameterLabel("展开反射内部信息", "expandReflectionTypes", "_dumpExpandReflectionTypes", "是否递归展开 Delegate、Assembly、Module 和 MemberInfo 等反射基础类型。", "false：仅显示有界摘要（默认），普通字段和属性值仍正常显示\ntrue：递归展开反射内部结构"),
                        _dumpExpandReflectionTypes);
                    _dumpOutputFormat = EditorGUILayout.TextField(
                        ParameterLabel("输出格式", "outputFormat", "_dumpOutputFormat", "选择首选的输出视图；响应始终同时包含结构化树和文本。", "json：以结构化 JSON 树为首选视图（默认）\ntext：以缩进文本为首选视图"),
                        _dumpOutputFormat);
                    _dumpIndentation = EditorGUILayout.TextField(
                        ParameterLabel("文本缩进", "indentation", "_dumpIndentation", "text 模式使用的缩进字符串，默认为两个空格。"),
                        _dumpIndentation);
                    _dumpIgnoreTypes = EditorGUILayout.TextField(
                        ParameterLabel("忽略类型（逗号分隔）", "ignoreTypes", "_dumpIgnoreTypes", "不继续展开子字段的完整类型名列表；匹配后仅显示类型名。"),
                        _dumpIgnoreTypes);
                    EditorGUI.indentLevel--;
                }
                EditorGUILayout.EndFoldoutHeaderGroup();

                EditorGUILayout.Space(4);
                using (new EditorGUILayout.HorizontalScope())
                {
                    var hasInput = !string.IsNullOrWhiteSpace(_dumpSessionId) && !string.IsNullOrWhiteSpace(_dumpHandle);
                    GUI.enabled = !_diagnosticRunning && !_dumpRunning && hasInput;
                    if (GUILayout.Button("执行", GUILayout.Height(28), GUILayout.Width(80)))
                        RunObjectDump();
                    GUI.enabled = _dumpRunning;
                    if (GUILayout.Button("取消", GUILayout.Height(28), GUILayout.Width(80)))
                        CancelDump();
                    GUI.enabled = true;
                    GUILayout.FlexibleSpace();
                    if (_dumpRunning)
                        EditorGUILayout.LabelField("执行中…", GUILayout.Width(60));
                }
            }
        }

        private void FillDumpExample(string key)
        {
            switch (key)
            {
                case "gameobject":
                    _dumpQuickEvalCode = "var go = new UnityEngine.GameObject(\"UPilot_DumpTest\");\ngo.transform.position = new UnityEngine.Vector3(1, 2, 3);\ngo";
                    _dumpOutputFormat = "text";
                    _dumpMaxDepth = 3;
                    Repaint();
                    return;
                case "transform":
                    _dumpQuickEvalCode = "UnityEngine.Object.FindObjectOfType<UnityEngine.Transform>()";
                    _dumpOutputFormat = "json";
                    _dumpMaxDepth = 2;
                    Repaint();
                    return;
                case "dict":
                    _dumpQuickEvalCode = "new System.Collections.Generic.Dictionary<string, int> { [\"a\"] = 1, [\"b\"] = 2, [\"c\"] = 3 }";
                    _dumpOutputFormat = "json";
                    _dumpMaxDepth = 3;
                    Repaint();
                    return;
            }
        }

        private bool AnyExecutionRunning => _diagnosticRunning || _evalRunning || _reflectRunning ||
                                            _dumpRunning || _dumpQuickRunning;

        private static GUIContent CreateToolCheckMenuContent(string toolId)
        {
            var coverage = toolId == "csharp_eval"
                ? "检查表达式、控制流、集合、异常、闭包、异步、会话、句柄及预期拒绝。"
                : toolId == "unity_reflection_call"
                    ? "使用内置 TestUPilotQuickDebugObject 检查参数、重载、泛型、ref/out、异步、句柄和仅调用一次。"
                    : "使用内置 TestUPilotDumpObject 检查基础类型、集合、循环引用、三层继承成员和安全限制。";
            return new GUIContent("检查“" + UPilotQuickDebugToolCheck.DisplayName(toolId) + "”工具",
                toolId + "：" + coverage + "\n仅检查本地 Unity 服务，不验证 MCP 传输。\n诊断日志：" +
                UPilotQuickDebugToolCheck.RelativeLogPath(toolId));
        }

        private void CancelDiagnostic()
        {
            if (!_diagnosticRunning) return;
            try { _diagnosticCts?.Cancel(); } catch (ObjectDisposedException) { }
        }

        private static string GetDiagnosticLogPath(string toolId)
        {
            var root = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            return Path.Combine(root, UPilotQuickDebugToolCheck.RelativeLogPath(toolId));
        }

        private void OpenDiagnosticLog(string toolId)
        {
            var path = GetDiagnosticLogPath(toolId);
            if (!File.Exists(path))
            {
                AppendLog("[诊断日志] 日志文件尚未生成。", true);
                return;
            }

            EditorUtility.OpenWithDefaultApp(path);
        }

        private async void RunToolCheck(string toolId)
        {
            var bridge = UPilotBridge.Instance;
            await RunToolCheckAsync(toolId, async (token, progress) =>
            {
                switch (toolId)
                {
                    case "csharp_eval":
                        return await UPilotCSharpEvalToolCheck.RunAsync(bridge.ExecutionService, token, progress: progress);
                    case "unity_reflection_call":
                        return await UPilotReflectionToolCheck.RunAsync(bridge.ExecutionService, bridge.ReflectionService, token, progress);
                    default:
                        return await UPilotObjectDumpToolCheck.RunAsync(bridge.ExecutionService, token, progress: progress);
                }
            });
        }

        private async Task RunToolCheckAsync(string toolId,
            Func<CancellationToken, Action<string>, Task<UPilotToolCheckResult>> runCheck,
            Action<UPilotToolCheckResult> publishResult = null, Action<string, string> writeReport = null)
        {
            if (AnyExecutionRunning) return;
            var pending = new UPilotToolCheckResult(toolId);
            var path = GetDiagnosticLogPath(toolId);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            _diagnosticCts = new CancellationTokenSource();
            _diagnosticRunning = true;
            _diagnosticProgress = pending.DisplayName + " 初始化";
            writeReport = writeReport ?? UPilotQuickDebugToolCheck.WriteReport;
            var logError = "";
            var reportSaved = false;
            try
            {
                try { writeReport(path, UPilotQuickDebugToolCheck.PendingReport(pending)); }
                catch (Exception ex) { logError = "未完成报告写入失败：" + ex.Message; }
                UPilotToolCheckResult result;
                try
                {
                    result = await runCheck(_diagnosticCts.Token, progress => _diagnosticProgress = progress);
                    if (result == null) throw new InvalidOperationException("检查未返回结果。");
                }
                catch (Exception ex)
                {
                    result = pending;
                    result.Status = ex is OperationCanceledException
                        ? UPilotToolCheckStatus.Cancelled : UPilotToolCheckStatus.UnableToCheck;
                    result.FailedChecks.Add(ex.GetType().Name + ": " + ex.Message);
                }
                result.RunId = pending.RunId;
                result.StartedAtUtc = pending.StartedAtUtc;
                result.ElapsedMs = clock.ElapsedMilliseconds;
                if (_diagnosticCts.IsCancellationRequested) result.Status = UPilotToolCheckStatus.Cancelled;
                result.Complete();
                if (!string.IsNullOrEmpty(logError)) result.Warnings.Add(logError);
                try
                {
                    writeReport(path, result.BuildFullReport());
                    reportSaved = true;
                    _unavailableDiagnosticLogs.Remove(toolId);
                }
                catch (Exception ex)
                {
                    logError = "诊断日志写入失败：" + ex.Message;
                    result.Warnings.Add(logError);
                    _unavailableDiagnosticLogs.Add(toolId);
                }
                if (publishResult != null) publishResult(result);
                else AppendLog(result.BuildSummary(reportSaved), result.IsErrorForUi || !reportSaved);
            }
            finally
            {
                _diagnosticRunning = false;
                _diagnosticCts?.Dispose();
                _diagnosticCts = null;
                _diagnosticProgress = "";
                if (!_windowDisabled && this != null) Repaint();
            }
        }

        private async void RunOneClickEvalAndDump()
        {
            if (_diagnosticRunning || _dumpQuickRunning) return;

            var bridge = UPilotBridge.Instance;
            var service = bridge.ExecutionService;
            if (service == null) { AppendLog("UPilot Bridge 尚未就绪。", true); return; }

            ResetObjectDumpLog();
            _dumpQuickCts = new CancellationTokenSource();
            _dumpQuickRunning = true;
            Repaint();

            var code = string.IsNullOrWhiteSpace(_dumpQuickEvalCode)
                ? "var list = new System.Collections.Generic.List<int>(new int[] { 1, 2, 3 });\nvar dict = new System.Collections.Generic.Dictionary<string, int> { [\"a\"] = 1, [\"b\"] = 2 };\nlist"
                : _dumpQuickEvalCode;
            string ownedSessionId = null;

            try
            {
                var session = service.Sessions.Open("快捷调试-查看对象信息", 600, 32, 4, 4, 4);
                ownedSessionId = session.Id;
                var evalPayload = new CSharpEvalPayload
                {
                    code = code,
                    sessionId = ownedSessionId,
                    variablesJson = "",
                    limitsJson = "",
                    resultMode = "handle",
                    executionBackend = "auto",
                };
                var evalJson = UnityEngine.JsonUtility.ToJson(new CSharpEvalMessage { payload = evalPayload });

                string sessionId = null;
                string handle = null;
                Exception evalError = null;

                await service.HandleCSharpEvalAsync("qd-oc-" + System.Guid.NewGuid().ToString("N"), evalJson, _dumpQuickCts.Token,
                    enqueue: a => a(),
                    sendResult: r =>
                    {
                        sessionId = r.sessionId;
                        handle = r.resultHandle;
                        AppendLog("[一键操作] C# 执行完成。resultType=" + r.resultType + " handle=" + handle);
                        return System.Threading.Tasks.Task.CompletedTask;
                    },
                    sendError: e => { evalError = new Exception(e.Code + ": " + e.Message); return System.Threading.Tasks.Task.CompletedTask; });

                if (evalError != null) { PublishObjectDumpLog("[一键操作] C# 执行失败：" + evalError.Message, true); return; }
                if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(handle))
                { PublishObjectDumpLog("[一键操作] 未返回 sessionId 或 handle。", true); return; }

                _dumpSessionId = sessionId;
                _dumpHandle = handle;

                var dumpPayload = new ObjectDumpPayload
                {
                    sessionId = sessionId,
                    handle = handle,
                    maxDepth = _dumpMaxDepth,
                    maxFieldsPerNode = _dumpMaxFieldsPerNode,
                    maxTotalNodes = _dumpMaxTotalNodes,
                    includeStatic = _dumpIncludeStatic,
                    includeTypeNames = _dumpIncludeTypeNames,
                    expandReflectionTypes = _dumpExpandReflectionTypes,
                    outputFormat = string.IsNullOrWhiteSpace(_dumpOutputFormat) ? "json" : _dumpOutputFormat,
                    indentation = string.IsNullOrWhiteSpace(_dumpIndentation) ? "  " : _dumpIndentation,
                    ignoreTypes = string.IsNullOrWhiteSpace(_dumpIgnoreTypes) ? Array.Empty<string>() : _dumpIgnoreTypes.Split(new[] { ',' }, System.StringSplitOptions.RemoveEmptyEntries),
                };
                var dumpJson = UnityEngine.JsonUtility.ToJson(new ObjectDumpMessage { payload = dumpPayload });

                await service.HandleObjectDumpAsync("qd-oc-dump-" + System.Guid.NewGuid().ToString("N"), dumpJson, _dumpQuickCts.Token,
                    enqueue: a => a(),
                    sendResult: r =>
                    {
                        var sb2 = new System.Text.StringBuilder();
                        sb2.AppendLine("[一键查看对象信息完成]");
                        sb2.AppendLine("  typeName: " + r.typeName);
                        sb2.AppendLine("  totalNodes: " + r.totalNodes);
                        if (r.truncated)
                            sb2.AppendLine("  truncated: true  reason: " + r.truncateReason);
                        sb2.AppendLine("---");
                        sb2.AppendLine(r.text ?? "");
                        PublishObjectDumpLog(sb2.ToString().TrimEnd());
                        return System.Threading.Tasks.Task.CompletedTask;
                    },
                    sendError: err =>
                    {
                        PublishObjectDumpLog("[一键查看对象信息错误] [" + err.Code + "] " + err.Message, true);
                        return System.Threading.Tasks.Task.CompletedTask;
                    });

            }
            catch (Exception ex)
            {
                PublishObjectDumpLog("[一键操作异常] " + ex.GetType().Name + ": " + ex.Message, true);
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(ownedSessionId))
                {
                    try { service.Sessions.Close(ownedSessionId); }
                    catch (Exception ex) { AppendLog("[一键操作] 关闭临时会话失败：" + ex.Message, true); }
                }
                _dumpSessionId = "";
                _dumpHandle = "";
                _dumpQuickRunning = false;
                _dumpQuickCts?.Dispose();
                _dumpQuickCts = null;
                Repaint();
            }
        }

        private async void RunObjectDump()
        {
            if (_diagnosticRunning || _dumpRunning) return;
            var bridge = UPilotBridge.Instance;
            var service = bridge.ExecutionService;
            if (service == null) { AppendLog("UPilot Bridge 尚未就绪。", true); return; }

            ResetObjectDumpLog();
            _dumpCts = new CancellationTokenSource();
            _dumpRunning = true;
            Repaint();

            var id = System.Guid.NewGuid().ToString("N");
            var ignoreTypes = string.IsNullOrWhiteSpace(_dumpIgnoreTypes)
                ? Array.Empty<string>()
                : _dumpIgnoreTypes.Split(new[] { ',' }, System.StringSplitOptions.RemoveEmptyEntries);
            var payload = new ObjectDumpPayload
            {
                sessionId = _dumpSessionId,
                handle = _dumpHandle,
                maxDepth = _dumpMaxDepth,
                maxFieldsPerNode = _dumpMaxFieldsPerNode,
                maxTotalNodes = _dumpMaxTotalNodes,
                includeStatic = _dumpIncludeStatic,
                includeTypeNames = _dumpIncludeTypeNames,
                expandReflectionTypes = _dumpExpandReflectionTypes,
                outputFormat = string.IsNullOrWhiteSpace(_dumpOutputFormat) ? "json" : _dumpOutputFormat,
                indentation = string.IsNullOrWhiteSpace(_dumpIndentation) ? "  " : _dumpIndentation,
                ignoreTypes = ignoreTypes,
            };
            var json = UnityEngine.JsonUtility.ToJson(new ObjectDumpMessage { payload = payload });

            try
            {
                await service.HandleObjectDumpAsync(id, json, _dumpCts.Token,
                    enqueue: action => action(),
                    sendResult: result =>
                    {
                        var sb2 = new System.Text.StringBuilder();
                        sb2.AppendLine("[csharp_object_dump 完成]");
                        sb2.AppendLine("  typeName: " + result.typeName);
                        sb2.AppendLine("  totalNodes: " + result.totalNodes);
                        if (result.truncated)
                            sb2.AppendLine("  truncated: true  reason: " + result.truncateReason);
                        sb2.AppendLine("---");
                        sb2.AppendLine(result.text ?? "");
                        PublishObjectDumpLog(sb2.ToString().TrimEnd());
                        return System.Threading.Tasks.Task.CompletedTask;
                    },
                    sendError: err =>
                    {
                        PublishObjectDumpLog("[csharp_object_dump 错误] [" + err.Code + "] " + err.Message, true);
                        return System.Threading.Tasks.Task.CompletedTask;
                    });
            }
            catch (Exception ex)
            {
                PublishObjectDumpLog("[csharp_object_dump 异常] " + ex.GetType().Name + ": " + ex.Message, true);
            }
            finally
            {
                _dumpRunning = false;
                _dumpCts?.Dispose();
                _dumpCts = null;
                Repaint();
            }
        }

        private void CancelDump() { try { _dumpCts?.Cancel(); } catch { } }

        private static string GetObjectDumpLogPath()
        {
            var projectDirectory = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            return Path.Combine(projectDirectory, ObjectDumpLogRelativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        private static void OverwriteObjectDumpLogFile(string path, string content)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(path, content ?? string.Empty, new UTF8Encoding(false));
        }

        private void ResetObjectDumpLog()
        {
            try
            {
                OverwriteObjectDumpLogFile(GetObjectDumpLogPath(), string.Empty);
            }
            catch (Exception ex)
            {
                AppendLog("[对象信息日志] 无法清空日志文件：" + ex.Message, true);
            }
        }

        private void PublishObjectDumpLog(string message, bool isError = false)
        {
            try
            {
                OverwriteObjectDumpLogFile(GetObjectDumpLogPath(), message);
            }
            catch (Exception ex)
            {
                AppendLog("[对象信息日志] 无法写入日志文件：" + ex.Message, true);
            }
            AppendLog(message, isError);
        }

        private void DrawLogArea()
        {
            using (new EditorGUILayout.VerticalScope(_styleBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("日志", EditorStyles.boldLabel);
                    GUILayout.FlexibleSpace();
                    _autoScroll = EditorGUILayout.ToggleLeft(
                        ParameterLabel("自动滚动", "autoScroll", "_autoScroll", "控制新日志到达后是否自动滚动到底部。", "false：保持当前滚动位置\ntrue：自动滚动到最新日志"),
                        _autoScroll,
                        GUILayout.Width(92));
                    if (GUILayout.Button("清空", GUILayout.Width(50)))
                    { _logEntries.Clear(); _logEntryErrors.Clear(); _logScroll = Vector2.zero; }
                }

                EditorGUILayout.Space(2);

                var logHeight = Mathf.Clamp(position.height * 0.28f, 100f, 400f);
                _logScroll = EditorGUILayout.BeginScrollView(_logScroll, GUI.skin.box, GUILayout.Height(logHeight), GUILayout.ExpandWidth(true));
                for (var index = 0; index < _logEntries.Count; index++)
                {
                    var style = _logEntryErrors[index] ? _styleLogError : _styleLog;
                    EditorGUILayout.LabelField(_logEntries[index], style, GUILayout.ExpandWidth(true));
                }
                EditorGUILayout.EndScrollView();

                if (_autoScroll && _logEntries.Count > 0 && Event.current.type == EventType.Repaint)
                    _logScroll.y = float.MaxValue;
            }
        }

        private void AppendLog(string message, bool isError = false)
        {
            _logEntries.Add(message);
            _logEntryErrors.Add(isError);
            if (_logEntries.Count > MaxLogEntries)
            {
                _logEntryErrors.RemoveRange(0, _logEntries.Count - MaxLogEntries);
                _logEntries.RemoveRange(0, _logEntries.Count - MaxLogEntries);
            }
            if (isError)
                Debug.LogError("[UPilot QuickDebug] " + message);
            else
                Debug.Log("[UPilot QuickDebug] " + message);
            if (!_windowDisabled && this != null) Repaint();
        }

        private void CancelAll()
        {
            CancelEval();
            CancelReflect();
            CancelDump();
            CancelDiagnostic();
            try { _dumpQuickCts?.Cancel(); } catch { }
        }
    }
}
