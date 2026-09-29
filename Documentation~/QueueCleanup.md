# 任务查看、恢复与手动 / AI 批量清理

## 设置与窗口

高级设置提供“查看当前任务队列”、“批量清理可安全处理项…”和独立的“允许 AI 清理当前项目队列占用”。
配置字段 `aiQueueCleanupAllowed` 默认 true；旧配置缺失同样开启，明确 false 保持关闭。
全选/取消全选不影响它。AI 不得自行修改授权。

窗口打开加载、手动刷新，不推进任务，无逐项或全部清理按钮。
Server `/queue` 与工具无目标预览复用同一汇总，不新增调度器或任务数据库。
展示未完成/阻断的 Task、Test、Operation、WriteBatch、关联 Step、Capture 和已知命令。
`unity_operation_list` 只列 Bridge 命令 tracker，旧 `active/total` 是全局口径；
`activeMatchingCount`、`activeOtherCount`、`matchedCount` 和 `returnedCount` 分别说明过滤后活动数、
其它活动数、截断前匹配数及实际返回数，`queryExcluded` 是被排除的本次查询命令数。
它不等同于全部持久 Operation；维护判断使用 `unity_queue_cleanup()` 清单。
清单中的 Bridge 命令只含 Server 已知记录，未枚举 Bridge 队列时明确标为 incomplete。
断连、过期、缺失和未知明确显示，不能当作空队列。Step 保留原始 instanceId、runId 和
Operation 关联；来源为既有持久记录，明确标记实时状态未验证，不执行 Restore。

## 精确清理

`unity_queue_cleanup()` 默认只读汇总。预览示例：

```json
{
  "targetType": "WriteBatch",
  "targetId": "<原始 writeBatchId>",
  "action": "release",
  "reason": "历史批次阻断，保留原始未知结果",
  "dryRun": true
}
```

保持目标、动作、原因不变，传入预览返回的 `confirmToken` 和 `expectedProjectPath`，
设置 `dryRun=false` 执行。令牌 120 秒过期且只消费一次，目标变化拒绝。
开关开启即持续授权，不要求任务属于当前聊天，无需二次人工确认；
不授予任意文件写入能力，不修改原工具权限。

| 类型 | 支持能力 |
|---|---|
| Task / Test | `cancel`、`cleanup`；具备精确证据的持久测试 Task 支持 `release`，已隔离的 post-Reload 孤儿支持 `abandon`；均沿用原 runGuid |
| Operation | `cancel`；`recover` 仅恢复原 Step 未决清理；`release` 仅支持已审核的安全适配器（首版仅纯 `wait_seconds` 计划），不重跑 Execute / 已执行 Finally |
| Capture | `stop`，精确 sessionId，保留日志产物 |
| WriteBatch | `release`，历史 recovery_required 或无自动编译授权/无派发身份的 deferred，排除仍有执行动作 |
| Step、普通命令、无适配器 Task | 明确不支持，不删记录冒充停止 |

## WriteBatch 保真处置

原始 SQLite 行备份到 `Library/UPilot/ServerState/queue-backups/<随机ID>.json`，
独占创建、flush/fsync、回读 SHA256 验证后，事务写入独立 `disposition_json`。
目标变化或备份失败不解除阻断。保留原 status、terminalSnapshot、compile 身份和 unknown outcome。
保存请求、原因、备份路径/大小/hash 和处置时间；恢复与迟到快照不重新占位或改写原结果。
不清除其它记录，不重启或重放 Start。

EditMode 本身不是安全证据：还检查权威新鲜状态、编译阶段、主线程队列、
已发/挂起命令、自动批次恢复观察者，以及晚于批次进展的主线程泵时间。
无法排除执行就拒绝。

## 高级设置“批量清理可安全处理项”

这不是删除队列或把所有任务改为成功的按钮。它对**一次预览中的固定目标集**逐项调用
既有取消、清理、恢复或行政处置适配器；无法证明原执行已隔离的目标仍保留。
不会因为聊天退出而自动执行，不新增调度器，不修改 `stateless_http`，不重启 Unity。

操作步骤：

1. 在 `UPilot/高级设置` 打开“批量清理可安全处理项…”，填写清理原因。
2. 点击“预览当前全部任务（无副作用）”，查看每个目标的动作或不支持原因。
3. 确认影响范围后，点击“确认执行预览中的可处理项”并确认对话框。
4. 使用“查询原请求（不重发）”观察结果。关闭窗口或 Domain Reload 不撤销后台请求；
   原请求 ID 保存在 SessionState。响应丢失时只能查询原 ID，不能重新提交以猜测结果。

手动窗口与 AI 使用同一个独立 `aiQueueCleanupAllowed` 授权和一次性 token 校验。
关闭授权时可以查看，但不能执行。窗口不会修改授权。

### AI 调用（复用 `unity_queue_cleanup`）

预览：

```json
{
  "targetType": "All",
  "targetId": "*",
  "action": "force_clear_all",
  "reason": "处理当前项目已阻塞任务，保留原始结果和证据",
  "dryRun": true
}
```

保存预览的 `requestId`，执行时保持类型、目标、动作和原因不变，添加原
`confirmToken`、`expectedProjectPath`，设置 `dryRun=false`。不把 token 打印到日志。
状态查询（不需要重新提交 token）：

```json
{
  "targetType": "All",
  "targetId": "<原 bulk-requestId>",
  "action": "force_clear_status",
  "expectedProjectPath": "<原项目绝对路径>"
}
```

- 提交前校验项目、授权、目标集和每个可处理目标的身份；状态改变则拒绝。
- 每个副作用前先持久化原请求记录；断线或 Server 重启后查询原记录，不重发不确定请求。
- `dispatchComplete=true` / `status=finished` 只表示本轮派发结束，不表示任务全部停止。
- **仅 `allCleared=true` 才表示当前清单完整且没有剩余占用项**。不完整清单、未知结果和
  unsupported 均不能得到“全部清理成功”。逐目标结果及 `remaining` / `issues` 必须同时查看。
- 日志位于 `Library/UPilot/QueueCleanup/bulk-*.json`，保留原任务/Operation 的记录与产物。
- 内部 Editor HTTP 传输仅开放有限动作，限制 loopback、精确项目、JSON 请求大小，拒绝浏览器
  Origin / 代理来源。第三方 AI 仍只使用 Streamable HTTP MCP，不把该内部传输当作通用执行接口。

### Reload 后孤儿测试的行政处置

仅在原 runGuid、旧/当前 Domain、原执行与在途命令隔离、Runner inactive、Editor 就绪等证据
满足时，允许 `abandon`。原记录先备份并校验，再提交 `Released` 并解除实际活动占位。
同一个处置请求只观察原结果，不再次派发。不支持任意自定义异步工作或身份丢失的 Start。

`Released` 是行政终态，**不是测试通过，不是资源清理成功**；原业务结果（已知或未知）、
历史错误、未确认资源及备份证据保留，`cleanupSucceeded` 不伪装为 true。真正的正常清理
必须取得原回调/API 释放及最终存储提交证据，不能以行政处置替代验收。

## 测试清理的正常完成条件

Runner inactive 时不绑定取消 API；active 时沿用审核过的取消能力；unknown 保留保护。
API fake-null 仍按原托管引用注销自己的回调，不创建替代 API，不影响其他回调。
在 RunFinished 原 Domain 内先释放并持久化本服务资源证据，随后等待 Runner/Editor 就绪及
最终提交。释放失败停止热循环；30 秒绝对清理预算不被 status/重连/Reload 重置。
显式清理重试后，只有对应阶段验证成功才将其当前错误移入 `cleanupErrorHistory`。

正常终态要求权威业务结果、`cleanupPending=false`、`cleanupSucceeded=true`、
`cleanupStatus=completed`、无当前错误/未释放资源，以及最终快照和活动指针提交成功。
活动指针不能清除其他运行；业务失败不因清理变成成功或 aborted。

## 日志

复用 Server、UPilot 文件与 Unity Console，以 `[UPilot][QueueCleanup]` 标记，
包含请求、原ID、类型、动作、阶段、结果、原因和错误码，不输出令牌或完整参数。
区分开始、尚未停止、已确认清理、无需操作。失败/不支持/拒绝/结果未确认使用 Error；
Unity 可达时是真实 `Debug.LogError`，不依赖 verbose，不改全局日志设置。
轮询不重复刷屏，转发失败不改变业务结果、不重发操作。
断连或卡死时不保证立即显示 Unity 红色日志，只能记录 Server Error 和结构化结果。

## 本次验证（2026-09-29）

默认工程两次连续定向运行均 66/66，均取得权威正常清理终态；Python 六组定向回归
115 passed。真实 MCP 和 Editor HTTP bulk 预览/提交/原请求查询通过；当前 7 个不支持
Operation 被保留，`allCleared=false`，不能称为“全部任务已清空”。

Unity 2022.3 的 HTTP 8017 端点不可用，UTF 1.1.33 / 1.4.6 矩阵未完成；未更改依赖。
实际窗口预览和原请求查询有截图证据；原生确认对话框点击尚未完成独立交互验收。
详细身份、编译、runGuid、证据限制见 `TestCleanupRecovery-Acceptance-2026-09-29.md`。

## 历史验证与部署记录（以下不是当前 Server 状态）


定向测试：`upilotserver~/tests/test_queue_cleanup.py`、持久 Task/Operation、
编译状态、恢复契约、清理屏障及 Unity `UPilotQueueCleanupTests`。
预期 Error 用 `LogAssert.Expect` 声明。未运行完整 EditMode 套件，未清理当前真实任务。

最终关联编译 `wb-457ac844-dffc-40dc-b7e6-f0f0ec0c75a4`：0 errors / 3 条既有 Flow warning。
Unity 定向验收 10/10 passed，runGuid=`01dcdc21-7aca-4845-9efa-7eeabd2a4f4d`，
cleanupVerified=true，测试期间 sourceUnchanged=true。报告：
`Tests~/UPilotTest/Log/UPilotAcceptance/1790145129922_req-01ff995b-133e-419e-ac2b-0db862bada68/summary.json`，
514768 bytes，SHA256：
`f79965163b82d60c6617429e9d9cfab112fefeff2ac0afc00715c5a4038372f4`。
Python 最终定向回归 112 passed，2 条既有 websockets 弃用 warning；包含关闭授权、
真实代理入口无通用写权限执行、跨项目/状态变化拒绝、备份失败、重启/迟到回包、
未知起始身份拒绝、日志去重及凭据脱敏、断连/部分数据和既有 Capture 入口兼容。

Agent rules 43 / skill pack 49 已生成、校验并同步规范工程全部五个目标；
两份安装 Skill 校验通过，规则受控块外字节保持不变。
实际重复同步为 current/changed=false；46 个管理文件的路径/内容hash/完整精度mtime
组合 SHA256 均为 `52779afcc126e348784221d26cbae5d3b0de63260c83a281cb9e326976bab8eb`，
备份目录集合未变化，无新增备份。

在线 Server PID 108348 尚未加载新工具，也未注册 `unity_service_restart`。
C# 热加载不等于 Python 已更新。需用户更新/重启 Server、刷新 MCP 工具列表后，
再验证 `/queue` 与 `unity_queue_cleanup` 的在线可用性；本任务未绕过授权进行 shell 重启。
因此上述测试证明源码及 C# 定向行为，不代表旧 Server 已运行新清理工具或窗口数据接口。

## 通用编排暂停后的边界

新的 Operation（手写回调和全部 stepPlan）及公开 Step Start/Validate 固定暂停，返回 `GENERIC_ORCHESTRATION_DISABLED`；不创建 Run、Capture 或业务请求。专用测试、验收、编译、构建、Capture 和 Snapshot 不受这项门禁影响。不能借 Task、反射或临时脚本绕过。

历史查询、产物和已有安全清理能力仍保留。`operation_wait` 遇到 `RecoveryRequired` 立即返回 `recoveryBlocked=true`、`terminal=false`，不自动取消、重启、重置期限或重放 Start。停用新启动不代表历史资源释放，也不把未知结果改为成功、失败或 Released。

“批量清理可安全处理项”保留原 `force_clear_all/force_clear_status` 参数以兼容客户端，仍要求预览、token、项目核验和独立授权。必须分别展示可处理、不支持、仍在清理和库存未知项；`dispatchComplete` 不等于 `allCleared`，`allCleared=false` 不能宣称全部清理完成。
