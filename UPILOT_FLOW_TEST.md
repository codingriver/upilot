# UPilot Flow Test Guide

Flow 测试仅在 Unity 6、可选依赖完整且 `UPILOT_ENABLE_FLOW` 已启用时运行。

## Default Installation

1. 确认 `UPilot.Flow` 与 `UPilot.FlowBridge` 未加载。
2. 确认 `unity_capabilities_get` 返回 `flow.enabled=false`。
3. 确认 `tools/list` 不包含 `unity_upilot_flow_*`。
4. 确认 UPilot 核心编译、反射、截图、测试和构建工具可用。

## Enabled Installation

1. 明确确认启用 Flow。
2. 检查 Unity 版本、可选包和 `UPILOT_ENABLE_FLOW`。
3. 重启 MCP 客户端。
4. 校验 `Samples~/UPilotFlow/Yaml` 的 148 个文件均包含 `schemaVersion: 2`。
5. 运行解析、动作、循环、超时、取消、截图、报告和 Headed 回归。

## Migration

1. 对旧文件执行 `unity_upilot_flow_migrate(dryRun=true)`。
2. 检查字段变化、无效动作和目标文件。
3. 执行 `dryRun=false`。
4. 再次迁移应返回 `changed=false`。
5. 用 `unity_upilot_flow_validate` 校验输出。

JSON 报告作为验收依据，Markdown 仅用于阅读。
