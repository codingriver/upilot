# UPilot 包标准测试与 AI 集成优化项目

## 项目定位

本项目不是普通 Unity 业务项目，而是 UPilot 包在仓库内的标准测试、集成和验收项目。它用于在真实 Unity Editor 环境中验证 `io.github.codingriver.upilot` 的功能、MCP 工具、安装流程、Agent 规则和配套 Skills 是否正确、稳定、易于使用。

本项目的最终目标不是单纯让测试变绿，而是持续把测试中发现的问题沉淀回 UPilot 产品，使各类 AI Agent 能用更少的上下文、更少的工具调用、更短的等待时间和更明确的恢复路径，简单、安全、高效、快速地完成 Unity 项目的检查、修改、测试、构建和证据收集。

## 首要目标与优先级

以下三项共同属于 P0，不能只完成其中一项：

1. 验证并提高 UPilot 包的功能正确性、兼容性、可诊断性和长期运行稳定性。
2. 持续补充和优化 UPilot 的 Agent 规则模板，使生成到不同 Unity 项目的规则准确、简洁、可执行，并帮助 AI 简单、安全、高效、快速地选择正确工具，处理写入、编译、长任务、卡死恢复和验收证据。
3. 持续补充和更新 UPilot 配套 Skills，使 Codex、Claude、Cursor 等 AI 工具能更方便地发现、安装和使用 UPilot，减少手写 MCP 调用、重复轮询、错误降级、不必要的等待和 Unity 重启。

P1 是完善测试覆盖、示例、文档、诊断报告和截图证据。P1 应服务于上述 P0 目标，而不是用测试项目内的临时绕过掩盖包、规则模板或 Skill 的问题。

## Agent 规则模板与 Skills 的共同设计目标

UPilot Agent 规则模板和配套 Skills 都是 UPilot 产品体验的一部分，其共同目标是让不同厂商、不同能力层级的 AI 工具都能简单、安全、高效、快速地使用 UPilot：

- **简单**：用户只需表达 Unity 目标，不应被要求理解 Bridge 端口、内部实现或冗长调用链。规则和 Skills 应提供明确入口、合理默认值、最短可行流程、常见示例和可直接执行的 `nextAction`。
- **安全**：执行前确认工程身份、目标范围和写入权限；破坏性操作使用预览、精确目标和可恢复策略；长任务具备取消、停止和清理能力。不得为了速度绕过安全边界、结果校验或用户明确授权。
- **高效**：优先最窄的语义工具、高层工作流、批量操作和增量结果；避免反复获取完整工具列表、完整日志、完整报告或重复执行已经通过的测试。规则应减少无效决策，Skills 应减少无效工具调用。
- **快速**：能附着现有编译就不重新编译，能停止业务就不重启 Unity，能增量轮询就不阻塞等待，能使用项目现有编排入口就不临时重建流程。重试必须有边界，并在相同失败特征重复时立即转向诊断根因。

规则模板与 Skills 应分工清晰：规则模板保存跨项目长期稳定的目标、边界、优先级和完成标准；Skills 保存工具发现、参数选择、具体工作流、恢复步骤和按需引用资料。不要在规则模板中复制完整工具手册，也不要让 Skills 重复堆叠规则正文，以免增加上下文和降低执行速度。

优化规则模板或 Skills 时，应优先衡量是否减少了 AI 的决策次数、工具调用次数、上下文消耗、等待时间和故障恢复成本，同时确认没有降低安全性、正确性、可诊断性和验收证据质量。

## 问题归属与修改原则

- UPilot 运行时、Editor、MCP Server、Flow、安装或工具实现的问题，应优先修复 UPilot 包源代码，并增加最小回归测试。
- 如果问题来自 Agent 不知道该调用什么、调用顺序不稳定、容易误判完成、容易重复测试或错误重启 Unity，应同步评估并更新 Agent 规则模板。
- 如果问题来自 AI 难以发现工具、参数说明不足、常见工作流步骤过多、恢复路径不清晰或安装副本不同步，应同步评估并更新 UPilot Skill 主源、引用文档或辅助脚本。
- 只有在权威产品行为已经改变、而测试预期过时时，才允许只更新测试断言。不得为了通过测试降低正确性、安全性或验收标准。
- 能推广到其他 Unity 项目的经验，不应只写进本项目的局部规则或测试代码；应沉淀到 UPilot 包的规则模板或 Skills。
- 尚不能在当前任务中完成的产品、规则或 Skill 改进，必须记录到仓库根目录 `TODO_UPilot.mcd`，包含现象、影响、建议、证据和状态。

## 规则模板与 Skill 主源

- Agent 规则模板主源：`../../skills/upilot-unity-mcp/AGENTS.md.template`。
- 规则生成与安装逻辑：`../../Editor/Core/UPilotAgentSetup.cs`。
- UPilot Skill 主源：`../../skills/upilot-unity-mcp/`。
- 仓库级 Skill 发现入口：`../../.agents/skills/upilot-unity-mcp/`。
- 本项目 `.agents/skills/upilot-unity-mcp/` 是安装和兼容性验证副本，不是长期维护的唯一主源。
- 本文件末尾由 UPilot 起止标记包围的区域是自动管理块，不应手工维护其生成内容。需要改变通用规则时，应修改模板/生成器、更新对应模板版本、增加测试，然后通过 UPilot 重新生成并验证管理块。
- 当规则模板行为发生变化时，应评估提升 `AgentRulesTemplateVersion`；当 Skill 安装内容或更新策略发生变化时，应评估提升 `SkillInstallTemplateVersion`。
- 修改 Skill 后，应运行其校验脚本，并验证主源、仓库发现入口和项目安装副本之间不存在意外漂移。

## 标准工作方式

1. 先确认连接的是本项目，再进行任何 Unity 验收或修改。
2. 先复现并定位问题属于包实现、测试、规则模板、Skill 还是项目集成，不盲目增加重试或重启 Unity。
3. 优先修复可复用的根因，并补充针对性测试；已通过的测试不重复运行。
4. C# 或程序集相关修改完成后执行一次安全编译，处理结构化编译错误后再继续。
5. 针对性测试全部通过后，默认停止；只有用户明确要求全量、完整回归、整套验收或语义等价的 whole-suite 验证时，才运行一次完整 EditMode 回归。
6. 长任务必须轮询到终态；取消、停止和清理必须以业务真正结束、资源释放完成为准，不能把“已发送取消请求”当作完成。
7. Unity 疑似卡死时先采集状态和诊断证据，优先通过 UPilot 或业务停止能力恢复；重启 Unity 只能作为有证据、有说明的最后手段。
8. 最终报告应区分编译结果、测试结果、条件跳过、已知限制、产物路径、截图来源、哈希和未完成项。

## 完成标准

一次 UPilot 改动只有同时满足以下条件才可以报告完成：

- 根因已在正确的主源中修复，没有只依赖测试项目临时绕过。
- 相关针对性测试通过；完整 EditMode 回归仅在用户明确要求时作为附加验收，且不得重复运行已经通过的用例。
- 编译错误为零；与任务相关的 Console 错误、活动操作、活动采集和未清理资源已处理或明确说明。
- 测试中暴露的通用 Agent 使用问题已评估是否需要更新规则模板和 Skills；需要更新时应在同一任务中完成，或写入 `TODO_UPilot.mcd`。
- 规则模板或 Skill 有变化时，生成、安装、版本、同步和校验流程已经验证。
- 规则模板或 Skill 有变化时，至少验证工具发现、连接检查、写入授权、编译/测试、长任务停止或故障恢复中的相关典型路径，确认流程比修改前更简单、更安全、更高效或更快速。
- 新增规则不得无意义扩大上下文；新增 Skill 内容应按需加载，并优先复用语义工具、高层工作流和结构化 `nextAction`。
- 验收报告能说明这次改动如何提高 UPilot 的稳定性，以及如何让 AI 更方便、更简单地使用 UPilot。

<!-- upilot:start -->
# UPilot Unity MCP

rulesVersion: 13
upilotPackageVersion: 0.3.27
projectPath: D:\upilot\Tests~\UPilotTest
generatedAt: 2026-08-27T06:44:41Z

This Unity project has the `io.github.codingriver.upilot` UPM package installed.
Project-specific business rules outside this controlled UPilot block take precedence.

## Parent Agent Rules

- Parent Agent rules path: `../../AGENTS.md` (relative to the project root `AGENTS.md`).
- Before applying this UPilot block, automatically load the parent rules when the path is not `(none)`.
- Resolve every loaded `AGENTS.md` to a canonical absolute path and keep a visited set, including this file, so repeated or circular references are skipped.
- Apply inherited parent rules first, then this project's local rules, then this UPilot block.

## UPilot Package Acceptance

- When validating UPilot repository/package changes, use `./Tests~/UPilotTest` under the UPilot repository as the default and canonical Unity project for compile and EditMode acceptance.
- Do not treat external client projects such as `D:\MA\xclient` or `F:\xclient2` as default UPilot validation targets.
- Use external client projects only when explicitly requested for project-side/business smoke validation or investigation.
- If this rule block is installed inside another Unity project, use that project for its own business workflows, but return to `./Tests~/UPilotTest` before claiming UPilot package compile/EditMode acceptance.

## Connection

- Streamable HTTP: `http://127.0.0.1:8011/mcp`
- Health check: `http://127.0.0.1:8011/health`
- Third-party AI tools must connect through Streamable HTTP at `http://127.0.0.1:<httpPort>/mcp` only.
- Never configure a third-party AI client with a WebSocket URL, the internal Unity Bridge port, a stdio command, or a local MCP Server process command.
- WebSocket transport is internal to MCP Server <-> Unity Bridge. The default internal port is 8765 and must not appear as the AI client's endpoint.
- When multiple Unity projects run concurrently, allocate a unique HTTP/WebSocket port pair per project internally, give each client registration a distinct name, and expose only that project's HTTP `/mcp` endpoint to the AI tool.
1. Call `unity_mcp_status`.
2. Require `connected: true` and `serverReady: true`.
3. Verify `paths.unityProjectAbsolute` matches `D:\upilot\Tests~\UPilotTest` (allow equivalent slash normalization).
4. Stop and report the mismatch if another Unity project is connected.

## Capabilities

- Distinguish server registration, client tool-list injection, and a successful real call; they are different states.
- If a native tool is not visible in the client, call `unity_capabilities_get` or `unity_tools_find` before declaring it unavailable. Treat `registered`, `available`, and `callableNow` as separate states and follow `unavailableReason` / `nextAction`.
- After enabling an optional feature or changing tool registration, restart or refresh the MCP client tool list.
- Use the narrowest dedicated semantic tool. Use `unity_reflection_call` for existing compiled entry points.
- Only after `unity_reflection_call` actually fails may you fall back to one bounded `reflection_eval` expression.
- For Unity Editor operations, prefer an available UPilot semantic tool. Fall back to local scripts, menu execution, reflection evaluation, or UI automation only after targeted capability discovery confirms the dedicated tool is unavailable or an actual call fails. Report the fallback reason.
- Do not repeatedly fetch the full tool list. Use `unity_tools_find` for targeted discovery.

## Writes And Compile

- Call `unity_ensure_ready` before Editor mutations and inspect the exact target before destructive changes.
- Decide Editor readiness from `ready`, `blocked`, `blockedReason`, `authoritative`, `isStale`, and `nextAction`; follow `nextAction` while blocked or recovering.
- Do not infer readiness from raw `isPlaying` or `isCompiling` values alone. Compilation phases `queued`, `compiling`, `domain_reload`, and `verifying` are non-ready even when `isCompiling=false`.
- After one batch of disk writes, call `unity_sync_after_disk_write` once.
- After C# or assembly-related changes, prefer one `unity_safe_compile_and_wait` call. It attaches to an existing compile and verifies persistent errors after Domain Reload.
- If `unity_sync_after_disk_write(triggerCompile=true)` returns `ok=true/status=compiling`, do not retry sync; call `unity_safe_compile_and_wait` and follow its `stage`, `nextAction`, and structured error result.
- Use `unity_compile_wait` plus `unity_compile_errors` only when observing a compile that must not be triggered or attached through the safe workflow; `unity_compile_errors_get` is a compatibility alias.
- Compile only after C# or assembly-related changes. Do not compile again when no code changed.
- After compilation, read structured compile errors and relevant Console errors before editing again.

## Optional MonoHook Tracing

- MonoHook Tracing is optional and manually controlled. All hook points, per-point stack capture, and Console output default to disabled.
- Do not enable, apply, auto-restore, or consume tracing events unless explicitly requested. Query status first and preserve the existing configuration.
- `unity_monohook_tracing_configure` saves without applying by default; use `apply=true` only when hook installation or application is explicitly required.
- Respect `Unsupported` diagnostics. Do not use Native, InternalCall, injected, reflection-eval, or other lower-level hook fallbacks.
- Keep high-frequency tracing, stack capture, and Console output bounded, then restore the original configuration after temporary diagnostics.

## Long Operations

- Before starting a hand-authored generic operation, call `unity_operation_validate(jobSpec)` and fix its structured field errors; validation must not start PlayMode or business work.
- For tests, builds, smoke runs, and other long workflows, prefer `unity_operation_start`, `unity_operation_status`, `unity_operation_wait`, `unity_operation_cancel`, and `unity_operation_collect_artifacts`.
- Starting an operation is not success; poll until a terminal status.
- Treat `waitWindowElapsed=true/terminal=false` as a running operation, not a timeout failure. Only the operation's `jobTimeoutAt` is its terminal timeout.
- Report only meaningful changes: status, phase, error, `failureSignature`, suspected-stuck, or important artifacts.
- Use `detailLevel=summary` for routine status/wait polling. Request `standard` or `full` only for bounded diagnosis, and set `maxTailChars` instead of returning unbounded domain/log/report text.
- Use project-provided bridge entry points when they exist. Do not rebuild business workflows with shell commands, temporary scripts, menu calls, or UI automation.
- Keep business orchestration in project code. UPilot should start, poll, diagnose, capture logs, and collect artifacts.

## Operation Status Contract

- Project bridge status JSON should use generic fields where possible: `ok`, `operationId`, `status`, `phase`, `error`, `detail`, `elapsedSec`, `phaseElapsedSec`, `progress`, `failureSignature`, `artifacts`, `metrics`, and `domain`.
- UPilot parses only generic fields. Business fields belong in `domain` and are passed through unchanged.

## Persistent Console Capture

- For long-running or audit-sensitive operations, call `unity_console_capture_start` before the operation, keep its `sessionId`, and always call `unity_console_capture_stop` on success or failure.
- Never repeatedly scan a complete large capture. Pass each `unity_console_capture_read` result's `nextSequence` as the next call's `afterSequence`.
- Before concluding cleanup, call `unity_console_capture_list`, inspect recovered or historical sessions still marked active, and stop the relevant session explicitly.
- Keep raw Console capture separate from domain-specific reports. Prefer project-relative output paths and do not allow paths outside the project unless the user explicitly requests one.
- Console capture cleanup must use dry-run, target inspection, and confirm-token execution.
- For canonical UPilot package acceptance, prefer `unity_upilot_acceptance_run`. It stops active captures before ConsoleCaptureService self-tests and does not start a persistent capture around that test run.

## Configuration CSV Safety

- Read targeted records with `unity_config_csv_get`; do not infer the file encoding, newline style, delimiter, header row, column count, or key uniqueness.
- Every `unity_config_csv_patch` write must follow `dryRun=true` -> inspect preview and hashes -> explicit write approval -> `dryRun=false` with the returned `confirmToken`.
- Supply `expectedValues` for fields whose current values are known. After apply, verify the target values, encoding, newline style, column count, and unchanged non-target bytes reported by the tool.

## Hang Diagnostics

- When Unity stops pumping commands or appears stuck, call `unity_hang_status` before retrying or restarting it.
- On Windows, collect `unity_hang_capture` before restart when diagnostic evidence is needed. Confirm the output path and report dump metadata; the capture must not terminate Unity.

## Artifacts And Screenshots

- Prefer project-relative artifact paths returned by the project bridge.
- Prefer `unity_screenshot_save` for screenshots.
- When fallback is allowed, pass an explicit ordered `fallbackSources` list. Report screenshot `path`, `bytes`, `width`, `height`, `sha256`, the actual `source`, `degraded`, `degradeReason`, and `originalError`.
- For EditorWindow capture, resolve the Unity window with `unity_editor_windows_list` and reuse its exact `typeName` or title. Validate returned match metadata against that Unity `instanceId`/type identity; never select an operating-system window by a matching title.
- Treat EditorWindow/SceneView screenshots as trustworthy pixel evidence only when `pixelSourceVerified=true` and `occlusionSensitive=false`. Always report `captureApi`, Unity PID, `windowHandle`, `foreground`, `degraded`, and `degradeReason`; reject or explicitly downgrade screen-pixel/camera fallbacks.
- UPilot records artifact metadata and hashes; business code decides whether the artifact proves success.

## Assets And Prefabs

- Use `unity_prefab_query_components` for read-only Prefab child hierarchy/component checks before entering Prefab Mode or editing YAML. It returns GameObject paths, component types, and optional serialized fields without changing the current scene or requiring write access.
- Use write tools such as `unity_asset_modify_data`, `unity_component_modify`, or `unity_prefab_save` only after exact target inspection and write access approval.

## Acceptance

- Preserve the `runGuid` returned by `unity_test_run`; after PlayMode Domain Reload or MCP reconnect, query `unity_test_results(runGuid=...)` instead of treating a new in-memory service as authoritative.
- During polling, use incremental status, log, and report APIs instead of repeatedly reading complete outputs.
- For UPilot repository/package acceptance in `./Tests~/UPilotTest`, call `unity_upilot_acceptance_run` and preserve its `summary.json` path, bytes, and SHA256. Treat `status=no_tests` as a distinct terminal result, not a failed synthetic test and not a passing run when tests are required.
- For Shader failures, use `unity_shader_inspect` and `unity_shader_check_errors` before broad Console searches or manual reimports.
- For EditorWindow acceptance, prefer `unity_verify_window` and use `windowMatch` as the target-window truth. Treat legacy `windowDiagnostics` as UPilot window/layout diagnostics, not proof that a third-party window is absent.
- For SceneView pixel acceptance, require an exact `UnityEditor.SceneView` match plus `includesSceneGui=true`, `includesHandles=true`, `pixelSourceVerified=true`, and `occlusionSensitive=false`; report repaint sequence/timestamps.
- Retry automatically only when the registry marks the operation idempotent and non-destructive.
- If the same `failureSignature` repeats, stop blind reruns and fix project logic, test configuration, or acceptance criteria first.
- On timeout, inspect status, operation timing, Console capture, artifact summary, and last progress before choosing one bounded retry or a documented fallback.

## MCP Improvement Feedback

- If testing or development exposes missing MCP capability, inconsistent state, unstable polling, insufficient artifacts, poor failure attribution, repeated manual steps, or integration friction that could be simplified by improving UPilot features, MCP tools, agent rules, or project integration, record a structured improvement item in the UPilot repository-root `TODO_UPilot.mcd`.
- Each item should include the observed problem, affected workflow/tool, proposed UPilot or integration improvement, reproduction or evidence when available, and current status.
- Do not bury UPilot improvement ideas only in external client project TODO files; the UPilot repository-root `TODO_UPilot.mcd` is the source of truth for UPilot product/backlog follow-up.
- Do not block the main task just to write feedback unless the missing MCP capability prevents safe completion; summarize any recorded UPilot improvement in the final handoff.
<!-- upilot:end -->
