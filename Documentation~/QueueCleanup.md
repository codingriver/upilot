# 最小版任务查看与 AI 占用清理

## 设置与窗口

高级设置提供“查看当前任务队列”和独立的“允许 AI 清理当前项目队列占用”。
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

| 类型 | 首版能力 |
|---|---|
| Task / Test | `cancel`、`cleanup`，复用原始 runGuid 和既有流程 |
| Operation | `cancel`，复用已登记 cancelCall 与清理，包括关联 Step |
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

## 日志

复用 Server、UPilot 文件与 Unity Console，以 `[UPilot][QueueCleanup]` 标记，
包含请求、原ID、类型、动作、阶段、结果、原因和错误码，不输出令牌或完整参数。
区分开始、尚未停止、已确认清理、无需操作。失败/不支持/拒绝/结果未确认使用 Error；
Unity 可达时是真实 `Debug.LogError`，不依赖 verbose，不改全局日志设置。
轮询不重复刷屏，转发失败不改变业务结果、不重发操作。
断连或卡死时不保证立即显示 Unity 红色日志，只能记录 Server Error 和结构化结果。

## 验证与部署

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
