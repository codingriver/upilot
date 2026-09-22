# UPilot Automation 公共支撑能力集成方案

状态：已完成  
最后更新：2026-09-21  
适用版本：UPilot 0.3.32，Unity 2022.3+

## 1. 目标与边界

本迭代只把可由任意 Unity Editor 自动化项目复用、且不理解业务语义的能力集成到现有 `io.github.codingriver.upilot` 包。KingShotBattle 的启动、PlayMode、登录界面等待、GM 入场、Case `Begin/Tick/Cleanup`、战斗证据时机、恢复和退出仍由项目桥负责。

UPilot 继续以既有 Operation 作为长任务权威，不增加第二套调度器、Provider 执行框架、Case Runner、工作流 DSL、自动重试/回滚、运行时监控框架或 `automation_start/status` MCP 工具。新增代码全部位于 Editor 程序集，命名空间为 `CodingRiver.UPilot.Automation`，不增加 UPM 依赖。

## 2. 必要能力清单

| 模块 | 公共职责 | 明确不负责 |
| --- | --- | --- |
| `AutomationCatalogV1` / `AutomationSelectionV1` | 校验纯数据 Case/Suite 目录；按 Suite 或显式 Case 列表解析选择；检查重复、未知、顺序、环和终止项 | 业务类型发现、实例化、工厂、关卡/英雄/兼容性、自动注册或排序 |
| `UPilotConsoleCaptureApiV1` | 为项目内编译桥公开 Capture 的能力探测、启动、状态、边界、异步分页读取和所有权保护停止 | 强制停止、凭据托管、接管他人会话、同步阻塞扫描 |
| `AutomationLogPolicyV1` | 对结构化 Console 记录按阶段/Case 区间和项目提供的规则分类，执行 allow/deny/budget 与证据完整性门禁 | 解析业务文本报告、定义业务白名单、推断未知上下文 |
| `AutomationReportWriterV1` | 生成可恢复校验的 `events.jsonl` 与不可改写的 `summary.json`，返回字节数和 SHA256 | 业务执行恢复、任务状态机、复制原始 Console 文件 |

## 3. 公共契约

### 3.1 Catalog 与 Selection

- ID 使用 `StringComparer.OrdinalIgnoreCase`。
- `AutomationCaseDescriptorV1` 只包含 ID、展示名、中性 `beforeCaseIds/afterCaseIds` 和 `mustBeLast`。
- `AutomationSuiteDescriptorV1` 只包含 ID 与有序 Case ID。
- 显式 `caseIds` 非 `null` 时优先于 Suite；空数组是错误，不回退到 Suite 或全量。
- 缺少选择、重复 ID、未知 ID、Suite 空列表、约束环、实际顺序违规和非末位终止 Case 都返回结构化诊断。
- before/after 只在本次选中集合中生效；分析不插入、不删除、不重排 Case。

### 3.2 Console Capture

- `Start` 与 `Stop` 只能在 Editor 主线程调用；`ReadAsync` 仅在主线程准备一致快照，磁盘扫描在线程池执行。
- Operation 创建的采集由 Operation 持有和停止；项目只读取 session ID。
- 本地菜单创建的采集必须自己生成并跨 Domain Reload 保存 owner token，停止时提交同一凭据。
- API 不公开 `forceStop`；凭据缺失或不匹配时拒绝停止，也不静默接管现存会话。
- 返回对象只包含现有 manifest 的 token 哈希，不回显原始凭据。

### 3.3 Log Policy

- 阶段与 Case 采用 `[fromSequenceInclusive, toSequenceExclusive)` 区间。
- 调用方必须在业务 teardown 后固定最终边界，并等待该边界前记录落盘后再完成分页分类。
- 丢失记录、分页不完整、读写失败、会话身份不匹配或等待超时均产生 `evidenceComplete=false`，不得通过。
- deny 规则优先；随后按声明顺序选择第一个 allow 规则。超过该规则 `maximumCount` 的记录阻断。
- 默认无 allow 规则；`Error/Exception/Assert` 阻断，普通 Log/Warning 仅在 deny 命中时阻断。
- 有 phase/case scope 的规则不能匹配未知上下文。
- fingerprint 只归一化空白、GUID 和堆栈源码行号，不归一化普通数字或业务 ID；分类始终基于原始记录。

### 3.4 Report

- `Create` 创建新 run；`OpenExisting` 只恢复报告写入，不恢复或重放业务执行。
- 打开现存 run 时校验 run identity、事件版本、连续 sequence 和每一条 JSONL；损坏显式失败，不截断。
- `Complete` 原子创建 `summary.json`。已有 summary 时只允许内容完全相同的幂等调用，不能改写 outcome、终止时间或正文。
- 输出目录必须位于当前 Unity 工程内。原始 Console Capture 以外部 artifact 引用记录，不复制文件。
- `GetArtifacts` 返回最终路径、bytes 和 SHA256；报告是证据，不是第二个任务状态机。

## 4. 包结构

```text
Editor/Automation/
  UPilot.Automation.asmref
  AutomationCatalogV1.cs
  UPilotConsoleCaptureApiV1.cs
  AutomationLogPolicyV1.cs
  AutomationReportWriterV1.cs
Tests/Editor/Automation/
  AutomationCatalogV1Tests.cs
  AutomationLogPolicyV1Tests.cs
  AutomationReportWriterV1Tests.cs
  UPilotConsoleCaptureApiV1Tests.cs
```

`UPilot.Automation.asmref` 指向既有 `UPilot.Editor`。测试继续进入既有 `UPilot.Editor.Tests`，不把 NUnit 依赖带入产品程序集。

## 5. 接入时序

```text
Operation/项目桥启动
  -> 获得或创建 Console Capture
  -> Catalog Validate + Selection Analyze
  -> 项目桥执行自己的业务生命周期与 Case loop
  -> 按 sequence 记录 phase/case 区间
  -> 失败证据与 Snapshot 收集
  -> 项目 teardown / restoration / cleanup
  -> 固定最终 Console 边界并完成分页读取
  -> Log Policy 分类
  -> Report Complete
  -> 项目桥发布业务终态
  -> Operation 停止其持有的 Capture
```

项目桥不得在证据、恢复、清理日志分类完成前发布 terminal，因为现有 Operation 观察到业务 terminal 后会停止它持有的 Capture。

## 6. 验收

- 默认工程：`D:\upilot\Tests~\UPilotTest`。
- Unity 2022 兼容工程：`D:\upilot\Tests~\UPilotTest2022`，只在需要兼容矩阵时运行。
- 仅运行新增 fixture 及直接回归；不默认运行全量 EditMode。
- Catalog：缺失/空/重复/未知选择、顺序、环、must-be-last。
- Log：deny 优先、allow 顺序、跨 Case budget、未知 scope、fingerprint、证据不完整。
- Capture：主线程门禁、分页、缓冲落盘、边界、错误凭据和不公开 force-stop。
- Report：run identity、损坏尾记录、冻结终态、幂等完成、工程路径门禁、artifact hash。
- 静态检查：产品程序集无业务命名空间、无 NUnit、无 Flow/Tracer 依赖、无 Case 执行循环。

## 7. 后续建议

- P1：完成本文件四个模块及所有权/证据完整性门禁。
- P2：只有出现实际 CI 消费者后再增加 JUnit 导出。
- 暂不开发：通用 Case Runner、自动发现、分组/重试/回滚、业务日志规则库、截图替代实现、第二套 Operation 或 runtime 监控框架。

项目侧延期修改见 `D:\MA\_AI_Docs\xclient\30_ENVIRONMENTS\xclient2\Automation\UPilotIntegrationMigration.md`。

## 8. 完成证据

- 产品代码位于 `Editor/Automation`，通过 asmref 进入既有 `UPilot.Editor`；未增加 package dependency。
- 定向测试位于 `Tests/Editor/Automation`，包含 Catalog/Selection、Capture facade、Log Policy、Report Writer、13KB Console 记录和两个自持执行循环的中性宿主。
- Unity 6 canonical：write batch `wb-fba37fed-b83a-4bb5-b30d-e8968de574ff` 关联编译为 `terminal/errorsVerified/correlationVerified=true`；runGuid `1ad22085-bebd-45c9-9d0e-257cc392f6b8`，21/21 Passed，cleanup 成功，结果权威。artifact：`D:\upilot\Tests~\UPilotTest\Log\UPilotAcceptance\1789986321893_req-d8e1ee52-a684-4081-8533-a7553e62de3a\summary.json`，492976 bytes，SHA256 `13d94ef227fe369a93e048cd9391b226eb7255956bd40e834ef3f26d2e6f0ea6`。
- Unity 2022.3：write batch `wb-2ca626bd-61f6-43bc-8f0f-e26372f8468e` 关联编译为 `terminal/errorsVerified/correlationVerified=true`；runGuid `37dfe123-7e30-422c-8c70-03f05d315ec2`，21/21 Passed，cleanup 成功，结果权威。artifact：`D:\upilot\Tests~\UPilotTest2022\Log\UPilotAcceptance\1789986404557_req-f78d6389-1b0f-4494-b817-edff5839ce37\summary.json`，350294 bytes，SHA256 `c96d6e51ab95df0f9cef6a9e12f761eeef6ea8024b1e79b48e82cd47391cb14f`。
- 两套验收均确认 `sourceUnchanged=true`；编译 warning 仅为既有 Flow Schema 的三条 UAC1009，不来自本模块。
