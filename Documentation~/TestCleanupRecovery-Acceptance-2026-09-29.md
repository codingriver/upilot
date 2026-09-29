# 测试清理恢复与紧急批量清理验收 — 2026-09-29

## 结论与范围

核心修复、手动/AI 入口已经落地，默认工程真实连续运行及 Python 定向回归通过。
**尚不能声明全部验收完成**：Unity 2022.3 两种 UTF 配置端点阻塞，原生确认对话框
点击尚未完成独立交互验收；原 API 原生对象销毁来源及具体 dirty 编译输入仍未查明。

仅修改 UPilot 权威源码、相关测试、模板与说明；未部署到 F:\xclient2，未修改 PackageCache，
未发布版本，未重启 Unity，未用删除记录或伪造成功解除占位。保留其他未提交/并发改动。

## 实施内容

- `UPilotTestService` / `UPilotTestRunnerAdapter`：身份优先、inactive 不调用取消、
  fake-null 原托管 API 注销、明确 API 所有权、有界清理、分阶段错误历史。
- 在 RunFinished 原 Domain 内释放并持久化原回调/API 证据；Runner、Editor 和最终提交
  仍作为独立屏障。未知资源保持保护，不把 Reload 后丢失引用当作释放证据。
- `UPilotTestRunStore`：最终快照 / last 指针 / active 指针提交语义、部分提交恢复、
  已占用活动指针不可被新运行覆盖。
- 修复 JsonUtility 将 null disposition 序列化为空内联对象的兼容问题：仅完全无证据的空对象
  归一化，部分/损坏证据仍拒绝；成功行政处置后清除残留 ownership-unknown 标记，
  持久化失败不提前清除。
- `UPilotTestService.Disposition.cs`：精确 post-Reload 测试孤儿预览、隔离证明、备份、
  原身份行政处置。Released 不计为成功/清理通过。
- `queue_bulk.py` / `queue_http.py` / `UPilotQueueCleanupWindow.cs`：固定集合预览、
  一次性确认、逐项校验、派发前日志、原请求查询、Advanced Settings 按钮、独立授权。
- Agent rules 47 / Skill pack 56：源模板维护后同步默认和 2022 工程五个目标；两工程
  最后检查均 current，两份安装 Skill 均已校验。2022 同步采用已有离线 CLI，未改 UPM 依赖。

## 编译与加载证据

默认工程 `D:\upilot\Tests~\UPilotTest`，HTTP 8011。
实际 Unity 6000.6.0a2，UTF 1.7.0；Unity PID 43112，Server PID 32968。
授权 Server 维护 `cc7ca8ec-efcf-4561-81df-49ad771b819c` 已完成，readOnlyVerified=true；
这不表示被维护影响的任意其他工作已完成。

最终代码批次：

- writeBatchId：`wb-9752366d-384c-4e54-b892-e4683970ba35`
- compileOperationId：`efcb0ad7fc1d4777a777b54276365dfb`
- 创建时间：1790641570706；编译验证时间：1790641584420。
- terminal / errorsVerified / correlationVerified 均 true；0 errors / 0 warnings。
- 后续收尾仅文档/证据更新，没有再触发无关编译。

## 两轮真实定向验收

精确 fixture：`UPilotRunnerRecoveryTests`、`UPilotP1ReliabilityTests`。
通过 UPilot 验收入口运行，未跑全套。

| 轮次 | Task | 原 runGuid | 结果 |
|---|---|---|---|
| 1 | task-40813e75-d9c3-45a7-b2e8-24939c65b337 | ed0b8acf-bbb9-4e8a-a6a0-cf3202305a16 | 66 passed / 0 failed |
| 2 | task-9a117233-9234-4fd6-9b7d-1096e2b2ba95 | 4dbb73e5-2af3-483b-b8fa-ef47d46c5d44 | 66 passed / 0 failed |

两轮都验证：原身份匹配、resultAuthoritative=true、Task completed/terminal、
cleanupPending=false、cleanupSucceeded=true、cleanupStatus=completed、
cleanupResourcesReleased=true、cleanupErrors=[]、unresolvedResources=[]、persistenceError=""。
第二轮通过真实准入；两轮之间未重启、未手动清状态、未改代码。
旧诊断运行的 Released 不算进这两轮，也不算测试通过。

Python：六组 `test_runner_recovery_contract`、`test_recovery_observation`、
`test_queue_cleanup`、`test_queue_disposition`、`test_queue_bulk`、`test_queue_abandon`，
最终重跑 **115 passed / 2 个既有 websockets 弃用 warning**。

## 手动 / AI 入口验收

- MCP 原请求：`bulk-94eb6b82-9b8a-4421-9240-cd2e210224a4`。
- Editor HTTP 原请求：`bulk-eaee2937-817b-48ae-9914-27e60b91adfb`。
- 两次均只对预览中的 unsupported 集合提交，验证没有误派发、没有伪造 allCleared。
  dispatchComplete=true，allCleared=false；7 个旧 Operation 保留，未删除/重放。
- HTTP 带跨来源 Origin 的请求返回 403 / QUEUE_UI_REQUEST_REFUSED。
- 真实窗口通过 UnityWebRequest 取得预览，再查询上述原 HTTP 请求并显示 finished 和
  “尚未确认全部清理”。这不是无条件清空历史任务的成功案例。
- 专用鼠标调用未产生可验证效果后，使用有界 IMGUI SendEvent 触发预览/查询；
  原生确认对话框未完成独立交互测试，不能以接口测试替代该结论。
- 截图 `snapshot-6fc678dd47f548f0beb6070629b89ae8`（预览）、
  `snapshot-a2cb6583ddff4e56a5bda67902fef85f`（原请求结果）。
  PrintWindow 像素来源 verified、acceptedAsEvidence=true；contentRectVerified=false、
  cropComplete=false，不声称验证了完整内容裁剪。

## 尚未完成 / 保守阻塞

1. **2022 兼容矩阵**：2026-09-29 再次探测 HTTP 8017，WinError 10061；startup 记录
   serverStartRetryStatus=exhausted。已知端口登记/Windows 排除区间问题见根 TODO。
   UTF manifest/lock 仍为 1.4.6，未切换到 1.1.33；磁盘配置不证明加载版本。
   需恢复该精确工程的独立端点并验证项目/实际加载身份后，才能完成两种配置各两轮。
2. **原生确认对话框交互**：窗口预览、状态渲染、底层提交分别验证，但尚未走完真实
   对话框确认点击链路。不扩大为任意桌面自动化或自动点击未知弹窗。
3. **剩余队列**：7 个 Operation 没有安全适配器/原启动身份；清单另报
   CAPTURE_STATE_UNAVAILABLE、EDITOR_IDENTITY_UNVERIFIED。不能声明所有任务已清除。
   不通过放宽身份精度、删除记录或强制改成功解决本次验收。
4. **触发源调查**：已确认 RunFinished → UTF 后置清理/解锁 Reload，以及 PerformanceTesting
   删除生成 JSON/meta 后 Refresh 的链路；具体 dirty 编译输入及原 API 原生销毁来源未证实。
   修复通过在窗口打开前释放本服务资源避免依赖该未决调查，不伪称完整根因已找到。

## 证据位置

`Tests~/UPilotTest/Log/P0P1/cleanup-matrix/`：

- `ownershipfix-compile.json`、`ownershipfix-errors.json`
- `ownershipfix-run{1,2}-{start,task,unity}.json`
- `final-python-verification.txt`
- `bulk-{api,http}-{preview,apply,status}.json`、`bulk-http-origin-denied.json`
- `ui-preview-snapshot.json`、`ui-status-snapshot.json`、`ui-original-status-check.json`
- `default-integration-final.json`、`2022-integrations-final.json`
- `2022-final-endpoint-observation.json`
- `final-evidence-index.json`（关键证据 SHA256，不包含 token 值）

完整预览文件可能含一次性 token，仅留本地，不向聊天或公开报告打印。
