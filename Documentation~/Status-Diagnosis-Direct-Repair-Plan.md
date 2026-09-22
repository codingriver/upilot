# UPilot 状态诊断与直接修复

日期：2026-09-21  
状态：已开发，待用户手动验收  
范围：UPilot Editor / Bridge / Server 的项目无关服务管理能力。  
验证约束：只进行必要编译检查，不执行自动化测试、自动化验收或业务冒烟。

## 1. 本次方案变更

本文件将对话中的开发方案落盘。“确认空闲时自动交接”由以下规则替代：

> 检测到需要修复的部署异常，或用户点击自动修复、重新启动后，直接执行统一修复，不等待当前任务空闲，不弹出空闲确认。

正在运行的任务、测试、Console Capture 或编译不构成等待条件。修复可能中断这些工作；不自动重放、不自动补跑，不把中断标记为成功或已清理。

进程身份不明不是“等待空闲”，而是无法安全执行，应结束本轮并报告失败。不批量结束 Python、不修改端口绕过错误、不重启 Unity。

## 2. 必要性与归属

本次只解决通用产品自身的连接、来源一致性、进程生命周期和故障可见性。实现不引用任何项目业务类型，不理解 Case、关卡、战场、登录或业务成功条件。

不增加公共 MCP 工具、Case 执行器、Provider 调度框架、维护门禁或 `automation_start/status`。复用现有 QuickStart、Server Manager、重启持久化记录、HTTP 健康接口和 `editor.state` 只读命令。

`xclient2` 不需要修改业务代码。本次项目侧仅更新已有 TODO 的双向引用；业务桥迁移不属于本轮。

## 3. 改动清单

| 文件 | 职责 |
| --- | --- |
| `Editor/Core/UPilotDeploymentDiagnostics.cs` | 当前 Bridge 的包身份快照、明确 main 判断、实际路径归一化、逐项诊断与错误保留 |
| `Editor/Core/UPilotBridge.cs` | 持续暴露认证拒绝、缺失 hello、身份契约不匹配及发生时间；成功握手后清除 |
| `Editor/Core/UPilotMcpServerManager.cs` | 30 秒状态期限、代次隔离、精确停止、端口释放、来源及只读往返验证 |
| `Editor/Core/UPilotQuickStart.cs` | 自动与手动修复合并、故障单次尝试、失败保留、一次弹窗、重载后的只观察恢复 |
| `Editor/Core/UPilotServerRestartDiagnostics.cs` | 保存 PID、创建时间、失败阶段、部署校验和只读校验结果 |
| `Editor/Core/UPilotServerRuntimeService.cs` | 按当前包精确版本准备 Server，保留自定义入口；统一修复错误弹窗归属 |
| `Editor/Core/UPilotMainWindow.cs` | 异常优先、可展开详情、长文本换行和重新启动按钮 |
| `Editor/Core/UPilotStatusWindow.cs` | 高级窗口修复入口与主窗口一致，不使用换端口和单独重连 Bridge 兜底 |
| `Editor/Core/UPilotMainWindow.Setup.Agents.cs` | 首次设置等待完整修复终态，不以版本号返回替代成功 |
| `Editor/Core/UPilotPackageUpdateLifecycle.cs` | 独立 added/removed、重新安装、来源切换接入统一修复；保留跨重载意图 |
| `upilotserver~/src/upilot_mcp/version.py` | 启动时固定版本、渠道、实例 ID、入口与实际模块路径 |
| `upilotserver~/src/upilot_mcp/server.py` | hello 携带启动身份快照 |
| `upilotserver~/src/upilot_mcp/mcp_stdio_server.py` | 既有 `/health` 增加身份字段和可选只读往返验证；`/stats` 使用同一版本快照 |

## 4. 状态诊断契约

每项诊断有独立代码、错误文本及是否已明确证实的标记。检查项包括认证、状态查询、端口进程归属、健康接口、版本、渠道、协议、配置入口、实际入口、模块路径、项目和运行实例。

- 明确异常立即显示，不等待 30 秒。缺失值显示“未知/未返回”，不能推断为匹配或不匹配。
- 缺少旧 Server 不支持的身份字段，明确报告协议字段缺失，不永久显示获取状态中。
- 对应检查成功才移除既有问题。新一轮查询缺失字段不覆盖已知错误；刷新缓存不清空错误。
- 主状态栏显示首要错误；展开详情查看其余错误、双方版本、渠道、安装来源、路径、项目、PID、实例及最后成功状态时间。
- 修复期间保留触发原因并显示阶段。后台普通观察不反复弹窗。
- 状态失败、状态超时、修复失败均保留“重新启动”。修复执行中显示“重启中”，重复点击合并。

状态获取有一个共用的有界任务。期限包含加入正在进行的刷新、端口检查、进程识别、HTTP 获取和汇总；新查询加入现有任务不会重新获得 30 秒。缓存代次不匹配的旧请求不能提交结果。

观察窗口另有不随 Repaint、轮询或缓存失效重置的 30 秒期限，覆盖端口已起但握手一直未完成的情况。

## 5. 版本和来源

- Server 目标是与当前安装 Bridge 配套，不是线上最新版本，不升级 UPM。
- 非 main 产品版本严格相等，不接受较高版本兜底。
- Git 仅精确 `#main`，Local/Embedded 仅实际检出的 Git HEAD 为 `main`，才豁免产品版本相等。不能确认分支时不豁免。
- main 仍检查渠道、入口、实际模块、项目、进程和协议。
- 安装方式和渠道分别显示。Local/Embedded 是安装方式，不是 main 标志。
- Git 的明确语义版本 ref 归为 release；其他 Git 源码 ref 归为 source，但只有精确 main 获得版本相等豁免。
- Python 自动管理入口来自已加载程序集所属的 UPM `resolvedPath`，不扫描并选择任意旧 PackageCache。旧版本自动生成的当前工程 PackageCache 入口支持迁移。
- 自定义 Python/EXE 入口与配套来源不一致时保留配置并明确提示修改，不静默替换。
- Windows 路径比较解析最终文件路径，避免目录联接或符号链接的字符串差异误判。
- EXE 从当前包版本对应的发布清单准备，校验 UPM 版本、Server 版本、渠道、协议及既有下载 SHA256；不请求 `releases/latest`。
- 版本信息在 Server 模块启动时捕获一次。后续请求不会通过重新读取已修改的磁盘文件冒充运行实例版本。

这些身份信息用于部署一致性诊断，不是任意 Python 热加载模块的完整加密来源证明，也不宣称整个运行进程的所有代码都经过哈希证明。

## 6. 统一修复流程

1. 获取新状态，核对当前项目、精确进程与当前包来源。其他项目或无法识别的进程直接失败。
2. 准备当前包的配套 Python 入口或 EXE。准备失败不停止当前服务。
3. 停止 Bridge，重新读取并核对 Server PID、创建时间及带当前项目日志路径和端口的命令行。
4. 持有目标进程句柄，结束已验证的当前项目 Server，不等待业务任务结束。
5. 确认旧进程退出且两个端口释放，随后启动配套 Server，再启动 Bridge。
6. 核对新进程、项目、渠道、入口、模块来源、协议、版本规则和新的 Bridge 会话。
7. 经既有 `/health?probe=bridge` 发起一次 `editor.state` 只读命令；验证响应、请求 nonce、新会话和 Server 实例相符。
8. 只有全部通过才记录成功。任何阶段失败结束本轮，保存失败阶段并弹出一次可滚动、可复制的详情窗口。

超时边界：

| 阶段 | 期限 |
| --- | --- |
| 单次完整状态采集及未完成观察 | 30 秒 |
| 普通 HTTP 状态请求 | 沿用 2 秒 |
| 进程候选归属扫描 | 8 秒 |
| 旧进程退出及端口释放观察 | 沿用 4 秒 |
| 替换进程启动后的完整验证 | 沿用 20 秒，不因 Domain Reload 重新起算 |
| 验证用只读命令 | Server 5 秒，HTTP 客户端 7 秒 |
| 配套 EXE 准备 | 5 分钟上限，沿用有界下载与安装能力 |

失败后的自动尝试锁按工程保留，普通轮询、窗口重开或 Domain Reload 不解除锁定。用户点击可以开始新一轮。后台观察到所有检查恢复后，仍需真实只读往返成功，才能清除失败并重新允许后续故障的自动修复。

失败文本和最后已弹窗的修复 ID 按工程保存在 EditorPrefs，避免编辑器重新打开后再次弹出同一次历史失败；新的人工修复仍有独立失败通知。

重载发生在准备阶段且没有新进程身份时，报告需要人工重新启动，不重放停启。已有新进程身份时，只恢复观察并核对 PID 的创建时间。原业务任务身份和证据不由修复流程删除或改写。

## 7. 包生命周期

- 独立移除：在旧包尚可执行时停止当前项目服务，保留重装后的检查意图。
- 独立添加、重装、release/source 切换：加载后进入同一配套检查与修复入口。
- 包生命周期的失败不表示业务任务已清理。
- 普通脚本 Domain Reload 没有包变更意图时，不无条件重启 Server；只有观察到实际异常才按单次自动修复策略处理。

## 8. 手动验收清单

以下均由用户手动执行，本轮未运行对应自动化用例。

| 检查场景 | 预期 |
| --- | --- |
| 非 main，版本不同 | 显示双方版本；准备并启动当前 Bridge 配套 Server |
| 明确 main，产品版本不同 | 不仅因产品版本不同报错，仍检查其余身份项 |
| Local/Embedded 非 main 或分支未知 | 不豁免版本相等 |
| 渠道不一致 | 显示双方渠道和安装来源，不能以版本相同显示就绪 |
| 旧 hello 缺少身份契约字段 | 立即显示缺失字段或协议不兼容，不持续获取状态中 |
| 旧 PackageCache 入口仍存在 | 自动管理入口按当前包解析，实际旧进程被识别并修复 |
| 自定义入口错误 | 不覆盖路径；明确说明配置入口、配套入口及下一步 |
| /health 失败或无响应 | 显示失败阶段，30 秒内提供可操作的重新启动 |
| 持续刷新或 Repaint | 不延长当前观察期限，不让旧请求覆盖新结果 |
| 任务、测试、采集或编译中点击重启 | 不等待业务空闲；不自动补跑或声明清理完成 |
| 双击重启、两个窗口同时修复 | 合并为同一轮，不重复结束或启动进程 |
| 其他项目或未知进程占用 | 直接失败，不结束该进程，不自动换端口 |
| 停服、启动、认证或只读验证失败 | 每轮只弹一次失败窗口；保留错误和重新启动 |
| 自动修复失败后继续轮询、重开窗口、重载 | 不无限自动重启；用户点击可以重试 |
| 一切恢复 | 新进程、新会话和真实只读结果通过后才成功 |
| 包 Remove/Add 与来源切换 | 不遗留旧入口进程；普通脚本重载不造成无条件重启 |

## 9. 编译与交付证据

规范编译项目为 `D:\upilot\Tests~\UPilotTest`，不以外部业务工程替代包编译证据。

第一批 `writeBatchId=wb-bcd48484-d69c-48a3-9df8-c57207dd9e18`，Unity 返回 `phase=completed`、`terminal=true`、`errorsVerified=true`、编译错误 0。三条警告均位于既有 `UPilot.Flow.Models.cs`，为字典字段的 UAC1009 序列化警告。证据：`Tests~/UPilotTest/log/P0P1/repair-compile-warnings-1.json`。

第二批 `writeBatchId=wb-96ca8f3d-1ffb-4711-ac80-57244f2417da`，Unity 返回已验证完成、编译错误 0，三条警告仍为上述既有 Flow 警告。证据：`Tests~/UPilotTest/log/P0P1/repair-compile-errors-2.json`。

最终增量批次：

- `writeBatchId=wb-955bb361-e6bd-4e09-90bf-bf03d0e89878`
- `compileOperationId=8fa5541e064845e887a8270c849e5c8e`
- `writeBatchCreatedAt=1789992499281`
- `lastCompileVerifiedAt=1789992514350`，晚于批次创建时间。
- `phase=completed`、`terminal=true`、`errorsVerified=true`、编译错误 0、本批警告 0。
- 证据：`Tests~/UPilotTest/log/P0P1/repair-compile-errors-final.json`；7561 bytes；SHA256 `51bd0e1231bbddd3ed5507e38ee48469891d99410f2b11a2a9615c781539f320`。

最终针对 UPilot 的 Error Console 查询为 0 匹配，证据：`Tests~/UPilotTest/log/P0P1/repair-console-errors-final.json`。这是有界关键词查询，不宣称整个工程 Console 没有其他历史问题。

Python 三个修改文件通过 `py_compile` 语法编译，`git diff --check` 通过；修改的 C# 均保留 UTF-8 BOM。未执行单元测试、EditMode/PlayMode 测试、自动化验收或业务冒烟。

开发现场观察：第一批编译重载后，规范工程的新诊断识别到旧 Server 缺少启动身份字段，产品自动修复完成了一次换代（PID 11004 -> 36332），持久化记录包含部署和只读验证完成；之后两次普通代码重载中 Server PID 保持 36332。此记录仅说明当时发生的产品行为，不构成完整场景验收，也不替代上面的用户手动清单。

根 TODO：`D:\upilot\TODO_UPilot.mcd` / “部署新鲜度证据与备用端点客户端（2026-09-21）”。  
项目 TODO：`F:\xclient2\TODO_UPilot.mcd` / “本地 UPM 切换后的 Server/Bridge 协议不匹配诊断”。
