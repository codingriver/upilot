# UPilot

UPilot 是面向 AI Agent 的 Unity Editor MCP 自动化核心，提供连接、诊断、编译、Console、场景、对象、组件、资源、Prefab、包、测试、构建、截图、反射调用和长任务追踪能力。

UPilot Flow 是包内可选的 YAML EditorWindow 自动化模块。它默认关闭，不影响核心能力。

## Requirements

- Unity 2022.3 或更高：支持全部 UPilot 核心能力。
- Python 3.11 或更高：运行 MCP 服务。
- UPilot Flow：仅支持 Unity 6+，并要求可选依赖和 `UPILOT_ENABLE_FLOW`。

## Install

在 Unity `Packages/manifest.json` 中添加：

```json
{
  "dependencies": {
    "io.github.codingriver.upilot": "https://github.com/codingriver/upilot.git#v0.2.0"
  }
}
```

Python 服务位于 `upilotserver~`：

```powershell
cd upilotserver~
python -m pip install -e .
python run_upilot_mcp.py --transport http --http-port 8011
```

MCP 客户端统一连接：

```text
http://127.0.0.1:8011/mcp
```

内部 WebSocket 端口只用于 Python 服务与 Unity Bridge 通信，不得配置为 MCP 客户端地址。

## Project Config

项目根目录 `.upilot/config.json`：

```json
{
  "schemaVersion": 2,
  "mcp": {
    "httpHost": "127.0.0.1",
    "httpPort": 8011,
    "wsHost": "127.0.0.1",
    "wsPort": 8765
  },
  "cache": {
    "contextStaleMs": 2000
  },
  "features": {
    "flow": {
      "enabled": false
    }
  }
}
```

配置优先级为：工具调用参数 > CLI/环境变量 > 项目配置 > 内置默认值。

## Agent Workflow

1. 调用 `unity_mcp_status` 并校验 `connected`、`serverReady` 和项目路径。
2. 工具是否存在不明确时调用 `unity_capabilities_get` 或 `unity_tools_find`。
3. 修改 Editor 前调用 `unity_ensure_ready`。
4. 一批磁盘写入后只调用一次 `unity_sync_after_disk_write`。
5. 仅在 C# 或程序集相关内容变化后编译。
6. 长任务通过 `unity_task_*` 与 `unity_operation_*` 查询阶段、耗时和卡住状态。

服务端已注册、客户端已注入、工具实际调用成功是三个不同状态。工具列表或可选功能变化后需要刷新 MCP 客户端。

## Core APIs

- `unity_capabilities_get`
- `unity_tools_find`
- `unity_operation_list` / `unity_operation_get`
- `unity_task_start` / `unity_task_status` / `unity_task_cancel`
- `unity_reflection_call`
- `reflection_eval`，仅作为一次有边界的降级表达式

所有工具使用 schema v2 响应，包含结构化错误、上下文新鲜度和分层耗时。

## UPilot Flow

默认安装不会加载 `UPilot.Flow`，不会注册 `unity_upilot_flow_*`，也不要求 Flow 依赖。

在 Unity 6 项目中启用 Flow 前必须由用户明确确认。启用后需要安装可选包、添加 `UPILOT_ENABLE_FLOW`，并重启 MCP 客户端刷新工具列表。

Flow YAML 使用：

```yaml
schemaVersion: 2
name: Example
steps:
  - action: wait
    duration: 100ms
```

先用 `unity_upilot_flow_validate` 校验；迁移旧文件时先调用 `unity_upilot_flow_migrate(dryRun=true)`。

详细说明见 `Documentation~/UPilot-Flow.md`，升级映射见 `MIGRATION.md`。

## Development

```powershell
cd upilotserver~
python -m compileall -q src
python -m pytest -q
python ..\skills\upilot-unity-mcp\scripts\check_skill_pack.py
```

版本变化见 `CHANGELOG.md`。
