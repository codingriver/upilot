# UPilot MCP Server 开发文档

本文档面向维护 `upilot` 随包 Python MCP server 的开发者。普通用户安装与基础使用请先阅读根目录 `../README.md`。

## 定位

`upilot` 是 Unity package 与公开产品名。本目录中的 Python 代码提供 Unity package 使用的 MCP server。

Python server 负责：

- 通过 stdio 或 Streamable HTTP 暴露 MCP tools；
- 通过 WebSocket 接收 Unity Editor bridge 连接；
- 转发编辑器状态、Console 日志、编译、资源、场景、GameObject、组件、窗口、截图、包、菜单、脚本、Prefab、材质、构建、测试和诊断等工具调用；
- 当 Unity 侧显式启用 UPilot Flow 时，转发 YAML EditorWindow 自动化调用。

## 目录结构

```text
upilotserver~/
  run_upilot_mcp.py          # 推荐的源码启动入口。
  pyproject.toml             # Python 包元数据和 console scripts。
  requirements.txt           # 源码运行所需依赖。
  mcp.example.json           # 本地 MCP client 配置示例。
  src/upilot_mcp/        # Server Python 包。
  tests/                     # Pytest 测试。
  scripts/                   # 验收与辅助脚本。
  deploy/                    # 发布/部署辅助脚本。
  e2e-specs/                 # 示例 UI/editor E2E spec。
```

## 环境要求

- Python `3.11` 或更高版本。
- Unity package `io.github.codingriver.upilot`。
- Unity Editor 以普通编辑器模式打开。
- Unity Editor bridge 配置为连接到 Python server 相同的 WebSocket host 和 port。

## 架构

Server 有两层传输：

- **MCP transport**：通过 stdio 或 Streamable HTTP 暴露给 MCP client。
- **Unity bridge transport**：供 Unity Editor bridge 连接的内部 WebSocket listener。

常见本地开发拓扑：

```text
MCP client
  -> stdio 或 http://127.0.0.1:8011/mcp
  -> Python MCP server
  -> ws://127.0.0.1:8765
  -> Unity Editor bridge
```

HTTP MCP endpoint 必须使用 `/mcp` 路径，例如：

```text
http://127.0.0.1:8011/mcp
```

WebSocket 端口 `8765` 不是 MCP client endpoint，它是 Python server 与 Unity Editor bridge 之间的内部连接端口。

## 源码环境

在本目录下安装运行依赖：

```bash
python -m pip install -r requirements.txt
```

开发模式安装：

```bash
python -m pip install -e .[dev]
```

开发模式安装后会暴露这些 console scripts：

- `upilot`
- `upilot-mcp`

## 从源码运行

从仓库根目录启动：

```bash
python -m pip install -r upilotserver~/requirements.txt
python upilotserver~/run_upilot_mcp.py --transport http --http-port 8011 --port 8765
```

从 `upilotserver~/` 目录启动：

推荐的 Streamable HTTP 启动方式：

```bash
python run_upilot_mcp.py --transport http --http-port 8011 --port 8765
```

MCP client URL：

```text
http://127.0.0.1:8011/mcp
```

面向 local-command MCP client 的 stdio 启动方式：

```bash
python run_upilot_mcp.py --transport stdio --port 8765
```

开发模式安装后的 console script 启动方式：

```bash
upilot-mcp --transport http --http-port 8011 --port 8765
```

## 参数

| CLI 参数 | 环境变量 | 默认值 | 作用 |
| --- | --- | --- | --- |
| `--transport` | `UPILOT_TRANSPORT` | `stdio` | MCP 传输方式：`stdio` 或 `http`。 |
| `--host` | `UPILOT_HOST` | `127.0.0.1` | Unity Editor bridge 使用的 WebSocket host。 |
| `--port` | `UPILOT_PORT` | `8765` | Unity Editor bridge 使用的 WebSocket port。 |
| `--http-host` | `UPILOT_HTTP_HOST` | `127.0.0.1` | Streamable HTTP host。 |
| `--http-port` | `UPILOT_HTTP_PORT` | `8000` | Streamable HTTP port。 |
| `--label` | 无 | 当前目录名 | Unity 诊断中显示的标签。 |
| `--log-file` | `UPILOT_LOG_FILE` | 未设置 | 可选日志文件路径。 |
| `--log-level` | `UPILOT_LOG_LEVEL` | `DEBUG` | Python 日志级别。 |

如果同时运行多个 Unity 项目，给每个项目使用不同的 `--port`，例如 `8765`、`8766`、`8767`。

## MCP Client 配置

对于支持 Streamable HTTP 的 client，先自行启动 server，再配置 HTTP URL：

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

对于 local-command client，使用 stdio：

```json
{
  "mcpServers": {
    "upilot": {
      "command": "python",
      "args": [
        "<PATH_TO_UPILOT_REPO>/upilotserver~/run_upilot_mcp.py",
        "--transport",
        "stdio",
        "--port",
        "8765"
      ],
      "env": {
        "PYTHONUTF8": "1"
      }
    }
  }
}
```

更完整的 local-command 示例见 `mcp.example.json`。

## Unity Editor 连接

Python server 只是桥接的一侧，Unity Editor 必须同时打开并连接到同一个 WebSocket endpoint。

基本检查项：

- Python 进程正在监听配置的 WebSocket host 和 port。
- Unity Editor package 已安装并启用 bridge。
- Unity Editor 没有运行在 batchmode。
- MCP tool `unity_mcp_status` 返回 `connected: true`。

如果 `unity_mcp_status` 可调用，但显示 Unity 未连接，说明 MCP server 已运行，但 Unity Editor bridge 尚未连接。

## 测试

在本目录运行 Python 测试：

```bash
python -m pytest
```

部分测试会启动本地 server 进程并绑定端口。如果测试因为端口占用失败，停止旧进程，或在测试支持时改用空闲端口后重试。

## 开发约定

- 保持 `run_upilot_mcp.py` 作为推荐源码启动入口。
- MCP HTTP 路径保持为 `/mcp`。
- WebSocket `8765` 是内部 Unity bridge 端口，不是面向 MCP client 的 endpoint。
- Python import module 为 `upilot_mcp`，对外发布名和命令为 `upilot-mcp`。
- MCP 工具命名需要与实现路径保持一致。Roslyn 动态编译工具不再暴露，也不注册 Unity bridge `roslyn.*` 路由。
- 稳定调用已有业务方法时应使用 `unity_reflection_call`；需要一条表达式级 eval 时使用 `reflection_eval`，对应 Unity bridge 路由 `reflection.eval`。

## 排障

### HTTP 根路径可访问，但 MCP 调用失败

必须使用完整 MCP endpoint：

```text
http://127.0.0.1:8011/mcp
```

根路径只适合作为人工查看的 landing page 或健康提示。

### Unity 未连接

确认 Unity Editor 已打开、package 已安装，并且 bridge 连接的 WebSocket port 与 server 启动时传入的 `--port` 一致。

### 端口冲突

修改 WebSocket port，并确保 Unity 使用同一个值：

```bash
python run_upilot_mcp.py --transport http --http-port 8011 --port 8766
```

如果同时运行多个 HTTP server，也要修改 `--http-port`。

### 非 ASCII 路径或文本乱码

local-command client 可以设置 Python UTF-8 模式：

```json
{
  "env": {
    "PYTHONUTF8": "1"
  }
}
```

## 相关文档

- 根目录 README：`../README.md`
- MCP 工具状态矩阵：`../Documentation~/ToolStatus.md`
- UPilot Flow 使用指南：`../Documentation~/UPilot-Flow.md`
