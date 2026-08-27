# 自定义 MonoHook 点位教程

本文说明如何为 UPilot MonoHook Tracing 增加一个由 C# 特性自动发现、在窗口中手动启用的自定义点位。

自定义点位默认关闭。代码编译完成后，它只会出现在 `UPilot > Advanced > MonoHook` 列表中；必须由用户勾选并点击“应用”，才会安装 Hook。

## 1. 创建 Editor 程序集

Provider 必须位于 Editor 程序集中，并引用公共契约程序集：

```json
{
  "name": "MyProject.MonoHook.Tracing.Editor",
  "rootNamespace": "MyProject.EditorTracing",
  "references": [
    "UPilot.MonoHook.Tracing.Contracts.Editor"
  ],
  "includePlatforms": [
    "Editor"
  ],
  "defineConstraints": [
    "!UPILOT_DISABLE_MONOHOOK_TRACING"
  ],
  "autoReferenced": true,
  "noEngineReferences": false
}
```

如果项目已有 Editor asmdef，只需增加 `UPilot.MonoHook.Tracing.Contracts.Editor` 引用。自定义程序集不需要直接引用底层 `UPilot.MonoHook.Editor`。

## 2. 添加完整自定义点位

下面的示例追踪一个静态 `SetValue(int)` 方法。把代码放在上述 Editor 程序集内，例如 `Editor/Tracing/CustomSetValueHookPoint.cs`。

```csharp
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using CodingRiver.UPilot;
using UnityEditor;
using UnityEngine;

namespace MyProject.EditorTracing
{
    [UPilotMonoHookPoint(
        "SetValue",
        "myproject.custom",
        CategoryDisplayName = "My Project",
        CategoryOrder = 1000,
        Order = 10,
        DefaultEnabled = false)]
    internal sealed class CustomSetValueHookPoint : UPilotMethodHookPointBase
    {
        private static readonly string PointId =
            UPilotMonoHookPointIdentity.FromProviderType(
                typeof(CustomSetValueHookPoint));
        private static readonly BindingFlags Flags =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly MethodInfo TargetMethod =
            typeof(CustomTraceTarget).GetMethod(nameof(CustomTraceTarget.SetValue), Flags);

        private static IUPilotMonoHookEventSink _eventSink;

        public override UPilotMonoHookSupport CheckSupport(UPilotMonoHookContext context)
        {
            if (TargetMethod == null)
                return UPilotMonoHookSupport.Unsupported("找不到 CustomTraceTarget.SetValue(int)");

            if (TargetMethod.IsAbstract || TargetMethod.ContainsGenericParameters)
                return UPilotMonoHookSupport.Unsupported("目标方法不能是抽象方法或开放泛型方法");

            var implementation = TargetMethod.GetMethodImplementationFlags();
            if ((implementation & (MethodImplAttributes.InternalCall |
                                   MethodImplAttributes.Native |
                                   MethodImplAttributes.Runtime)) != 0)
            {
                return UPilotMonoHookSupport.Unsupported("目标不是安全的托管 IL 方法");
            }

            try
            {
                var il = TargetMethod.GetMethodBody()?.GetILAsByteArray();
                if (il == null || il.Length < 10)
                    return UPilotMonoHookSupport.Unsupported("目标方法体过短或无法读取");
            }
            catch (Exception ex)
            {
                return UPilotMonoHookSupport.Unsupported("无法检查目标方法体：" + ex.Message);
            }

            return UPilotMonoHookSupport.Supported();
        }

        protected override IEnumerable<UPilotMonoHookBinding> CreateBindings(
            UPilotMonoHookContext context)
        {
            _eventSink = context.EventSink;

            yield return new UPilotMonoHookBinding(
                TargetMethod,
                typeof(CustomSetValueHookPoint).GetMethod(nameof(SetValueReplacement), Flags),
                typeof(CustomSetValueHookPoint).GetMethod(nameof(SetValueProxy), Flags),
                "MyProject.MonoHook.SetValue");
        }

        protected override void UninstallCore(UPilotMonoHookContext context)
        {
            base.UninstallCore(context);
            _eventSink = null;
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static int SetValueReplacement(int value)
        {
            int result = SetValueProxy(value);

            _eventSink?.Publish(new UPilotMonoHookEvent
            {
                pointId = PointId,
                kind = "myproject.custom.set-value",
                phase = "after",
                frame = Time.frameCount,
                componentType = typeof(CustomTraceTarget).FullName,
                beforeValue = value.ToString(),
                afterValue = result.ToString(),
            });

            return result;
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static int SetValueProxy(int value)
        {
            throw new InvalidOperationException(
                "SetValueProxy must only be called through MonoHook.");
        }
    }

    internal static class CustomTraceTarget
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int SetValue(int value)
        {
            int normalized = value + 1;
            normalized ^= 0x35;
            return normalized ^ 0x35;
        }
    }

    internal static class CustomTraceDemoMenu
    {
        [MenuItem("Tools/MonoHook Demo/Call SetValue")]
        private static void CallSetValue()
        {
            Debug.Log("SetValue result: " + CustomTraceTarget.SetValue(10));
        }
    }
}
```

`SetValueProxy` 的方法体不会被正常直接执行。Hook 安装后，Replacement 通过 Proxy 调用原始目标；如果业务代码直接调用 Proxy，它抛出异常是预期保护行为。

## 3. 特性字段

`UPilotMonoHookPointAttribute` 负责把 Provider 自动加入窗口列表：

| 字段 | 用途 |
| --- | --- |
| `Id` | 可选显式稳定 ID；省略时由 Provider 类型生成 |
| `DisplayName` | 点位在窗口中的名称 |
| `Category` | 稳定的分类 ID |
| `CategoryDisplayName` | 分类显示名称 |
| `DefaultEnabled` | 是否默认勾选；诊断点位应保持 `false` |
| `HighFrequency` | 标记高频点位，例如 Update 或频繁 setter |
| `Order` | 分类内排序 |
| `CategoryOrder` | 分类排序 |

省略 ID 时，最终格式为：

```text
provider:<程序集简单名>:<Provider 命名空间.类名>
```

例如：

```text
provider:MyProject.MonoHook.Tracing.Editor:
MyProject.EditorTracing.CustomSetValueHookPoint
```

使用 `UPilotMonoHookPointIdentity.FromProviderType(typeof(CustomSetValueHookPoint))` 可以取得与 Registry 完全一致的值，适合填写事件的 `pointId`。

如果点位来自长期发布的包，或者需要在重命名 Provider 后继续复用原有 Settings、MCP pointIds 和诊断数据，应显式声明稳定 ID：

```csharp
[UPilotMonoHookPoint(
    "SetValue",
    "myproject.custom",
    Id = "myproject.custom.set-value")]
```

现有三参数写法继续兼容：

```csharp
[UPilotMonoHookPoint(
    "myproject.custom.set-value",
    "SetValue",
    "myproject.custom")]
```

Provider 必须满足以下条件：

- 是非抽象类。
- 实现 `IUPilotMonoHookPointProvider`，通常直接继承公共基类。
- 提供无参构造方法，非 public 构造方法也可以。
- 点位 ID 不与其他 Provider 重复。

程序集编译或 Domain Reload 后，UPilot 会通过 `TypeCache` 自动发现带特性的 Provider，不需要手工维护注册列表。

## 4. 选择公共基类

通常优先继承 `UPilotMethodHookPointBase`：

- 实现 `CreateBindings`，返回一个或多个 `UPilotMonoHookBinding`。
- 基类统一管理 Hook handle、安装失败清理和逆序卸载。
- `UninstallCore` 重写时必须调用 `base.UninstallCore(context)`。

只有在不适合使用方法绑定、需要自行管理其他资源时，才直接继承 `UPilotMonoHookPointBase` 并实现 `InstallCore`、`UninstallCore`。

也可以直接实现 `IUPilotMonoHookPointProvider`，但这意味着安装状态、重复安装、异常清理和卸载都需要自行处理。

## 5. Target、Replacement 和 Proxy 签名

三者的参数与返回值必须严格匹配。

静态目标：

```csharp
// Target
static int SetValue(int value)

// Replacement / Proxy
static int SetValueReplacement(int value)
static int SetValueProxy(int value)
```

实例目标需要在 Replacement 和 Proxy 的第一个参数显式接收实例：

```csharp
// Target
void SetValue(int value)

// Replacement / Proxy
static void SetValueReplacement(MyComponent __this, int value)
static void SetValueProxy(MyComponent __this, int value)
```

其他规则：

- 重载方法必须用明确参数类型调用 `GetMethod`，不要只按方法名匹配。
- Replacement 和 Proxy 建议添加 `MethodImplOptions.NoOptimization`。
- 自己控制的 Target 建议添加 `MethodImplOptions.NoInlining`，并避免过短的方法体。
- 需要执行原始逻辑时提供 Proxy；不需要调用原始逻辑时可以传 `null`，但要明确这会完全替代目标行为。
- 不要尝试绕过 `InternalCall`、Native、Injected、抽象、开放泛型或不可读 IL 的 Unsupported 结果。
- 同一个目标方法不能同时被多个 MonoHook 占用。

## 6. 发布事件

通过 `context.EventSink` 发布 `UPilotMonoHookEvent`。建议至少填写：

- `pointId`：必须与 Registry 的最终 ID 一致；隐式 ID 使用 `UPilotMonoHookPointIdentity.FromProviderType` 获取。
- `kind`：更细的事件类型，可与 pointId 相同。
- `phase`：例如 `before`、`after` 或 `exception`。
- `frame`：需要帧信息时填写 `Time.frameCount`。
- `componentType`：目标类型全名。
- `beforeValue`、`afterValue`：修改前后值。

实例目标还可以根据实际对象填写 `objectName`、`instanceId`、`hierarchyPath` 和 `scenePath`。

EventSink 会统一处理事件序号、UTC 时间、事件速率限制、未变化值抑制、按点位堆栈采样和可选 Console 输出。自定义 Provider 不应直接调用 `Debug.Log` 代替事件发布。

## 7. 手动验收

1. 等待程序集编译和 Domain Reload 完成。
2. 打开 `UPilot > Advanced > MonoHook`。
3. 在 `My Project` 分类中确认出现 `SetValue`，且默认未启用。
4. 勾选点位并点击“应用”。只勾选但不应用不会安装 Hook。
5. 执行 `Tools > MonoHook Demo > Call SetValue`。
6. 在事件日志中确认出现 `myproject.custom.set-value`，值从 `10` 变为 `11`。
7. 如需调用堆栈，开启该点位右侧的堆栈选项，再次调用菜单。
8. 取消勾选并点击“应用”，再次调用菜单，确认不再产生该点位事件。

测试结束后应恢复原配置。不要默认开启自定义点位、堆栈或 Console 输出。

## 8. 常见问题

### 点位没有出现在列表中

- 检查程序集是否为 Editor 程序集。
- 检查 asmdef 是否引用 `UPilot.MonoHook.Tracing.Contracts.Editor`。
- 检查是否定义了 `UPILOT_DISABLE_MONOHOOK_TRACING`。
- 检查 Provider 是否带 `UPilotMonoHookPointAttribute`、是否为非抽象类、是否有无参构造方法。
- 先处理项目中的编译错误，再等待 Domain Reload。

### 显示点位 ID 重复

搜索所有 `UPilotMonoHookPointAttribute`，为每个 Provider 使用全局唯一且稳定的 ID。不要通过修改显示名称解决 ID 冲突。

### 显示 Unsupported 或安装失败

- 确认反射找到了正确重载。
- 确认 Target 是可读取的托管 IL，且方法体不是过短包装层。
- 确认 Replacement、Proxy 的返回值和参数完全匹配。
- 实例方法不要遗漏第一个 `__this` 参数。
- 检查目标是否已被另一个 MonoHook 占用。
- 不要改用 Native、Injected 或 reflection-eval 等方式强行绕过安全检查。

### Hook 已安装但没有事件

- 确认调用的是被绑定的准确重载。
- 确认窗口中已经点击“应用”，而不是只保存了勾选状态。
- 确认事件没有被全局速率限制或“忽略未变化值”过滤。
- 对自己控制的目标使用 `NoInlining`，避免调用被内联后绕过 Hook 入口。

## 9. 参考实现

Package Manager 中的 `MonoHook Tracing Custom Provider` Sample 是最小权威示例，主源位于：

```text
Samples~/MonoHookTracing/
```

公共扩展契约位于：

```text
Editor/Optional/MonoHook.Tracing.Contracts/
```
