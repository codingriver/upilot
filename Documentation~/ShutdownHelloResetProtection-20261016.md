# UPilot 停服保护与失败诊断（2026-10-16）

## 两次故障需要分别判断

用户指定的 trunk 日志中，旧服务停止观察约在 2.61 秒处因 `service_reset` 结束，尚未取得进程退出及 HTTP/WS 双端口释放证据。原 hello 应用路径会在首次空绑定或 Server 生命周期改变时重置活动任务，却没有检查当前停止操作和连接周期，因此迟到的握手能够中断正在等待的停止观察。修复不能仅延长停止等待时间。

2026-10-16 19:54:47 的修复尝试 `7b54c835f21c4cf49b123f90ac9ecc11` 则在进程身份预检处退出：单 PID 命令行不可读，归属保持 Unknown，未停止进程、未提交重启，也没有对应 restart operation ID。原读取器把 helper 启动异常、超时和空输出等原因折叠为同一提示；旧证据不足以确认具体底层原因。后续 WMIC 实测可读，接近原一秒查询上限，只能说明瞬态超时有可能，不能证明历史发生了超时。

附件中的 `_activeTab` 空引用发生在另一条 `csharp.eval` 调用中。没有原表达式及关联证据，不能认定它导致命令行读取失败。附件包含的预期故障测试、历史测试失败与编译观察超时也不能合并为同一根因。

## 实现边界

- 停止准备前绑定 owner、代次和原期限，覆盖独立停止、强制停止、重启与 Bridge 关闭。hello 在接收和主线程应用处核对真实 socket、连接周期、取消 token、session 和停止操作快照；过期请求只丢弃，不重放。
- 首次绑定及同 Server 生命周期重连不清理任务。当前操作的新服务必须经过实例、进程创建时间及启动身份核验，才能复用该操作的一次主动清理结果；原部分失败信息保留。跨 Domain Reload 仅恢复可证明属于原操作的清理结果与观察，不恢复旧任务、不重派停启、不延长期限。
- 当前有效 hello 收到后、主线程应用前，既有基础设施只读观察返回 `UPILOT_RECONNECT_PENDING`，由 Server 的原读取策略在原期限内观察。该分类不提前认证，不新增业务命令重试。
- 每次被拦截的 hello 以普通 `Debug.Log` 输出一条含独立 eventId 的记录，同时立即写入 `Logs/UPilot/upilot-yyyy-MM-dd.log`。最低级别、Verbose、网络调试及 Console 开关不影响这组日志；请求来源栈和执行点栈写在正文中。
- 文件与 Console 输出相互隔离，文件失败只向 Console 报告，不递归写失败文件。错误继续使用现有 Error 通道与十秒聚合；敏感值先脱敏，进程诊断不记录命令行或 helper 输出正文。
- 进程读取失败细分为原期限耗尽、helper 启动失败、进程等待/输出等待超时、非零退出、空值、格式错误及读取/清理异常，记录 PID、耗时、预算、退出码和 OS 错误。无法证明进程归属时仍拒绝停止。
- 预检失败保存本次 gate 和明确的未知值，不借用历史重启诊断。历史恢复提示仅在当前身份、Bridge 会话及实际只读验证通过后解除，失败历史保留。

公共 MCP 接口、维护授权、真实进程退出和双端口释放安全门保持原契约。本任务没有请求或执行真实 Server/Unity 重启。

## 验证记录

规范验收工程为 `D:/upilot/Tests~/UPilotTest`，独立 HTTP 端口为 8011。定向范围为握手、日志、停服保护、命令行诊断、遗留服务恢复、启动与停止观察、维护和安装修复等十三个 fixture，不运行全库。

已有五个 Server 重连/原期限定向回归通过。早先 WriteBatch 的编译失败、到期和 aborted 结果保留；后来的全局零错误快照不替代原批次的关联验收。

生产源码冻结后的首轮 WriteBatch `wb-23a24136-f588-454e-a684-2d9da25ede2f` 为 verified/passed，关联与错误核验均通过，当轮结构化编译 0 错误、0 警告。首轮十三个 fixture 共 588 例：564 通过、24 失败，Task 为 `task-42607770-c7e3-44ab-b11c-d8e03f5ea720`，runGuid 为 `5d8a7cf7-2822-4e05-84f3-b7d168cf2b23`，权威结果及清理均已完成。24 个最终失败均为测试未声明新增的强制普通 Lifecycle Log；未据此删除业务断言、Error/Warning 断言或 `NoUnexpectedReceived`。

仅在三个相关测试文件中补充精确事件及数量预期，生产源码保持原 hash。保存收尾有一次晚于登记的正则转义修正，原批次 `wb-115c6597-b376-4d5a-b460-c195a7b87500` 不作为该晚写版本的证明；最终单独登记 `wb-4e9d03f9-89ef-4e9e-82d2-c839956f10d3`，compileOperationId 为 `3d1d033b99bf4c5abd2e1caf94f9f9ed`，compileRequestId 为 `req-3ca8cfe7-c80e-4819-ad3a-9b5a510ca7e3`。最终批次 verified/passed，`correlationVerified=true`、`errorsVerified=true`，`lastCompileVerifiedAt=1792155584302`，晚于 `writeBatchCreatedAt=1792155569015`。

最终 Unity 结构化编译为 **0 错误、1 警告**：现有 `Tests/Editor/UPilotDiagnosticModuleTamperTests.cs:88` 的 CS0618 `AssemblyBuilder` 弃用警告，不修改无关文件消除它。独立编译 Console 边界仍为 `CONSOLE_EVIDENCE_NOT_FOUND`，不能报告为编译 Console 零错误。

重新发现后精确选择原 24 个失败叶子，匹配 24/24、无未匹配或重复 selector。官方复验 Task `task-f6f48c5e-9366-46da-ab50-855302406bf1`、runGuid `4dec1aa8-5853-4bac-b7d1-fd03fd2fa8ec` 已 completed：**24/24 通过、0 失败**，权威结果、测试身份、清理及源码未变化均验证通过；`cleanupPending=false`、`unresolvedResources=[]`。这是 24 例定向复验，不改写首轮为一次 588/588 通过。首轮日志 fixture 的 13 例均通过，包含过滤开关关闭、两段栈、双写、长正文、脱敏及文件 sink 故障隔离。

官方复验产物为规范工程下 `Logs/UPilot/Reports/Acceptance/1792155644129_req-9c1fe6c0-1fcf-4823-8baf-1c254a05c255/summary.json`，独立核对 1,177,385 bytes，SHA256 为 `2ffde9ca5246d66dd3f3c30a86c7425eb4dc2f06132957159e978d9f4975d303`。最终 24 份源码及 meta hash 均未变化，保持 UTF-8 BOM 与原换行；最终 Capture activeCount=0，队列 complete=true/isStale=false/items=[]，Editor Ready 为 authoritative=true/isStale=false。

规范工程 Server PID 98192、Unity PID 38044、最终 Bridge session `b72f2b17f94d4e23868dfc1fae1c66e1`。Server `loadedSource=equivalent` 仅证明所列入口代码与源码等价，不覆盖 globals、closure、依赖和全部模块。没有通过真实停服/强杀触发 Domain Reload 的端到端竞态，也未破坏真实磁盘或 Console 输出端注入故障；隔离分支与 sink 故障测试不等于这些现场条件已验收。未刷新或重启 trunk，不能据规范工程结果声称其已恢复启动或原 8 秒复用超时已消失。

完整不可覆盖观察、源码基线及汇总保存于规范工程 `Log/P0P1/`，交付索引为 `reset-guard-delivery-20261016.json`；首轮、早期失败、到期及 aborted 历史均保留。
