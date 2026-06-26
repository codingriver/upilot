# 更新日志

本文记录此包的所有重要变更。

本文档对应英文版 `CHANGELOG.md`。格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，并遵循 [Semantic Versioning](https://semver.org/lang/zh-CN/spec/v2.0.0.html)。

## [0.1.0] - 2026-05-07

### 新增

- 初始 UPM 包发布。
- 面向 Unity Editor UIToolkit 与 IMGUI 的 YAML 驱动自动化框架。
- 38 个内置动作，包括点击、输入、拖拽、断言、截图等。
- 用于遍历 `VisualElement` 的 CSS-like 选择器引擎。
- 支持单步调试的 Headed Test Runner 窗口。
- 面向 CI 执行的 CLI 入口：`UnityUIFlowCliEntry.RunAllFromCommandLine`。
- 支持 Markdown/JSON 报告，以及失败时自动截图。
- 内置 `MonoHook`（MIT），用于编辑器方法拦截。
- 内置 `YamlDotNet`，用于 YAML 解析。
- 示例窗口、UXML 布局、USS 样式表和 YAML 测试用例。
