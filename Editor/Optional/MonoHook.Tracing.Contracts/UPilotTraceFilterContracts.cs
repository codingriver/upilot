// -----------------------------------------------------------------------
// UPilot Editor - public UPilot Tracer filter extension contracts.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using UnityEngine;

namespace CodingRiver.UPilot
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class UPilotTraceFilterAttribute : Attribute
    {
        public string Id { get; }
        public string DisplayName { get; }
        public int Order { get; set; }

        public UPilotTraceFilterAttribute(string id, string displayName)
        {
            Id = id ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
        }
    }

    public sealed class UPilotTraceFilterContext
    {
        public string PointId { get; }
        public UnityEngine.Object Target { get; }
        public GameObject GameObject { get; }
        public Component Component { get; }
        public string ObjectName { get; }
        public string HierarchyPath { get; }
        public string ScenePath { get; }
        public string ComponentType { get; }
        public string TargetType { get; }
        public string TargetGlobalObjectId { get; }
        public int TargetInstanceId { get; }
        public string EventSource { get; }
        public string MethodSignature { get; }
        public string Phase { get; }
        public bool IsPlaying { get; }
        public string BeforeValue { get; }
        public string AfterValue { get; }

        public UPilotTraceFilterContext(
            string pointId,
            UnityEngine.Object target,
            GameObject gameObject,
            Component component,
            string objectName,
            string hierarchyPath,
            string scenePath,
            string componentType,
            string methodSignature,
            string beforeValue,
            string afterValue,
            string phase = null,
            bool isPlaying = false)
            : this(
                pointId,
                target,
                gameObject,
                component,
                objectName,
                hierarchyPath,
                scenePath,
                componentType,
                methodSignature,
                beforeValue,
                afterValue,
                phase,
                isPlaying,
                null,
                null,
                null,
                0)
        {
        }

        public UPilotTraceFilterContext(
            string pointId,
            UnityEngine.Object target,
            GameObject gameObject,
            Component component,
            string objectName,
            string hierarchyPath,
            string scenePath,
            string componentType,
            string methodSignature,
            string beforeValue,
            string afterValue,
            string phase,
            bool isPlaying,
            string targetType,
            string targetGlobalObjectId,
            string eventSource,
            int targetInstanceId)
        {
            PointId = pointId ?? string.Empty;
            Target = target;
            GameObject = gameObject;
            Component = component;
            ObjectName = objectName ?? string.Empty;
            HierarchyPath = hierarchyPath ?? string.Empty;
            ScenePath = scenePath ?? string.Empty;
            ComponentType = componentType ?? string.Empty;
            TargetType = targetType ?? string.Empty;
            TargetGlobalObjectId = targetGlobalObjectId ?? string.Empty;
            TargetInstanceId = targetInstanceId;
            EventSource = eventSource ?? string.Empty;
            MethodSignature = methodSignature ?? string.Empty;
            Phase = phase ?? string.Empty;
            IsPlaying = isPlaying;
            BeforeValue = beforeValue ?? string.Empty;
            AfterValue = afterValue ?? string.Empty;
        }
    }

    public interface IUPilotTraceFilterProvider
    {
        bool Matches(UPilotTraceFilterContext context, string argument, out string reason);
    }

    public abstract class UPilotTraceFilterBase : IUPilotTraceFilterProvider
    {
        public abstract bool Matches(UPilotTraceFilterContext context, string argument, out string reason);
    }
}
