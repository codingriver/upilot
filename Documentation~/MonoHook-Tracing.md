# UPilot 追踪器

UPilot 追踪器是可选的 Editor 诊断模块，用于手动选择并安装 Unity 方法追踪点位。底层使用 MonoHook 技术栈，程序集仍保留 `UPilot.MonoHook.Tracing.Editor`；扩展契约位于 `UPilot.MonoHook.Tracing.Contracts.Editor`，MCP 薄适配位于 `UPilot.MonoHook.Tracing.Mcp.Editor`。

## 使用方式

在 Unity 中打开：

```text
UPilot > Advanced > 追踪器
```

所有内置点位默认关闭。勾选点位只修改项目配置，点击“应用”后才会实际安装或卸载 Hook。“卸载全部”只卸载当前 Hook，不修改已保存的点位选择。

`Instantiate`、`Destroy`、`SetParent`、`Translate`、`Rotate`、`RotateAround` 和 `LookAt` 提供“全部安全重载”选项，默认关闭。推荐模式按当前 Unity 版本选择能够覆盖独立调用族的最小安全集合，避免包装方法转发产生同点位重复日志；开启后会尝试安装所有通过安全检查的公开非泛型重载。`RotateAround` 的全部模式还会尝试覆盖仍可执行的过时二参数入口。修改该选项后点位显示“未应用”，点击“应用”才会按新策略重新安装。该选项不会绕过 Native、`InternalCall`、Injected、开放泛型或其他不安全目标检查，实际安装数量以点位覆盖诊断为准。

配置保存在：

```text
ProjectSettings/UPilotMonoHookSettings.asset
```

Domain Reload 后自动应用默认关闭，可在窗口的“运行保护”区域手动开启。

生命周期点位可以按程序集、命名空间和类型分别设置包含/排除范围。规则支持逗号、分号或换行分隔，并支持 `*`、`?` 通配符；空包含规则表示不过滤，排除规则优先。范围修改后再次点击“应用”，已安装的生命周期 Hook 会按新范围重新安装。

## 目标与事件过滤

目标过滤器支持按“对象 + 上下文 + 事件”组合筛选。单条规则内条件按 AND 组合，多条包含规则按 OR 组合，排除规则优先；过滤器默认关闭，不改变现有点位选择。

- 对象：场景对象、资源、Prefab Stage、Editor 临时对象、对象类型、派生类型、必需组件。
- 标识：GameObject 名称、完整 Hierarchy、父节点名称、任意祖先节点名称、最大层级深度、Scene、资源路径、Layer、Tag、Prefab 状态。
- 关系与身份：根对象、直接子节点、必需组件 enabled 状态、Prefab 来源路径、会话 InstanceID、GlobalObjectId、事件来源模式。
- 范围：当前选中对象、当前选中对象子树。
- 事件：点位模式、方法签名模式、`before/after` 阶段、EditMode/PlayMode。
- 值：值是否改变、修改前后内容、字符串包含、数值变化阈值。

追踪器还提供全局事件限流、可选单对象限流和可选重复事件抑制；新增低噪声选项默认关闭。它们产生的丢弃计数与过滤拒绝独立统计。

点位行可以单独绑定过滤器，未绑定时继承全局过滤器。对象名称和 Hierarchy 条件属于事件级过滤：底层方法可能仍被 Hook，但事件会在堆栈采集、缓冲和 Console 输出之前被拒绝；生命周期类型条件在满足条件时还可以缩小实际安装候选。

InstanceID 只适合当前 Unity 会话；需要长期保存时优先使用名称、Hierarchy、Prefab 路径或 GlobalObjectId。内置事件来源为 `EditMode`/`PlayMode`，不等同于调用者堆栈。

常用预设包括“场景业务对象”“当前选中及子树”和“排除 Editor 临时对象”。建议先用预览/统计确认命中结果，再启用高频点位；不要默认打开正则、完整堆栈或全部重载。

例如，筛选 Player 子树中禁用的 BoxCollider：

```json
{
  "NameMatchMode": 2,
  "NamePattern": "Player",
  "HierarchyMatchMode": 4,
  "HierarchyPattern": "Gameplay/Player",
  "RequiredComponentTypeName": "UnityEngine.BoxCollider",
  "RequiredComponentEnabledState": 2
}
```

其中枚举值以 C# 枚举为准，实际使用建议直接在界面编辑或导出预设，不要手写枚举数字。

## 当前点位

- 生命周期：`Awake`、`OnEnable`、`Start`、`Update`、`FixedUpdate`、`LateUpdate`、`OnDisable`、`OnDestroy`
- GameObject：`Instantiate`、`Destroy/DestroyImmediate`、`SetActive`、`AddComponent(Type)`
- Component：在 Unity 版本提供安全托管 setter 时支持 `Behaviour.enabled`、`Renderer.enabled`、`Collider.enabled`、`Collider2D.enabled`
- Transform：坐标、旋转、缩放 setter，组合设置、父子层级，以及经安全检查可安装的 `Translate`、`Rotate`、`RotateAround`、`LookAt`、Sibling 和 Detach 操作

生命周期安装会跳过方法体过短、抽象、泛型或无法读取方法体的目标。窗口使用“部分覆盖”状态显示安装数、跳过数和失败数，悬停状态文本可查看少量样例。

## 事件与导出

窗口最多显示最近 100 条事件，内存缓冲默认保留 2048 条。事件包含：

- sequence、UTC 时间和帧号
- 点位 kind 与执行阶段
- 稳定 pointId
- 对象名、实例 ID、层级路径和 Scene 路径
- 组件类型、目标类型、GlobalObjectId 和事件来源
- 实际命中的方法签名
- 修改前后的值
- 可选调用堆栈

窗口支持文本筛选、清空和事件 JSONL 导出。“导出诊断”会为每个点位输出配置状态、安装状态、candidate/installed/skipped/failed 统计和最多 5 条样例。调用堆栈按点位单独开启，默认全部关闭；全局只提供最大帧数和每 N 条采样一次两个保护参数。

“事件日志”标题右侧提供“输出到 Console”开关，默认关闭且切换后立即生效，不需要重新应用追踪点位。Console 日志使用固定 `[UPilot][Trace]` 前缀并包含 pointId、阶段、帧号、Scene、对象层级、组件类型、实际方法签名和修改前后值；开启点位堆栈后会追加 `Hook caller`。Console 使用独立的每秒日志上限，超过上限只丢弃 Console 输出，不影响内存事件和 JSONL 导出。

## 自定义点位

完整的程序集配置、可运行示例、签名规则和排错步骤见 [自定义 UPilot 追踪点位教程](MonoHook-Custom-Point-Tutorial.md)。

自定义 Editor 程序集需要引用：

```text
UPilot.MonoHook.Tracing.Contracts.Editor
```

然后通过特性和公共基类声明点位：

```csharp
[UPilotMonoHookPoint(
    "Example",
    "custom",
    CategoryDisplayName = "Custom",
    DefaultEnabled = false)]
internal sealed class ExampleHookPoint : UPilotMethodHookPointBase
{
    protected override IEnumerable<UPilotMonoHookBinding> CreateBindings(
        UPilotMonoHookContext context)
    {
        yield return new UPilotMonoHookBinding(target, replacement, proxy);
    }
}
```

省略 ID 时，Registry 会使用程序集简单名和 Provider 完整类型名生成 `provider:<assembly>:<namespace.type>`。发布事件时使用 `UPilotMonoHookPointIdentity.FromProviderType(typeof(ExampleHookPoint))` 可取得相同 ID。需要跨类名、命名空间或程序集重构保持配置兼容时，仍应通过三参数构造函数或 `Id = "stable.id"` 显式指定稳定 ID。

Provider 必须具有无参构造方法。最终点位 ID 必须全局唯一；重复 ID、无法创建的 Provider 和不安全目标会在窗口中显示明确状态。

Package Manager 的 Samples 中提供 `UPilot Tracer Custom Provider` 示例，导入后会自动发现 `Sample Custom / Sample SetValue` 点位；该示例同样默认关闭。

## MCP 辅助控制

MCP 仅复用手动管理的 Settings、Controller 和 Telemetry，不复制安装逻辑：

- `unity_monohook_tracing_status`
- `unity_monohook_tracing_configure`
- `unity_monohook_tracing_events`

`configure.apply` 默认 `false`，默认只保存配置，不安装或卸载 Hook；设置为 `true` 时仍通过现有 Controller 应用，不能绕过 Unsupported 或安全检查。

## 编译禁用

如项目完全不需要该可选功能，可定义：

```text
UPILOT_DISABLE_MONOHOOK_TRACING
```

该宏会同时排除 Contracts、Tracing、MCP Adapter、Tests 和 Sample，但不会禁用底层 `UPilot.MonoHook.Editor` 插件程序集。

## 安全边界

- 所有点位默认关闭，不自动扩大追踪范围。
- 不强制 Hook Unity 6 的不安全 Native/Injected 入口。
- 不同 Unity 版本的托管包装层不同；被识别为 `InternalCall`、短方法或不可读方法体的点位会显示 Unsupported，并在专项测试中以明确原因条件跳过。
- 程序集重载和 Unity 退出前会卸载已安装 Hook。
- 只有显式开启“Domain Reload 后自动应用”时，保存的启用点位才会在重载完成且 Editor 恢复就绪后重新安装。
- 高频 Transform 点位应结合事件速率限制使用。
- 高频 Update 系列点位同样默认关闭，应结合事件速率限制使用；调用堆栈建议保持关闭或提高采样间隔。
