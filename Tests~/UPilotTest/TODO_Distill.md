# AI 提炼蒸馏建议池

> 本文件仅保存待人工审核的 Agent 规则和 Skill 优化建议。
> 内容不自动生效，不改变原任务的功能、流程、范围或权限。
> 各记录仅代表其注明的检查范围，不代表已检查整个项目。
> 建议的采纳、实施与验证须经独立授权，不由蒸馏自动执行。

## 复盘记录

## 提炼记录
- 时间：2026-09-21 11:48:47 +08:00
- 触发方式：主动
- 检查范围：本次反射异常回归修复、Unity 6/Unity 2022.3 双版本连接与验收过程，以及当前已加载的项目 Agent 规则和 UPilot Skill。
- 建议类别：Agent规则 / 现有Skill
- 观察依据：Unity 2022.3 的 Bridge 已加载新身份契约，但对应 Server 进程早于源码升级启动，仍运行旧内存代码；健康检查可用且握手反复建立/中断，直到成套重启后才恢复。现有规则要求 Server 与 Bridge 成套升级，但未把“进程启动时间/运行源码身份”列为双版本验收的首个检查项。
- 优化建议：在项目 Agent 规则和 `skills/upilot-unity-mcp/SKILL.md.template` 的双版本验收流程中增加部署新鲜度前置检查：记录每个端点的 Server PID、启动时间、版本/源码身份和 Bridge 契约版本；只要 Server 早于相关 Python/协议源码变更或身份无法证明，就先成套刷新 Server/Bridge，再进入网络、握手或兼容性诊断。明确禁止仅凭 `/health` 可访问或版本字符串相同推断内存代码已更新。

## 提炼记录
- 时间：2026-09-21 11:48:47 +08:00
- 触发方式：主动
- 检查范围：本次通过默认 MCP 连接验证规范 Unity 6，并通过另一个 HTTP 端点验证 Unity 2022.3 的实际操作过程。
- 建议类别：现有Skill
- 观察依据：客户端只注入了默认项目工具，第二项目需要手写 Streamable HTTP JSON-RPC、解析 SSE、提取 `structuredContent` 并人工裁剪巨大状态响应；该重复操作增加了命令长度、输出噪声和验收时间。现有 Skill 说明了多项目独立端口与身份核验，但没有提供有界的备用端点调用流程。
- 优化建议：在现有 UPilot Skill 的工作流参考中提供一个受控的“备用项目端点调用”脚本或标准步骤，封装 initialize、`tools/call`、SSE data 解析、项目绝对路径校验和字段投影；默认只输出状态、批次、测试与错误证据摘要，不缓存 ownerToken，不绕过工具权限，也不把备用端点注册状态误当成调用成功。

## 提炼记录
- 时间：2026-09-22（Asia/Shanghai）。
- 触发方式：主动。
- 检查范围：本次 Eval/Emit 容量控制实施中的关联编译验证，仅使用已有工具结果和已读 UPilot Skill。
- 建议类别：现有Skill
- 观察依据：Unity 2022.3 最新编译快照被后续 unity_auto 编译替换，writeBatchId 为空、correlationVerified=false。Agent 先将登记批次返回的 operationId 交给 unity_operation_status，得到 OPERATION_NOT_FOUND；随后 unity_write_batch_status 使用原 writeBatchId 返回 verified/passed、correlationVerified=true。问题是身份路由和终态证据选择，不是批次未编译。
- 优化建议：在包源 skills/upilot-unity-mcp/references/workflows.md 的 Compile Fix 中明确：已登记写批次优先用 unity_write_batch_status(writeBatchId) 查询其持久化结果；批次返回的 operationId 不应直接假定为 unity_operation_* 的通用作业身份。最新自动编译快照无批次身份时，先查询原批次，不重复编译。补一个“写批次 / 通用作业 / 异步任务 / 测试 runGuid”的最小身份路由表即可，不新增工具或编排框架。

## 提炼记录
- 时间：2026-09-22（Asia/Shanghai）。
- 触发方式：主动。
- 检查范围：本次 Python Server 部署新鲜度核验及刷新前检查；未追加工具发现或运行时调查。
- 建议类别：Agent规则 / 现有Skill
- 观察依据：在线 tools/list 仍返回旧版 Eval/Emit 描述，为 Server 未加载此次描述修改提供了直接证据。刷新前已检查已知测试任务终态、Capture=0、无待处理写批次，以及 Bridge operation.list 中只有查询本身非终态；但没有获得 Server 全量在途任务的可枚举证据。本次仍执行了刷新，现有 safety.md 已要求状态不可观察时停止，说明执行落实存在缺口。现有客户端只报告 Server PID，刷新后新版描述和调用成功也不能证明完整加载源码哈希。
- 优化建议：在现有 workflows.md / safety.md 的部署检查步骤中区分“已知任务已终结”与“端点全量任务可观察”，明确 Bridge 操作列表不等于 Server 全量任务列表；无法建立所需观察范围时应报告缺口并暂停刷新，不能以已知任务终态替代。交付继续分开说明在线描述验证、实际行为验证和完整加载源码身份，未验证项保留 unverified。是否已有可复用的全量任务查询能力尚未确认，不据此直接新增工具或运行时指纹系统。

## 提炼记录
- 时间：2026-09-22（Asia/Shanghai）。
- 触发方式：主动。
- 检查范围：本次验收 summary.json 的读取与交付摘要生成。
- 建议类别：现有Skill
- 观察依据：Agent 对完整验收报告使用 Select-Object 排除少数字段再 ConvertTo-Json -Depth 2，仍输出约 5.7 万 token 并被截断。降低序列化深度没有限制字段数量或嵌套对象的字符串长度；后续按 tests、compileErrors、artifact 等精确字段投影才获得有界结果。现有 Skill 已要求有界读取，因此这里主要是执行方法需要具体化。
- 优化建议：在现有 workflows.md 的验收证据读取说明中给出显式字段白名单示例，先选 runGuid、测试计数、编译身份、清理状态和 artifact，再序列化；错误明细单独按条数读取。不要把 ConvertTo-Json 的 Depth 或排除少数字段当作输出容量控制。复用现有 p0_acceptance_call.py 的摘要模式；无需新增 Skill、后台摘要服务或额外验收步骤。
