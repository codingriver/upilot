# Profiler Capture 默认时长验证记录

日期：2026-10-14。规范项目：`D:/upilot/Tests~/UPilotTest`，Unity `6000.6.0a2`。

## 实现与生效

高级设置 → 常规 → 周期与超时新增“Profiler 默认采集时长（秒）”：初始 30 秒、范围 1～3600 秒、整数，沿用项目隔离的 EditorPrefs，并纳入现有默认恢复和项目偏好重置清单。

`unity_profiler_capture_start.durationSec` 省略或为 `null` 时，Server 发送内部 `useConfiguredDuration=true` 和旧 Bridge 兼容数值 30；显式数值优先并保留小数及原范围限制。Bridge 开始时读取一次默认值，将本次实际值固定到现有结果字段 `durationSec`。没有增加后台采集或设置巡检。

## 编译与定向回归

| 检查 | 结果与身份 |
| --- | --- |
| 规范项目关联编译 | `wb-7d92b889-563b-4e0c-9a0d-6c1c78202c8c`，`compileRequestId=req-e52a528a-a06b-4bcb-be36-345e92fc0c63`，`compileOperationId=4b148e91aa444ebfa5f3a42dc9c6b99d`。0 错误、1 条既有 AssemblyBuilder 弃用警告；`correlationVerified=true`、`errorsVerified=true`。创建时间 `1791970313642`，验证时间 `1791970360578`。 |
| Unity 定向 EditMode | **18/18**，`runGuid=3ace7f85-bc4d-4937-96a7-cff9b8e91568`；`resultAuthoritative=true`、`cleanupSucceeded=true`、资源释放完成。 |
| Python 定向回归 | **84 passed**，1.70 秒；使用项目 `.venv/Scripts/python.exe` 执行 `test_project_analysis_tools.py`、`test_p1_interaction_contracts.py`、`test_p2_public_tool_contracts.py`。两条既有 websockets 弃用提示。 |

Unity 覆盖默认值及项目偏好键注册、保存、范围限制、默认恢复、显式 30 和小数秒、旧协议未携带标记、运行中修改及重置不改变原时长，以及 lowOverhead／legacy 的到期与手动停止资源释放。复用原有 Profiler 可靠性与终态耗时冻结回归。

Python 覆盖省略／null 与显式数值的区分、上下界、数值兼容回退，以及原生工具与代理入口的 schema、默认值和实际分发一致性。

初次批次 `wb-dc58881b-b49a-4cba-86a9-d49080049bd8` 失败：并行 Watchdog 修改中的测试引用了未限定的 `LogLevel`。该处由并行工作修正；共享测试文件中重复的 `using System.Reflection` 在后续批次移除。初次失败不改写为成功，后续批次由 Server 合并并独立关联验证。

初次系统 Python 缺少依赖，最终改用项目现有虚拟环境，临时安装进程已停止。新增 Python 契约测试曾错误假定代理保留 schema 的显示标题及内嵌默认值；按现有代理契约分别检查默认值，并对其余语义字段保持精确比较后，84 项通过。

## 部署与现场调用

已有独立 AI 服务维护授权有效，维护前完整任务队列为空、无活动 Console Capture、测试已结束。维护 ID：`d0429155-38f7-4a68-8f26-6051524a193c`，终态 `succeeded`，Health 和只读验证通过。Server PID 从 `109724` 变为 `161852`；Unity PID 仍为 `127140`。新 Bridge 会话为 `98909acc83f346d0a52fd46c7abca761`，managed domain generation 为 3。

新建原生 HTTP MCP 会话重新读取 `tools/list`，核实 `durationSec` 可省略、允许 number/null、协议默认值为 null；高级设置的业务默认仍为 30 秒。将新配置临时设为 45 秒后，通过正式工具实测：

| 入口与参数 | 实际时长 | 终态 |
| --- | --- | --- |
| 原生，省略 | 45 秒 | Stopped |
| 原生，显式 30 | 30 秒 | Stopped |
| 原生，null | 45 秒 | Stopped |
| 代理，省略 | 45 秒 | Stopped |
| 代理，null | 45 秒 | Stopped |
| 代理，显式 12.5 | 12.5 秒 | Stopped |

第一项运行时把设置改成 60 秒，原 captureId 的 status 仍返回 45 秒。六项均使用原 captureId 停止并冻结耗时。最终新配置恢复为缺项状态，已加载访问器返回 30；完整队列为空，最后采集为 Stopped。

初次现场脚本误用反射兼容字符串结果，导致整数参数／偏好恢复绑定错误；没有重放不确定的 start。已将新偏好恢复默认，修正为 typed arguments 和 `resultValue.valueJson` 解码后重新完成上述六项，原偏好存在性和值的恢复检查通过。该问题属于验收脚本，未据此修改产品反射逻辑。

## 证据与覆盖边界

- [集中证据](../Tests~/UPilotTest/Log/UPilotValidation/ProfilerDuration_20261014_dc58881b/final-evidence.json)，SHA256：`79d07fc4c7a69dfc603e6b2c68dd0df0d34f8abdd796aad2f4dfc7e4d61987fa`。
- [现场证据](../Tests~/UPilotTest/Log/UPilotValidation/ProfilerDuration_20261014_dc58881b/live-evidence.json)，SHA256：`c10be4fd6dbe53b905bb2be6219b0208d142a2aea36836cb41d06da1151d73b0`；同目录保留修正后的客户端脚本及 JSON/CSV 产物。
- 未运行完整 EditMode、PlayMode、Unity 2022 或一小时实际持续采集。到期资源释放测试通过推进测试自有开始时间触发，不等同于长时间浸泡。
- 测试 Console 范围查询未观察到 Error，但 phase 级证据存在既有游标覆盖缺口，不宣称全量 Console 无错误。
- 加载证据来自新工具 schema、关联编译和实际请求结果，不代表全模块来源鉴证。原生 MCP 会话已拉取新工具列表；宿主应用已预注入的旧包装元数据无法由 Server 强制刷新，仍显示旧参数时需重新连接客户端。
