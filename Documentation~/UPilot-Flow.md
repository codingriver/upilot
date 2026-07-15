# UPilot Flow

UPilot Flow 是 UPilot 包内可选的 YAML EditorWindow 自动化模块，不用于 Game View 或运行时 UI。

## Availability

- 默认关闭。
- 需要 Unity 6+。
- 需要 `UPILOT_ENABLE_FLOW` 和可选包依赖。
- Unity 2022 只使用 UPilot 核心能力，Flow 保持关闭。
- 启用或禁用后需要重启 MCP 客户端刷新工具列表。

## Schema

v0.2 YAML 使用 `schemaVersion: 2`：

```yaml
schemaVersion: 2
name: Login smoke
fixture:
  host_window:
    type: ExampleLoginWindow
    reopen_if_open: true
steps:
  - action: type_text
    selector: "#username"
    value: codex
  - action: click
    selector: "#login-button"
```

缺少 `schemaVersion` 的文件按版本 1 兼容读取。迁移时先运行 `unity_upilot_flow_migrate` 且保持 `dryRun=true`，检查字段变化、无效动作和目标文件，再执行写入。

## Tools

- `unity_upilot_flow_validate`
- `unity_upilot_flow_migrate`
- `unity_upilot_flow_run_file`
- `unity_upilot_flow_run_suite`
- `unity_upilot_flow_run_async`
- `unity_upilot_flow_results`
- `unity_upilot_flow_cancel`
- `unity_upilot_flow_force_reset`

Flow 关闭时这些工具不注册。通过 `unity_capabilities_get` 查看关闭原因和启用条件。

## Timeouts And Reports

- 默认步骤超时：10000ms。
- 步骤、等待、用例和套件超时分别管理。
- 执行状态包含当前文件、用例、步骤、阶段耗时、最后进展和疑似卡住信息。
- JSON 报告是权威数据源，Markdown 和 Headed 界面从 JSON 生成。
- 默认报告目录：`Reports/UPilot/Flow`。

## Actions

`ActionDescriptor` 描述动作名称、参数、目标要求、驱动能力、默认超时和副作用。Schema、动作文档和能力清单应由描述数据生成。

示例位于 `Samples~/UPilotFlow`。升级映射见根目录 `MIGRATION.md`。
