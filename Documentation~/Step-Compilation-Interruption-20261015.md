# Step 编译中断与通用辅助能力

日期：2026-10-15（Asia/Shanghai）。本次保持 `IAutomationStep` 不变，框架实现和测试没有引入项目业务类型。

## 行为与边界

Step 服务监听 `CompilationPipeline.compilationStarted`，排除既有临时诊断模块编译。启动时 Editor 正在编译则拒绝启动；独立 Domain Reload 沿用原有行为。

真实编译开始时，所有尚未终结的 Step 计划立即冻结，包括 Execute/Poll、Cancel/Cleanup、Finally、Restore 和报告收尾阶段。已经进入调用栈的同步回调可以返回，结果不能覆盖编译失败。终态为 `Failed / STEP_COMPILATION_STARTED`，提示“任务执行期间触发 Unity 脚本编译，任务已强制终止。”此前业务错误保留为次级错误和事件。

编译中断标记先写入当前 Editor 会话的 SessionState，再保存失败状态；标记包含任务身份及终止快照，存储键还隔离执行器存储路径。Reload 恢复先检查标记，禁止旧活动存档进入业务恢复。保存失败仍保持冻结，可靠保存和框架收尾完成后才清除标记。

只释放框架持有的 Capture、Snapshot 和 Gizmos 资源，10 秒总预算以首次编译时间计算，Reload 不重新计时。业务清理记为 `SkippedByCompilation`，尚未执行的 Step 跳过，不能据框架资源释放成功宣称业务状态已恢复。普通取消、普通失败和人工立即停止保留原有语义。

失败报告保留原 runId、operationId、Step、执行阶段和编译开始时间；已有不可变报告保留，并把编译失败报告写入 `compilation-interruption` 子目录，以当前 State 的报告路径为准。新任务启动会先清空旧报告 writer，避免启动期编译中断污染上一任务的报告。

## Helper 分工

```csharp
protected static string WriteTextArtifact(
    string runId, string instanceId, string kind, string fileName, string content);

protected static string RestoreResultJson(string status, string errorCode = "");
```

`WriteTextArtifact` 只在当前任务的可写主线程回调及登记窗口内可用。框架验证任务身份，分配 `attachments/<SHA256(instanceId)>/<fileName>`，拒绝目录穿越、目录联接、保留设备名等非法路径。文本以严格 UTF-8 无 BOM 编码写入新临时文件，刷盘后不覆盖发布，核验字节和 SHA256，持久登记成功后返回绝对路径。

相同 owner、kind、路径和字节允许幂等返回；内容冲突或未登记的同名文件明确失败，不覆盖、不认领。登记失败保留已发布证据并报告错误，不能假报写入成功。编译终止后拒绝新业务写入，已登记证据保留。业务负责 JSON 结构及序列化，框架不解析附件内容；已有文件注册和 Snapshot API 继续使用。

`RestoreResultJson` 复用真实 Restore 解析器校验 `Restored / Unsupported / Failed`，`Failed` 必须包含非空错误码。它仅构造协议 JSON，不修改执行结果、错误缓存或恢复状态，不执行恢复，也不参与编译终止清理。

## 验证

- 规范项目：`D:/upilot/Tests~/UPilotTest`，Unity `6000.6.0a2`。最终修改批次 `wb-7d496711-b006-4d47-b73d-f266f2efd400`，createdAt `1792073535869`、verifiedAt `1792073576447`，`verified/passed`、`errorsVerified/correlationVerified=true`、0 编译错误。验收复用后续已验证编译 `req-0f765e51-22f6-42e4-a191-6859eae432d8`，未再次手动编译。
- 7 个定向 fixture：CompilationInterruption、JsonContract、Executor、ImmediateStop、ImmediateSnapshotRelease、Service、Gizmos。最终 Task `task-2ab68147-90a0-4442-8d0d-b7c28286cb0e`，runGuid `5c11dcc6-64e0-4321-9d0b-9d0a2dd49d0d`，**267/267 通过，0 失败/跳过**；`resultAuthoritative/acceptancePassed/cleanupVerified/testIdentityVerified/sourceUnchanged=true`。
- 规范报告：`Tests~/UPilotTest/Logs/UPilot/Reports/Acceptance/1792073672856_req-e79c3d0b-ba68-4172-810d-fc3433dc199c/summary.json`，1330416 bytes，工具报告 SHA256 `2ebb3d20efd23c64596378fe1134897e490f359522754d483d937691695e9b7e`。
- 隔离测试覆盖各执行/收尾阶段、旧回调晚到、保存失败后重建、重复编译、不发生 Reload、独立 Reload、固定释放期限、资源失败、旧报告保护、人工立即停止回归，以及 helper 协议、路径、幂等、冲突、字节和登记失败。
- 历史失败保留：首轮 251/261，10 项因并行源码写入触发编译而被忙检查拒绝；随后 265/265 但 `sourceUnchanged=false`，不算验收成功。补测后的 266/266 曾完整通过。最终报告重置修复后，一次验收尚在发现阶段被并行 Reload 中断，返回 `UPILOT_ACCEPTANCE_DISCOVERY_FAILED / COMMAND_RECOVERY_REQUIRED`，没有启动测试；以上述最终 267/267 作为交付证据，未放宽门槛。

## trunk 接入与真实编译

`F:/trunk/Assets/Editor/KingShotBattle/Steps/KsbSkillSequenceSteps.cs` 的公共 Attach 改用新附件 API，Restore 使用 JSON helper；业务序列化留在项目。删除旧自写文件 helper，13 处调用复用公共入口。未修改 8 个注册 ID、业务 JSON、判定或增加编译/Reload 分支。历史附件保留，今后以报告附件元数据取得路径。

接入批次 `wb-5fd76eaa-93be-44f1-83c1-87d454c5e230`，createdAt `1792073793324`、verifiedAt `1792073860437`，`verified/passed`、`errorsVerified/correlationVerified=true`。项目既有 `KsbSkillSequenceStepContractTests` 通过 **19/19**，runGuid `121a9f27-8aa4-48cc-a6b0-4e0b0387286d`，`terminal/resultAuthoritative/cleanupSucceeded/cleanupResourcesReleased=true`。

真实编译使用官方 Operation 的两个通用 wait Step，没有启动战场。原 Operation `op-79853852-88be-404d-bcfc-adfa22b840c3` / run `c6ffab3d632644b58f3e475c0940ac5f` 在接入编译开始时失败，时间 `1792073810159`，阶段 `Executing/Normal/Executing`。当前 Step 为 Failed，Finally 为 Skipped，两者 `cleanupState=SkippedByCompilation`；报告、Step State、MCP 和外层 Operation 均为 `STEP_COMPILATION_STARTED`。实际 Reload 后 domain 8→9，原身份、失败原因和 Written 报告保持，Finally 未启动；Gizmos 关闭已验证，无自有 Capture 或 Snapshot。

工具核验的项目报告：`F:/trunk/Logs/UPilot/Reports/Steps/c6ffab3d632644b58f3e475c0940ac5f/summary.json`，4053 bytes，SHA256 `9256ba66e212ac873ece7e941093b3a1c455202cdfd724b5922cc0f5e907192c`。这次现场覆盖正常执行期的编译及其 Reload，其余阶段由隔离 fixture 覆盖；未运行完整 EditMode 或真实战场测试，未进行 SVN 写操作。
