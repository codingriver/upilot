// -----------------------------------------------------------------------
// Upilot Editor — https://github.com/codingriver/upilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;

namespace codingriver.upilot
{
    [Serializable]
    public class BridgeEnvelope
    {
        public string id;
        public string type;
        public string name;
        public string sessionId;
        public string protocolVersion = "1.0";
        public long timestamp;
    }

    [Serializable]
    public class HelloPayload
    {
        public string unityVersion;
        public string projectPath;
        public string platform;
    }

    [Serializable]
    public class HeartbeatPayload { }

    /// <summary>session.hello 成功时服务端 payload（含 MCP 显示名与监听地址）。</summary>
    [Serializable]
    public class HelloAckPayload
    {
        public bool accepted;
        public int heartbeatIntervalMs;
        public string mcpLabel;
        public string mcpHost;
        public int mcpPort;
        /// <summary>MCP Python 进程当前工作目录绝对路径（通常为 Cursor 打开的仓库根目录）。</summary>
        public string mcpWorkingDirectory;
    }

    [Serializable]
    public class HelloAckMessage
    {
        public string id;
        public string type;
        public string name;
        public HelloAckPayload payload;
        public long timestamp;
        public string sessionId;
        public string protocolVersion;
    }

    [Serializable]
    public class HelloMessage
    {
        public string id;
        public string type;
        public string name;
        public HelloPayload payload;
        public long timestamp;
        public string sessionId;
        public string protocolVersion = "1.0";
    }

    [Serializable]
    public class HeartbeatMessage
    {
        public string id;
        public string type;
        public string name;
        public HeartbeatPayload payload;
        public long timestamp;
        public string sessionId;
        public string protocolVersion = "1.0";
    }

    [Serializable]
    public class ResultMessage<TPayload>
    {
        public string id;
        public string type = "result";
        public string name;
        public TPayload payload;
        public long timestamp;
        public string sessionId;
        public string protocolVersion = "1.0";
    }

    [Serializable]
    public class EventMessage<TPayload>
    {
        public string id;
        public string type = "event";
        public string name;
        public TPayload payload;
        public long timestamp;
        public string sessionId;
        public string protocolVersion = "1.0";
    }

    [Serializable]
    public class ErrorDetailPayload
    {
        public string commandId;
        public string commandName;
    }

    [Serializable]
    public class ErrorPayload
    {
        public string code;
        public string message;
        public ErrorDetailPayload detail;
    }

    [Serializable]
    public class ErrorMessage
    {
        public string id;
        public string type = "error";
        public string name;
        public ErrorPayload payload;
        public long timestamp;
        public string sessionId;
        public string protocolVersion = "1.0";
    }

    [Serializable]
    public class DomainReloadPayload
    {
        public string phase; // "starting" or "completed"
        public bool isCompiling;
        public string playModeState;
    }

    [Serializable]
    public class CompileRequestMessage
    {
        public string id;
        public string type;
        public string name;
        public CompileRequestPayload payload;
        public long timestamp;
        public string sessionId;
        public string protocolVersion;
    }

    [Serializable]
    public class CompileRequestPayload
    {
        public string requestId;
    }

    [Serializable]
    public class CompileAcceptedPayload
    {
        public bool accepted;
        public string compileRequestId;
    }

    [Serializable]
    public class CompileStatusPayload
    {
        public string requestId;
        public string status;
        public int errorCount;
        public int warningCount;
        public long startedAt;
        public long finishedAt;
    }

    [Serializable]
    public class CompileErrorItemPayload
    {
        public string file;
        public int line;
        public int column;
        public string message;
        public string severity;
    }

    [Serializable]
    public class CompileErrorsPayload
    {
        public string requestId;
        public int total;
        public List<CompileErrorItemPayload> errors = new();
    }

    /// <summary>MCP-initiated compile lifecycle (explicit compile.started / compile.finished events).</summary>
    [Serializable]
    public class CompileLifecyclePayload
    {
        public string phase;
        public string requestId;
        public string source;
        public long startedAt;
        public long finishedAt;
        public int errorCount;
        public int warningCount;
        public long durationMs;
    }

    /// <summary>Any script compilation via CompilationPipeline (editor UI or MCP).</summary>
    [Serializable]
    public class CompilePipelinePayload
    {
        public string phase;
        public string source;
        public long startedAt;
        public long durationMs;
    }

    [Serializable]
    public class CompileWaitMessage
    {
        public string id;
        public string type;
        public string name;
        public CompileWaitPayload payload;
        public long timestamp;
        public string sessionId;
        public string protocolVersion;
    }

    [Serializable]
    public class CompileWaitPayload
    {
        public int timeoutMs = 300000;
    }

    [Serializable]
    public class EditorDelayMessage
    {
        public string id;
        public string type;
        public string name;
        public EditorDelayPayload payload;
        public long timestamp;
        public string sessionId;
        public string protocolVersion;
    }

    [Serializable]
    public class EditorDelayPayload
    {
        public int delayMs;
    }

    [Serializable]
    public class EditorWindowCloseMessage
    {
        public string id;
        public string type;
        public string name;
        public EditorWindowClosePayload payload;
        public long timestamp;
        public string sessionId;
        public string protocolVersion;
    }

    [Serializable]
    public class EditorWindowClosePayload
    {
        public string windowTitle;
        public string matchMode;
    }

    [Serializable]
    public class EditorWindowCloseResultPayload
    {
        public bool ok;
        public string state;
        public string deniedReason;
        public string matchedTitle;
        public string matchedTypeName;
        public bool multipleMatches;
    }

    [Serializable]
    public class EditorWindowSetRectMessage
    {
        public string id;
        public string type;
        public string name;
        public EditorWindowSetRectPayload payload;
        public long timestamp;
        public string sessionId;
        public string protocolVersion;
    }

    [Serializable]
    public class EditorWindowSetRectPayload
    {
        public string windowTitle;
        public string matchMode;
        public float x;
        public float y;
        public float width;
        public float height;
    }

    [Serializable]
    public class UIToolkitScrollbarDragMessage
    {
        public string id;
        public string type;
        public string name;
        public UIToolkitScrollbarDragPayload payload;
        public long timestamp;
        public string sessionId;
        public string protocolVersion;
    }

    [Serializable]
    public class UIToolkitScrollbarDragPayload
    {
        public string targetWindow;
        public string scrollViewElementName;
        /// <summary>Optional nested path; when set, resolves outer→inner ScrollViews (same format as uitoolkit.scroll).</summary>
        public string scrollViewNamePath;
        public string scrollbarAxis;
        public float normalizedThumbPosition;
        public int dragSteps;
    }

    [Serializable]
    public class UIToolkitScrollbarDragResultPayload
    {
        public bool ok;
        public string state;
        public float scrollOffsetX;
        public float scrollOffsetY;
    }

    [Serializable]
    public class PlayModeSetMessage
    {
        public string id;
        public string type;
        public string name;
        public PlayModeSetPayload payload;
        public long timestamp;
        public string sessionId;
        public string protocolVersion;
    }

    [Serializable]
    public class PlayModeSetPayload
    {
        public string action;
    }

    [Serializable]
    public class PlayModeChangedPayload
    {
        public string state;
    }

    [Serializable]
    public class EditorStatePayload
    {
        public bool connected;
        public bool isCompiling;
        public string playModeState;
        public string activeScene;
    }

    [Serializable]
    public class MouseEventMessage
    {
        public string id;
        public string type;
        public string name;
        public MouseEventPayload payload;
        public long timestamp;
        public string sessionId;
        public string protocolVersion;
    }

    [Serializable]
    public class MouseEventPayload
    {
        public string targetWindow;
        public string action;
        public string button;
        public float x;
        public float y;
        public string[] modifiers;
        public float scrollDeltaX;
        public float scrollDeltaY;
        public string elementName;
        public int elementIndex = -1;
    }

    [Serializable]
    public class GenericOkPayload
    {
        public bool ok;
        public string state;
        public string status;
    }

    [Serializable]
    public class GenericOkEnvelope
    {
        public string id;
        public string type;
        public string name;
        public GenericOkPayload payload;
        public long timestamp;
        public string sessionId;
        public string protocolVersion;
    }

    [Serializable]
    public class KeyboardEventMessage
    {
        public string id;
        public string type;
        public string name;
        public KeyboardEventPayload payload;
        public long timestamp;
        public string sessionId;
        public string protocolVersion;
    }

    [Serializable]
    public class KeyboardEventPayload
    {
        public string targetWindow;
        public string action;    // keydown, keyup, keypress, type
        public string keyCode;   // Unity KeyCode name (e.g. "A", "Return", "Space")
        public char character;   // single character (for keydown with specific char)
        public string text;      // text to type (for "type" action)
        public string[] modifiers; // shift, ctrl/control, alt, cmd/command
    }

    [Serializable]
    public class UIToolkitDumpMessage
    {
        public string id;
        public string type;
        public string name;
        public UIToolkitDumpPayload payload;
        public long timestamp;
        public string sessionId;
        public string protocolVersion;
    }

    [Serializable]
    public class UIToolkitDumpPayload
    {
        public string targetWindow;
        public int maxDepth = 10;
    }

    [Serializable]
    public class UIToolkitQueryMessage
    {
        public string id;
        public string type;
        public string name;
        public UIToolkitQueryPayload payload;
        public long timestamp;
        public string sessionId;
        public string protocolVersion;
    }

    [Serializable]
    public class UIToolkitQueryPayload
    {
        public string targetWindow;
        public string nameFilter;
        public string classFilter;
        public string typeFilter;
        public string textFilter;
    }

    [Serializable]
    public class UIToolkitEventMessage
    {
        public string id;
        public string type;
        public string name;
        public UIToolkitEventPayload payload;
        public long timestamp;
        public string sessionId;
        public string protocolVersion;
    }

    [Serializable]
    public class UIToolkitEventPayload
    {
        public string targetWindow;
        public string eventType;
        public string elementName;
        public int elementIndex = -1;
        public string keyCode;
        public string character;
        public int mouseButton;
        public float mouseX;
        public float mouseY;
        public float wheelDeltaX;
        public float wheelDeltaY;
        public string[] modifiers;
    }

    [Serializable]
    public class UIToolkitElementInfo
    {
        public int index;
        public int parentIndex;
        public int depth;
        public string typeName;
        public string name;
        public string classes;
        public float worldBoundX;
        public float worldBoundY;
        public float worldBoundWidth;
        public float worldBoundHeight;
        public float localBoundX;
        public float localBoundY;
        public bool visible;
        public bool enabled;
        public int childCount;
        public string text;
        public string value;
        public string valueType;
        public bool interactable;
        public bool isFocused;
    }

    [Serializable]
    public class UIToolkitSetValueMessage
    {
        public string id;
        public string type;
        public string name;
        public UIToolkitSetValuePayload payload;
        public long timestamp;
        public string sessionId;
        public string protocolVersion;
    }

    [Serializable]
    public class UIToolkitSetValuePayload
    {
        public string targetWindow;
        public string elementName;
        public int elementIndex = -1;
        public string value;
    }

    [Serializable]
    public class UIToolkitInteractMessage
    {
        public string id;
        public string type;
        public string name;
        public UIToolkitInteractPayload payload;
        public long timestamp;
        public string sessionId;
        public string protocolVersion;
    }

    [Serializable]
    public class UIToolkitInteractPayload
    {
        public string targetWindow;
        public string elementName;
        public int elementIndex = -1;
        public string action; // "click", "focus", "blur"
    }

    [Serializable]
    public class UIToolkitDumpResultPayload
    {
        public bool ok;
        public string targetWindow;
        public int totalElements;
        public List<UIToolkitElementInfo> elements = new();
    }

    [Serializable]
    public class UIToolkitQueryResultPayload
    {
        public bool ok;
        public int matchCount;
        public List<UIToolkitElementInfo> matches = new();
    }

    [Serializable]
    public class UIToolkitScrollMessage
    {
        public string id;
        public string type;
        public string name;
        public UIToolkitScrollPayload payload;
        public long timestamp;
        public string sessionId;
        public string protocolVersion;
    }

    [Serializable]
    public class UIToolkitScrollPayload
    {
        public string targetWindow;
        public string elementName;
        public int elementIndex = -1;
        /// <summary>Nested ScrollView names from outer to inner, separated by | or / (M27 BL-07).</summary>
        public string scrollViewNamePath;
        public float scrollToX = -1;
        public float scrollToY = -1;
        public float deltaX;
        public float deltaY;
        public string mode = "absolute"; // "absolute" or "delta"
    }

    [Serializable]
    public class UIToolkitScrollResultPayload
    {
        public bool ok;
        public string state;
        public float scrollOffsetX;
        public float scrollOffsetY;
    }

    [Serializable]
    public class DragDropMessage
    {
        public string id;
        public string type;
        public string name;
        public DragDropPayload payload;
        public long timestamp;
        public string sessionId;
        public string protocolVersion;
    }

    [Serializable]
    public class DragDropPayload
    {
        public string sourceWindow;
        public string targetWindow;
        public string dragType;       // "asset", "gameobject", "custom"
        public float fromX;
        public float fromY;
        public float toX;
        public float toY;
        public string[] assetPaths;   // for dragType="asset"
        public ulong[] gameObjectIds;   // for dragType="gameobject" (EntityId wire ulong)
        public string customData;     // for dragType="custom"
        public string[] modifiers;
    }

    [Serializable]
    public class DragDropResultPayload
    {
        public bool ok;
        public string state;
        public string dragType;
        public string visualMode;     // DragAndDrop.visualMode.ToString()
    }

}
