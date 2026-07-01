# upilot

upilot 是一个开源 Unity Editor 自动化桥接包，面向 AI Agent 和 MCP client。
它通过本地 MCP 服务让外部工具检查、控制和诊断 Unity Editor。

UIFlow 是随包提供的可选附属工具，用 YAML 驱动 Unity EditorWindow UI 自动化。主品牌统一为小写 `upilot`。

## 核心功能

- 通过 MCP Streamable HTTP 暴露 Unity Editor 自动化能力。
- Python MCP server 通过 WebSocket 连接 Unity Editor bridge。
- 支持编辑器状态、Console、编译、资源、场景、GameObject、组件、窗口、截图、包、菜单、脚本、Prefab、材质、构建、测试和诊断等操作。
- 可选启用 UIFlow，用 YAML 自动化 UIToolkit 和部分 IMGUI EditorWindow。
- Unity 2022.3+ 默认支持 upilot 核心桥接；UIFlow 在 Unity 6+ 显式启用。

## 版本兼容

| 能力 | 要求 | 说明 |
| --- | --- | --- |
| upilot 核心桥接 | Unity `2022.3` 或更高 | 默认编译。 |
| upilot MCP 服务 | Python `3.11` 或更高 | 随包提供。 |
| UIFlow YAML 自动化 | Unity `6000.0` 或更高 | 需要启用 `UPILOT_ENABLE_UIFLOW`。 |
| 当前验证工程 | Unity `6000.6.0a2` | 见 `Tests~/UnityUIFlowTest`。 |

UIFlow 还需要消费项目安装以下 Unity 包：

- `com.unity.inputsystem`
- `com.unity.ui`
- `com.unity.ui.test-framework`
- `com.unity.test-framework`

在 Unity 2022 中不要启用 `UPILOT_ENABLE_UIFLOW`。此时 UIFlow 相关 MCP 调用会返回 `UIFLOW_UNAVAILABLE`，但 upilot 其他能力仍可用。

## 安装

在 **Window > Package Manager > Add package from git URL** 中添加：

```text
https://github.com/codingriver/upilot.git#v0.1.0
```

也可以直接编辑 `Packages/manifest.json`：

```json
{
  "dependencies": {
    "io.github.codingriver.upilot": "https://github.com/codingriver/upilot.git#v0.1.0"
  }
}
```

## 使用 upilot

打开 Unity 主面板：

```text
upilot/upilot
```

主面板用于启动/停止 MCP 服务、查看连接状态、执行诊断和查看日志。

MCP client 推荐配置：

```json
{
  "servers": {
    "upilot": {
      "type": "http",
      "url": "http://127.0.0.1:8011/mcp"
    }
  }
}
```

常用客户端配置示例。不同工具的配置格式不同，按实际使用的客户端复制对应片段即可。

### Codex CLI

Codex 使用 TOML 配置文件。将下面内容加入 `~/.codex/config.toml`：

```toml
[mcp_servers.upilot]
url = "http://127.0.0.1:8011/mcp"
startup_timeout_sec = 10
tool_timeout_sec = 60
```

### Claude Desktop

Claude Desktop 使用 JSON 配置文件。

配置文件位置：

- macOS：`~/Library/Application Support/Claude/claude_desktop_config.json`
- Windows：`%APPDATA%\Claude\claude_desktop_config.json`

```json
{
  "mcpServers": {
    "upilot": {
      "url": "http://127.0.0.1:8011/mcp"
    }
  }
}
```

### Claude Code

Claude Code 推荐使用命令行添加 MCP server。已通过 upilot 面板或命令行启动 HTTP MCP server 时，使用：

```bash
claude mcp add --transport http upilot --scope project http://127.0.0.1:8011/mcp
```

Claude Code 生成的项目级配置通常会写入 `.mcp.json`，形式类似：

```json
{
  "mcpServers": {
    "upilot": {
      "type": "http",
      "url": "http://127.0.0.1:8011/mcp"
    }
  }
}
```

### Cursor

Cursor 使用 `mcp.json`。项目级配置文件为 `.cursor/mcp.json`；全局配置文件为 `~/.cursor/mcp.json`。

```json
{
  "mcpServers": {
    "upilot": {
      "url": "http://127.0.0.1:8011/mcp"
    }
  }
}
```

使用 HTTP 配置时，需要先确认 upilot MCP server 正在运行，且 `http://127.0.0.1:8011/health` 返回 `status: ok`。

## 运行时代码工具

upilot 提供两类不同的 Unity 侧 C# 调用方式：

- `unity_reflection_call` 调用已经编译并加载到 Unity 程序集中的现有方法。稳定自动化入口，例如 `EnterBattle`、`ExitBattle`、`GetState`，优先使用它。
- `reflection_eval(code, variables = null, options = null)` 执行一条受限 C# 表达式语句，适合调试和自动化验收中的链式访问、索引、方法调用、运算符、赋值、三元、cast/as/is、空条件访问和 typed array 参数。它不支持 lambda/LINQ/async/await/控制流/ref-out-in/任意对象构造。
- `unity_roslyn_execute` 通过 Roslyn 动态编译并执行临时 C# 代码片段，适合一次性诊断和实验。`unity_roslyn_status`、`unity_roslyn_abort` 按 `executionId` 查询或终止动态执行任务。

旧的 `unity_csharp_execute` 工具名不再暴露。稳定业务自动化和已有方法调用请优先使用 `unity_reflection_call`；需要一条表达式级 eval 时使用 `reflection_eval`；只有确实需要动态编译临时代码时再使用 Roslyn 工具。

## MCP 工具清单

当前 `tools/list` 暴露 115 个 MCP 工具，覆盖连接、编译、Console、编辑器输入、场景、GameObject、组件、资源、Prefab、材质、脚本、包、测试、构建、批处理、截图、E2E、UIFlow、反射和 Roslyn 动态执行等能力。维护状态、条件可用性和验收口径见 `Documentation~/ToolStatus.md`。

## UIFlow 附属工具

UIFlow 用 YAML 描述 Unity Editor `EditorWindow` UI 测试流程，不面向 Game View，也不是 PlayMode Runtime UI 测试框架。

功能支持清单：

- UIToolkit 选择器：`#name`、`.class`、类型名、`[data-role=value]` 等。
- 指针动作：点击、双击、悬停、拖拽、滚动、上下文菜单、弹出菜单。
- 键盘与输入：聚焦、按键、文本输入、快速文本赋值。
- 字段与集合：赋值、选项选择、Slider、Tab、List、Tree、Table、SplitView、Breadcrumb。
- 等待与断言：可见、不可见、文本、值、启用状态、属性、等待元素出现。
- 截图、Markdown/JSON 报告和失败截图。
- 通过 `imgui_*` 动作支持部分 IMGUI 工作流。
- Headed Test Runner 可视化调试，以及 MCP 驱动的自动化验证。

启用 UIFlow 需要添加脚本宏：

```text
UPILOT_ENABLE_UIFLOW
```

启用后菜单入口：

```text
upilot/UIFlow/Test Runner
```

最小 YAML 示例：

```yaml
fixture:
  host_window: ExampleBasicLoginWindow
steps:
  - type_text_fast:
      selector: "#username-input"
      text: "admin"
  - type_text_fast:
      selector: "#password-input"
      text: "password"
  - click:
      selector: "#login-button"
  - assert_text:
      selector: "#status-label"
      text: "Login successful"
```

复杂 UIFlow 用法、选择器规则、页面接入规范和自动化边界已经拆分到独立文档：

- `Documentation~/UIFlow.md`

## 文档

- README：`README.md`
- 许可证：`LICENSE.md`
- 第三方组件声明：`NOTICE.md`
- upilot MCP server 开发文档：`upilotserver~/DEVELOPMENT.md`
- MCP 工具状态矩阵：`Documentation~/ToolStatus.md`
- UIFlow 使用指南：`Documentation~/UIFlow.md`

## MonoHook 开源组件与不安全代码

本项目内置了 [MonoHook](https://github.com/Misaka-Mikoto-Tech/MonoHook) 的核心源码拷贝，用于在 Unity Editor 内对托管方法做运行时 Hook。MonoHook 为 MIT License，相关源码位于 `Editor/Plugins/MonoHook/`。

- **不安全代码**：MonoHook 依赖 C# `unsafe` 代码，当前由相关 `.asmdef` 文件的 `allowUnsafeCode` 选项启用，例如 `Editor/Plugins/MonoHook/MonoHook.asmdef` 和使用该能力的编辑器程序集。
- **原生插件**：`Editor/Plugins/MonoHook/Plugins/` 中包含 macOS 用 `libMonoHookUtils_OSX.dylib` 与 `Utils.cpp`，与上游实现保持一致。
- **用法边界**：需要 Hook 能力的 Editor 脚本可以引用 `MonoHook` 命名空间，例如 `MethodHook`；业务代码不应与 MonoHook 强耦合，升级 Unity 小版本后也应重新验证 Hook 目标方法是否仍适用。

`allowUnsafeCode` 开启后，Unity 会允许对应程序集编译指针、非托管内存访问等 `unsafe` 代码，这是 MonoHook 这类底层 Hook 能力正常工作的前提。关闭该选项可以收紧程序集的安全边界，但依赖 `unsafe` 的源码会编译失败，MonoHook 相关的 IMGUI/Editor 方法拦截能力也将不可用。因此，除非确认不再使用这些 Hook 功能，否则不要关闭相关程序集的 `allowUnsafeCode`。

第三方组件声明见 `NOTICE.md`。

## reflection_eval 能力边界

`reflection_eval(code, variables = null, options = null)` 是一个受限的 Unity Editor 反射表达式执行工具。调用侧传入一条标准 C# 表达式语句即可，例如：

```csharp
IGG.Game.Module.KingShotBattle.KingShotBattleModule.Inst.RequestEnterLevel(10001u, false, new uint[1]{10001u});
```

支持内容：

- 一条表达式语句，可带末尾分号。
- 静态类型路径、已有对象访问、链式成员访问、索引器、数组/List/Dictionary 索引和方法调用。
- `variables` JSON 注入的只读参数，包括带 `{ "type": "...", "value": ... }` 的 typed value。
- 字面量：`null`、`true/false`、字符串/字符、整数/浮点数和常见数字后缀。
- typed array：`new uint[]{1,2}`、`new uint[2]{1,2}`、`uint[]{1,2}`。
- 白名单值类型构造：`Vector2`、`Vector3`、`Vector4`、`Quaternion`。
- 运算符：一元 `!`、`+`、`-`、`~`；二元 `*`、`/`、`%`、`+`、`-`、`<<`、`>>`、`<`、`<=`、`>`、`>=`、`==`、`!=`、`&`、`^`、`|`、`&&`、`||`。
- 三元 `?:`、括号、cast、`is`、`as`、空条件访问 `?.`。
- 对反射成员或索引器赋值：`=`、`+=`、`-=`。
- `options`：`resultMode`、`timeoutMs`、`maxTokens`、`maxCallDepth`、`maxResultItems`、`allowNamespacePrefixes`、`denyMethods`、`allowNonPublic`、`trace`。

不支持内容：

- 完整 C# 语句块，只支持单条表达式语句。
- `if`、`for`、`foreach`、`while`、`do`、`switch` 等控制流。
- lambda 表达式、LINQ 查询语法、`async/await`、直接 delegate 调用。
- `ref`、`out`、`in` 参数。
- 任意对象构造、创建不存在的类、定义新类型或动态编译代码。
- `using`、`namespace`、方法定义、本地函数、局部变量声明。
- 将修改写回 `variables` JSON；`variables` 只作为表达式输入。
