# Agent / Skill 统一生成与同步交付记录

日期：2026-09-20。项目：`D:\upilot\Tests~\UPilotTest`。

实现、当前项目文件同步和定向验收已完成；公共 MCP 新工具已加载并完成真实调用。没有为验收重启、停止或取消其他任务，没有提交或发布。

## 实现

- 唯一版本源 `skills/upilot-unity-mcp/template-manifest.json`：Agent **33**、Skill **34**。UPM 版本保持 **0.3.32**。
- 权威模板 → 仓库确定性生成 → 项目上下文同步 → 备份、暂存、替换、验证和失败回滚。
- Unity 核心：`Editor/Core/UPilotAgentSetup.Integrations.cs`；Bridge：`Editor/Core/UPilotAgentIntegrationService.cs`。Bridge 通过 Unity 主线程队列读取实际安装的 UPM 包并调用核心。
- 新工具 `unity_agent_integrations_check()`、`unity_agent_integrations_sync(apply=false)`；旧 Agent-only 工具仅处理共享 `AGENTS.md`，在线同步不回退到 Server 内置模板。
- Python 入口：`skills/upilot-unity-mcp/scripts/install_upilot.py --integrations-only`；统一引擎为相邻的 `sync_integrations.py`。支持只读预览、JSON 回执及原安装范围。
- 规则只替换唯一受管块，保留块外原始字节和 BOM；Skill 管理两个固定目录。保留 `../../AGENTS.md` 继承，未改造父规则或 `AGENT_Distill.md`。
- 规则哈希记录、Skill v1/v2 兼容、跨进程字节锁、源资源预检、提交前哈希检查、路径联接拒绝和独立目标失败汇总均已接入。

## 当前项目实际状态

上下文：HTTP 8011，UPM 0.3.32，父规则 `../../AGENTS.md`。所有路径相对当前项目。

| 目标 | 管理范围 | 最后检查 |
|---|---|---|
| `AGENTS.md` | UPilot managed block，规则 33 | current |
| `.cursor/rules/upilot-unity-mcp.mdc` | UPilot managed block，规则 33 | current |
| `CLAUDE.md` | `@AGENTS.md` managed block | current |
| `.agents/skills/upilot-unity-mcp/` | 整个目录，Skill 34 | current |
| `.claude/skills/upilot-unity-mcp/` | 整个目录，Skill 34 | current |

最后一次 CLI 正式重复同步返回 `ok=true/status=current/changed=false`；检查的 45 个受管文件内容及修改时间均未变化。随后真实 MCP 检查、预览和正式同步也均返回五个目标 `current`，没有新增写入。暂存目录无残留。两份 installed 校验均通过。

| 哈希范围 | SHA256 |
|---|---|
| 模板集合 | `165108ace2699f10cba6cc92f8a2160f4594c09f2325218171647e1ad273cb88` |
| AGENTS 完整文件 | `4d3673d610db33316eacf068b904fa6fa2f843566892414f3b5a088441df6863` |
| Cursor 完整文件 | `bedf985c0b6e0bb0d3893e2fb1d77e3c4510a315ea85da6aa92dcd8b07096483` |
| CLAUDE 完整文件 | `05da704d1374bba29ff925b8838e34fafee2ccf5f54826aa9ae055c2bbab0a19` |
| 两份 Skill 内容（不含安装元数据） | `767e9d4185f9da21151951a7bb80384c02aa508cc44d8c30872d1861e8146644` |

最后同步没有新增备份，已有两份历史备份原样保留：

- `.upilot/backups/agent-integrations/20260920T081335347630Z-d0729022/`：共享 Skill 的历史备份。
- `.upilot/backups/agent-integrations/20260920T083432093Z-8031cd2a/`：Cursor 规则历史备份。

两份备份的 manifest 均记录 Agent 31 / Skill 32，不能将其误称为本次 33/34 同步新建的备份。真实定制覆盖、备份哈希及失败回滚由临时项目定向测试验证。

## 验证证据

- Python 安装、模板、同步、旧接口和构建资源检查：**113 passed**。
- Python 写入授权直接回归：**3 passed，25 deselected**。
- `render_skill_pack.py --check`、source 校验、两份 installed 校验及 Skill frontmatter 快速校验通过。
- C# / Python 共用 `Tests/Fixtures/agent-integrations-contract.json`，验证 8021 端口生成字节、模板哈希和最终内容哈希。
- 最新 C# 写入批次：`wb-3f369299-be86-47a4-a7f5-480d9847f70c`，创建时间 `1789898521966`。
- Unity 编译请求：`req-4f18091a-bbaf-48da-a713-289f0d5fb69c`，`errorsVerified=true/correlationVerified=true`，验证时间 `1789898535533`，**0 错误、3 警告**。警告均来自本次未改动的 `Editor/Optional/Flow/Schema/UPilot.Flow.Models.cs` 中 Dictionary 的 UAC1009 序列化分析。
- 最新定向 EditMode：**39/39 passed**，包含工作线程进入 Bridge 后切回 Editor 主线程的真实预览回归。总验收 `acceptancePassed=true/cleanupVerified=true/testIdentityVerified=true/sourceUnchanged=true`，任务 `completed/terminal=true`，没有错误。

最终验收身份及原始证据：

- task：`task-838183f2-e02e-410a-955d-bc259cc77d02`
- runGuid：`f756fcff-7426-44c4-abfc-3455464efa5d`
- 文件：`Tests~/UPilotTest/Log/UPilotAcceptance/1789898936739_req-cd5ec27d-d299-4206-b22d-f56b7b7b2f37/summary.json`
- 字节数：`522313`
- SHA256：`4fe22894e41581620369e0639f7f3afcd81792acb7edb4572b53d477a56aba26`
- 源码身份 SHA256：`08447e1de57e5363ed60916ca34d50304ce0f4559d2d97b1572c15a08c7b432d`。

## 运行中断与恢复记录

前一轮测试期间 HTTP 8011 服务退出，Server PID 从 67424 变为 88416；本轮没有发起停止或重启，退出原因未确定。服务恢复后重新核验了 Unity 项目身份，并先恢复原 task/runGuid 的观察，没有重放该任务的 start。

- 原 task `task-35b82bb4-2bc1-4135-9736-145004fda8b2` / runGuid `fa00507d-8041-48d4-af09-858e6f201739`：39/39 测试和清理通过，但恢复后 `sourceUnchanged=false`，总验收失败；未将其当作成功证据。
- 失败报告：`Tests~/UPilotTest/Log/UPilotAcceptance/1789898593373_req-3aff2f2a-43d2-4c35-838f-377c45584cec/summary.json`，`543558` bytes，SHA256 `f58f14355e5c212389ae1e7d8b3fd9366fa6aa04c2ec4afbc9b7577929425f61`。
- 确认失败原因为源码一致性后，针对当前源码重新执行一次相同范围的定向验收，获得上一节记录的最终通过结果。
- 更早一轮恢复成功后残留旧错误字段的诊断问题已记入 `TODO_UPilot.mcd`，未扩展本次实现范围。

没有运行全量 EditMode，也没有使用外部 C# 编译器。EXE 验证范围是构建资源检查及定向测试，没有声称生成了新的 EXE 发布包。

## 公共 MCP 现场核验

- `unity_agent_integrations_check()`：成功，真实来源 `D:\upilot\skills\upilot-unity-mcp`，Agent 33 / Skill 34，五个目标 current。
- `unity_agent_integrations_sync(apply=false)`：成功，dryRun=true，changed=false。
- `unity_agent_integrations_sync(apply=true)`：通过现有项目写入授权，成功，changed=false，五个目标 current。
- `unity_agent_rules_check()`：仅返回项目 `AGENTS.md`，currentRulesVersion=33、recommendedRulesVersion=33、needsUpdate=false。
- 以上为恢复后公共工具的真实调用，不是仅凭注册或单测推断可用。

本次修复前的只读 Bridge 预览操作 `op-7bae985e-83f9-45e0-a453-fd8bfbf307c0` 曾因主线程错误进入 `RecoveryRequired`；其 payload 为 `apply=false`，没有提交同步写入。它及其他未知历史作业未被自动删除、取消或重放，不作为成功证据。当前功能通过修复后的真实公共入口及定向回归独立验证。
