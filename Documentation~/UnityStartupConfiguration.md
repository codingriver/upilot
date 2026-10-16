# Unity 启动配置与播放中编译保护

Unity 启动后，Bridge 连接并认证成功，且连续稳定 EditMode 3 秒、没有编译或资源更新时，由现有主线程循环调用 `UPilotUnityConfiguration.CheckAndApplyStartupConfiguration()`。等待条件中断或连接身份改变会重新计时，不逐帧输出日志。

入口先写入 `SessionState` 已尝试标记，再按固定顺序处理配置；同一 Unity 会话即使失败，重连、Bridge/Server 重启或 Domain Reload 也不重复执行。重新打开 Unity 才开始下一次检查。此处不增加配置注册框架或独立延期队列。

1. 读取 `EditorPrefs.ScriptCompilationDuringPlay`，必要时改为 `1`（`RecompileAfterFinishedPlaying`），回读验证。该用户级偏好持久保存，不随 Bridge 关闭恢复。
2. 读取当前构建目标的宏，仅缺少精确 `UPILOT` 项时调用原维护函数补充，然后回读。构建目标未知视为失败。平台切换、包生命周期和显式 Flow 操作保留原入口及时机。

独立配置项失败后继续下一项。入口不切模式、不重启服务、不主动请求编译；宏变更可能引起 Unity 正常编译。设置验证成功不代表后续编译成功。

配置入口直接调用 `Debug.Log/LogError/LogException`，不经过 UPilot 日志过滤，不修改全局日志开关。Console 必须包含开始清单及目标、逐项开始和结果、结束汇总。结果区分“原值符合”“已修改并验证”“失败”“未执行”；失败说明读取/写入/回读阶段、期望值、可取得的实际值及原因，异常保留原始堆栈。失败或未执行不能统计为全部成功。

| MCP 操作 | 播放、暂停或模式切换时 |
|---|---|
| `unity_compile`、安全编译、自动修复内编译 | 主线程副作用前拒绝，返回 `EDITOR_IN_PLAY_MODE` |
| `unity_asset_refresh`、`unity_sync_after_disk_write` | 禁止全局 Refresh，包括 `triggerCompile=false`；安全编译附着现有编译的恢复语义保留 |
| `unity_script_create/update/delete` | 稳定播放/暂停可写盘，但每次先只读确认实际偏好为 1；错误或读取失败拒绝。模式切换期间拒绝写盘 |
| 代码资产 reimport/copy/move/通用 delete | 副作用前拒绝；`.cs/.asmdef/.asmref/.rsp/.dll` 及对应 `.meta`，目录按真实磁盘内容递归检查，复制/移动同时检查源和目标；分类失败拒绝 |
| 普通非代码资源操作 | 保留允许，仅导入明确目标，不附带全局 Refresh |
| WriteBatch 登记/查询 | 保留现有延期、原批次身份、合并轮次及期限，回到 EditMode 后沿用原编译流程 |

脚本写入检查与写盘都在同一主线程排队动作内，覆盖排队后进入播放的竞态；历史编译错误不阻止稳定 EditMode 保存修复。路径要求规范的项目内 `Assets/*.cs`，拒绝穿越和文件/目录链接。

播放中删除直接删除已校验 `.cs` 和存在的 `.meta`，不调用 AssetDatabase 删除、Refresh、ImportAsset 或项目文件同步。结果新增 `saved/changedPaths/deletedPaths/imported/importDeferred/projectFilesSynchronized`；`importDeferred=true` 只代表磁盘变更，不能声称已经导入、编译或同步项目文件。部分失败在错误 detail 中返回实际变化、失败阶段和异常信息，不回滚或自动重试。EditMode 删除仍走 AssetDatabase，项目文件同步结果独立返回。

整批保存/删除完成后仅登记一次 `unity_write_batch_register(paths, compileWhenEditMode=true, deletedPaths=实际已删除路径)`；仅代码删除时 `paths=[]`。用原 `writeBatchId` 观察相关终态，不追加 sync/compile 或自建延期队列。保留所有既有 MCP 名称和输入参数。

保护范围是 UPilot 自身受控操作，不拦截其他插件、用户代码或任意反射执行中的编译 API，不使用全局程序集重载锁。

验证使用隔离存储和虚拟时钟，覆盖启动边界、日志、配置回读、模式与排队竞态、延期保存/删除和部分失败、代码/目录分类；配合原宏与资源变更测试、Python WriteBatch/编译恢复回归。真实 Unity 冷启动与真实 PlayMode 现场覆盖单独记录，不用模拟测试代替。

## 定向验证记录（2026-10-15）

- 规范项目 `D:/upilot/Tests~/UPilotTest`，Unity `6000.6.0a2`。本任务包含配置入口、模式保护及原宏/资源操作的四个 fixture，最终 `runGuid=55e43ef4-ad0f-47c2-a81c-3a6b41fdae83`：54/54 通过，0 失败、0 跳过；`resultAuthoritative=true`、`cleanupSucceeded=true`。最终测试前后本任务 13 个 C# 文件 SHA256 未变。未运行全套 EditMode。
- 最后配置分类修正的自有批次 `wb-3ac19c69-01fc-49a6-9173-e8578beec3fb` 曾失败：并发新增的 `AutomationStepExecutor.Gizmos.cs` 两处 `GetInstanceID()` 被 Unity 6.6 作为 CS0619 错误拒绝；该失败保留，未将其改报成功，也未修改该并发文件。其修改方随后修复并完成官方批次 `wb-2a45a50b-ce26-487c-bff7-fa83fa2b7abf`，本任务复用此已覆盖最终源码的编译，没有再发起无代码变化的编译。
- 最终复核上述原批次：`terminal/errorsVerified/correlationVerified=true`，createdAt=`1792052825230`，verifiedAt=`1792052843826`；compile request=`req-6d888e30-5aa2-4484-b2af-6c9d2f57f4a8`，compile operation=`60a2f0927ea4441bb100e3a471a35336`。结构化编译结果 0 Error、4 条未修改文件警告（既有 AssemblyBuilder 弃用和 Flow Dictionary 序列化警告）。
- Python 定向文件 `test_playmode_compile_policy.py`、`test_write_batch_rounds.py`、`test_editor_execution_state_v2.py`、`test_v02_contracts.py`：283/283 通过，2 条既有 websockets 弃用警告；覆盖 refresh-only 拒绝、现有编译恢复、延期结果透传、原 WriteBatch 在返回 EditMode 后继续及共享轮次。首次新增测试替身缺少响应上下文，已补足替身并保留原断言。
- 配置异常测试有意输出红色 Error/Exception，并捕获验证原始异常堆栈；不能把这些预期日志视为未捕获的产品错误。编译 Console 区间查询返回 0 条 Error，但 coverage=`partial/console_cleared`；最终测试 Console 整体范围完整、阶段范围 partial，不能声称完整阶段 Console 无错。
- 实际冷启动、真实播放中脚本磁盘变更、真实目录链接/权限故障未现场执行，模式/时钟/存储与磁盘副作用使用隔离测试覆盖。本任务未重启 Unity 或 MCP Server；Python 为源码回归，未验证现有 Server 进程已重新加载这些 Python 修改。
- 根 `AGENTS.md` 已直接增加开发规则；Agent/Skill 模板、生成块及版本清单未修改。场景保存、Flow 报告收尾的冗余全局刷新已移除，Flow 迁移仅在主线程导入实际变更的 YAML 目标；平台切换、包生命周期和显式 Flow 宏开关入口保留。
