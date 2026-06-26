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
| upilot MCP 服务 | Python `3.11` 或更高 | 当前仍放在 `unitypilot~/` 目录内以兼容旧布局。 |
| UIFlow YAML 自动化 | Unity `6000.0` 或更高 | 需要启用 `UNITYPILOT_ENABLE_UIFLOW`。 |
| 当前验证工程 | Unity `6000.6.0a2` | 见 `Tests~/UnityUIFlowTest`。 |

UIFlow 还需要消费项目安装以下 Unity 包：

- `com.unity.inputsystem`
- `com.unity.ui`
- `com.unity.ui.test-framework`
- `com.unity.test-framework`

在 Unity 2022 中不要启用 `UNITYPILOT_ENABLE_UIFLOW`。此时 UIFlow 相关 MCP 调用会返回 `UIFLOW_UNAVAILABLE`，但 upilot 其他能力仍可用。

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

本仓库本地开发时，也可以直接启动 Python MCP server：

```bash
python -m pip install -r unitypilot~/requirements.txt
python unitypilot~/run_upilot_mcp.py --transport http --http-port 8011 --port 8765
```

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

旧入口仍保留用于兼容：

```bash
python unitypilot~/run_unitypilot_mcp.py --transport http --http-port 8011 --port 8765
```

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
- Headed Test Runner 可视化调试，以及 MCP 驱动的 Agent 验证。

启用 UIFlow 需要添加脚本宏：

```text
UNITYPILOT_ENABLE_UIFLOW
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

复杂 UIFlow 用法、动作参数、选择器规则、页面接入规范和自动化边界已经拆分到独立文档：

- `Documentation~/UIFlow.md`
- `Documentation~/UIFlow.zh-CN.md`
- `Documentation~/00-API速查与最佳实践.md`
- `Documentation~/00-UIToolkit控件自动化覆盖与限制说明.md`
- `Documentation~/00-IMGUI控件自动化覆盖与限制说明.md`

## 兼容策略

这次迁移会把公开品牌、包名、菜单、面板和 MCP server identity 统一为 `upilot`。
为了避免破坏已有脚本，内部 C# namespace、assembly 名称、Python module 名称，以及旧的 `UnityPilot` / `UnityUIFlow` / `unityuiflow` 工具别名会暂时保留。

## 文档

- 英文 README：`README.md`
- 更新日志：`CHANGELOG.md`
- 中文更新日志：`CHANGELOG.zh-CN.md`
- 许可证：`LICENSE.md`
- 第三方组件声明：`NOTICE.md`
- upilot MCP server 开发文档：`unitypilot~/DEVELOPMENT.md`
- upilot MCP server 中文开发文档：`unitypilot~/DEVELOPMENT.zh-CN.md`
- UIFlow 使用指南：`Documentation~/UIFlow.zh-CN.md`
- UIFlow 英文指南：`Documentation~/UIFlow.md`
- Agent/MCP 执行规则：`Documentation~/03-UnityUIFlow-Agent-MCP测试强制规范.md`

## MonoHook 开源组件与不安全代码

本项目内置了 [MonoHook](https://github.com/Misaka-Mikoto-Tech/MonoHook) 的核心源码拷贝，用于在 Unity Editor 内对托管方法做运行时 Hook。MonoHook 为 MIT License，相关源码位于 `Editor/Plugins/MonoHook/`。

- **不安全代码**：MonoHook 依赖 C# `unsafe` 代码，当前由相关 `.asmdef` 文件的 `allowUnsafeCode` 选项启用，例如 `Editor/Plugins/MonoHook/MonoHook.asmdef` 和使用该能力的编辑器程序集。
- **原生插件**：`Editor/Plugins/MonoHook/Plugins/` 中包含 macOS 用 `libMonoHookUtils_OSX.dylib` 与 `Utils.cpp`，与上游实现保持一致。
- **用法边界**：需要 Hook 能力的 Editor 脚本可以引用 `MonoHook` 命名空间，例如 `MethodHook`；业务代码不应与 MonoHook 强耦合，升级 Unity 小版本后也应重新验证 Hook 目标方法是否仍适用。

`allowUnsafeCode` 开启后，Unity 会允许对应程序集编译指针、非托管内存访问等 `unsafe` 代码，这是 MonoHook 这类底层 Hook 能力正常工作的前提。关闭该选项可以收紧程序集的安全边界，但依赖 `unsafe` 的源码会编译失败，MonoHook 相关的 IMGUI/Editor 方法拦截能力也将不可用。因此，除非确认不再使用这些 Hook 功能，否则不要关闭相关程序集的 `allowUnsafeCode`。

第三方组件声明见 `NOTICE.md`。
