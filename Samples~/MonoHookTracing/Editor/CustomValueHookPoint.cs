// -----------------------------------------------------------------------
// UPilot sample - attribute-discovered custom MonoHook point.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace CodingRiver.UPilot.Samples.MonoHookTracing
{
    [UPilotMonoHookPoint(
        "sample.custom.value",
        "Sample SetValue",
        "sample.custom",
        CategoryDisplayName = "Sample Custom",
        CategoryOrder = 1000,
        Order = 10)]
    public sealed class CustomValueHookPoint : UPilotMethodHookPointBase
    {
        private static IUPilotMonoHookEventSink _eventSink;

        protected override IEnumerable<UPilotMonoHookBinding> CreateBindings(UPilotMonoHookContext context)
        {
            _eventSink = context.EventSink;
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            yield return new UPilotMonoHookBinding(
                typeof(MonoHookTracingSampleTarget).GetMethod(nameof(MonoHookTracingSampleTarget.SetValue), flags),
                typeof(CustomValueHookPoint).GetMethod(nameof(SetValueReplacement), flags),
                typeof(CustomValueHookPoint).GetMethod(nameof(SetValueProxy), flags),
                "UPilot.MonoHook.Sample.SetValue");
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
                pointId = "sample.custom.value",
                kind = "sample.custom.setValue",
                phase = "after",
                componentType = typeof(MonoHookTracingSampleTarget).FullName,
                beforeValue = value.ToString(),
                afterValue = result.ToString(),
            });
            return result;
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static int SetValueProxy(int value)
        {
            throw new InvalidOperationException("SetValueProxy must only be called through MonoHook.");
        }
    }

    public static class MonoHookTracingSampleTarget
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int SetValue(int value)
        {
            int normalized = value + 1;
            normalized ^= 0x35;
            return normalized ^ 0x35;
        }
    }
}
