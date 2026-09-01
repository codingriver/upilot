// -----------------------------------------------------------------------
// UPilot Editor - MonoHook tracing contract implementations.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Reflection;
using MonoHook;

namespace CodingRiver.UPilot
{
    internal sealed class UPilotMonoHookEventSink : IUPilotMonoHookEventSink
    {
        public long Publish(UPilotMonoHookEvent hookEvent)
        {
            if (hookEvent == null) return 0;
            return UPilotMonoHookInstallationService.Publish(hookEvent);
        }
    }

    internal sealed class UPilotMonoHookFactory : IUPilotMonoHookFactory
    {
        public IUPilotMonoHookHandle Install(MethodBase target, MethodInfo replacement, MethodInfo proxy, string ownerId)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (replacement == null) throw new ArgumentNullException(nameof(replacement));

            var existing = HookPool.GetHook(target);
            if (existing != null && existing.isHooked)
                throw new InvalidOperationException("目标方法已被其他 MonoHook 占用：" + target.DeclaringType + "." + target.Name);

            var tag = string.IsNullOrEmpty(ownerId) ? "UPilot.MonoHook.Custom" : ownerId;
            var hook = new MethodHook(target, replacement, proxy, tag);
            hook.Install();
            if (!hook.isHooked) throw new InvalidOperationException("MonoHook 未安装：" + tag);
            return new UPilotMonoHookHandle(hook);
        }
    }

    internal sealed class UPilotMonoHookHandle : IUPilotMonoHookHandle
    {
        private readonly MethodHook _hook;

        public bool IsInstalled => _hook != null && _hook.isHooked;

        public UPilotMonoHookHandle(MethodHook hook)
        {
            _hook = hook ?? throw new ArgumentNullException(nameof(hook));
        }

        public void Uninstall() => _hook.Uninstall();
        public void Dispose() => Uninstall();
    }
}
