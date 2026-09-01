// -----------------------------------------------------------------------
// UPilot Editor - physical MonoHook installers for selected Unity points.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using MonoHook;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot
{
    /// <summary>
    /// Bridges the manually managed point list to physical MethodHook instances.
    /// Stack capture is intentionally not part of this first implementation.
    /// </summary>
    internal static class UPilotMonoHookInstallationService
    {
        private const int MinimumManagedMethodBodySize = 10;
        private const int MaximumCoverageSamples = 5;

        private static readonly Dictionary<string, List<MethodHook>> LifecycleHooks =
            new Dictionary<string, List<MethodHook>>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> LifecycleFilterSignatures =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Dictionary<Type, Action<MonoBehaviour>>> LifecycleProxyDispatch =
            new Dictionary<string, Dictionary<Type, Action<MonoBehaviour>>>(StringComparer.Ordinal);
        private static readonly Dictionary<string, UPilotMonoHookCoverage> CoverageByPoint =
            new Dictionary<string, UPilotMonoHookCoverage>(StringComparer.Ordinal);
        private static readonly Dictionary<string, bool> AppliedHookAllSafeOverloadsByPoint =
            new Dictionary<string, bool>(StringComparer.Ordinal);

        private static MethodHook _setActiveHook;
        private static MethodHook _addComponentHook;
        private static MethodHook _behaviourEnabledHook;
        private static MethodHook _rendererEnabledHook;
        private static MethodHook _colliderEnabledHook;
        private static MethodHook _collider2DEnabledHook;
        private static readonly List<MethodHook> InstantiateHooks = new List<MethodHook>();
        private static readonly List<MethodHook> DestroyHooks = new List<MethodHook>();
        private static readonly Dictionary<string, List<MethodHook>> TransformHooks =
            new Dictionary<string, List<MethodHook>>(StringComparer.Ordinal);

        [ThreadStatic] private static bool _insideHook;
        [ThreadStatic] private static bool _insideConsoleLog;
        [ThreadStatic] private static int _instantiateHookDepth;
        private static double _eventRateWindowStart;
        private static int _eventRateWindowCount;
        private static double _consoleRateWindowStart;
        private static int _consoleRateWindowCount;
        private static int _consoleDroppedCount;
        private static int _perObjectDroppedCount;
        private static int _duplicateDroppedCount;
        private static int _traceFailureCount;
        private static readonly Dictionary<string, double> PerObjectRateWindowStarts =
            new Dictionary<string, double>(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> PerObjectRateWindowCounts =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly Dictionary<string, double> LastEventTimes =
            new Dictionary<string, double>(StringComparer.Ordinal);
        private static readonly MethodInfo InstanceIdToObjectMethod = typeof(EditorUtility).GetMethod(
            "InstanceIDToObject",
            BindingFlags.Static | BindingFlags.Public,
            null,
            new[] { typeof(int) },
            null);
        private static readonly Dictionary<string, int> StackTraceSampleCounts =
            new Dictionary<string, int>(StringComparer.Ordinal);

        public static readonly UPilotMonoHookEventBuffer Events = new UPilotMonoHookEventBuffer();
        public static int ConsoleDroppedCount => _consoleDroppedCount;
        public static int PerObjectDroppedCount => _perObjectDroppedCount;
        public static int DuplicateDroppedCount => _duplicateDroppedCount;
        public static int TraceFailureCount => _traceFailureCount;

        internal static bool SupportsHookAllSafeOverloads(string pointId)
        {
            switch (pointId)
            {
                case UPilotMonoHookPointId.GameObjectInstantiate:
                case UPilotMonoHookPointId.GameObjectDestroy:
                case UPilotMonoHookPointId.TransformSetParent:
                case UPilotMonoHookPointId.TransformTranslate:
                case UPilotMonoHookPointId.TransformRotate:
                case UPilotMonoHookPointId.TransformRotateAround:
                case UPilotMonoHookPointId.TransformLookAt:
                    return true;
                default:
                    return false;
            }
        }

        internal static bool IsHookAllSafeOverloadsApplied(string pointId)
        {
            return AppliedHookAllSafeOverloadsByPoint.TryGetValue(pointId, out bool applied) && applied;
        }

        internal static long Publish(UPilotMonoHookEvent hookEvent)
        {
            try
            {
                return PublishCore(hookEvent);
            }
            catch
            {
                _traceFailureCount++;
                return 0;
            }
        }

        private static long PublishCore(UPilotMonoHookEvent hookEvent)
        {
            if (hookEvent == null) return 0;

            var settings = UPilotMonoHookSettings.instance;
            settings.EnsureDefaults();
            if (string.IsNullOrEmpty(hookEvent.pointId))
                hookEvent.pointId = hookEvent.kind ?? string.Empty;
            if (!hookEvent.filterEvaluated)
            {
                if (!UPilotTraceFilterEngine.Evaluate(hookEvent, true, out var filterDecision))
                    return 0;
                hookEvent.filterProfileId = filterDecision.ProfileId;
                hookEvent.filterProfileName = filterDecision.ProfileName;
                hookEvent.filterReason = filterDecision.Reason;
                hookEvent.filterEvaluated = true;
            }
            bool hasValue = !string.IsNullOrEmpty(hookEvent.beforeValue) ||
                            !string.IsNullOrEmpty(hookEvent.afterValue);
            if (settings.suppressUnchangedValues && hasValue &&
                string.Equals(hookEvent.beforeValue, hookEvent.afterValue, StringComparison.Ordinal))
            {
                return 0;
            }

            double now = EditorApplication.timeSinceStartup;
            if (settings.suppressDuplicateEvents && IsDuplicateEvent(hookEvent, now, settings.duplicateEventWindowMilliseconds))
            {
                _duplicateDroppedCount++;
                Events.MarkDropped();
                return 0;
            }

            if (!TryAcquireEventSlot(settings.maxEventsPerSecond))
            {
                Events.MarkDropped();
                return 0;
            }
            if (settings.enablePerObjectRateLimit &&
                !TryAcquirePerObjectEventSlot(hookEvent, now, settings.maxEventsPerObjectPerSecond))
            {
                _perObjectDroppedCount++;
                Events.MarkDropped();
                return 0;
            }

            if (settings.ShouldCaptureStackTrace(hookEvent.pointId) &&
                ShouldSampleStackTrace(hookEvent.pointId, settings.stackTraceSampleEveryN))
            {
                hookEvent.stackTrace = CaptureStackTrace(settings.stackTraceMaxFrames);
            }
            if (string.IsNullOrEmpty(hookEvent.timestampUtc))
                hookEvent.timestampUtc = DateTime.UtcNow.ToString("O");
            if (settings.suppressDuplicateEvents)
                LastEventTimes[BuildEventIdentityKey(hookEvent)] = now;
            long sequence = Events.Add(hookEvent);
            if (sequence > 0 && settings.logEventsToConsole)
                WriteConsoleLog(hookEvent, settings.maxConsoleLogsPerSecond);
            return sequence;
        }

        internal static void ClearEvents()
        {
            Events.Clear();
            _eventRateWindowStart = 0d;
            _eventRateWindowCount = 0;
            _consoleRateWindowStart = 0d;
            _consoleRateWindowCount = 0;
            _consoleDroppedCount = 0;
            _perObjectDroppedCount = 0;
            _duplicateDroppedCount = 0;
            _traceFailureCount = 0;
            PerObjectRateWindowStarts.Clear();
            PerObjectRateWindowCounts.Clear();
            LastEventTimes.Clear();
            StackTraceSampleCounts.Clear();
            UPilotTraceFilterEngine.ClearStatistics();
        }

        public static bool IsInstalled(string pointId)
        {
            if (pointId == UPilotMonoHookPointId.GameObjectSetActive)
                return _setActiveHook != null && _setActiveHook.isHooked;
            if (pointId == UPilotMonoHookPointId.GameObjectAddComponent)
                return _addComponentHook != null && _addComponentHook.isHooked;
            if (pointId == UPilotMonoHookPointId.ComponentBehaviourEnabled)
                return _behaviourEnabledHook != null && _behaviourEnabledHook.isHooked;
            if (pointId == UPilotMonoHookPointId.ComponentRendererEnabled)
                return _rendererEnabledHook != null && _rendererEnabledHook.isHooked;
            if (pointId == UPilotMonoHookPointId.ComponentColliderEnabled)
                return _colliderEnabledHook != null && _colliderEnabledHook.isHooked;
            if (pointId == UPilotMonoHookPointId.ComponentCollider2DEnabled)
                return _collider2DEnabledHook != null && _collider2DEnabledHook.isHooked;
            if (pointId == UPilotMonoHookPointId.GameObjectInstantiate)
                return InstantiateHooks.Count > 0 && InstantiateHooks.All(hook => hook != null && hook.isHooked);
            if (pointId == UPilotMonoHookPointId.GameObjectDestroy)
                return DestroyHooks.Count > 0 && DestroyHooks.All(hook => hook != null && hook.isHooked);
            if (pointId == UPilotMonoHookPointId.LifecycleAwake ||
                 pointId == UPilotMonoHookPointId.LifecycleOnEnable ||
                 pointId == UPilotMonoHookPointId.LifecycleStart ||
                 pointId == UPilotMonoHookPointId.LifecycleUpdate ||
                 pointId == UPilotMonoHookPointId.LifecycleFixedUpdate ||
                 pointId == UPilotMonoHookPointId.LifecycleLateUpdate ||
                 pointId == UPilotMonoHookPointId.LifecycleOnDisable ||
                pointId == UPilotMonoHookPointId.LifecycleOnDestroy)
                return LifecycleHooks.TryGetValue(pointId, out var lifecycle) &&
                       lifecycle.Count > 0 &&
                       lifecycle.All(hook => hook.isHooked) &&
                       LifecycleFilterSignatures.TryGetValue(pointId, out var signature) &&
                       string.Equals(signature, GetLifecycleFilterSignature(pointId), StringComparison.Ordinal);
            return TransformHooks.TryGetValue(pointId, out var transform) &&
                   transform.Count > 0 && transform.All(hook => hook != null && hook.isHooked);
        }

        public static UPilotMonoHookCoverage GetCoverage(string pointId)
        {
            if (string.IsNullOrEmpty(pointId)) return null;
            CoverageByPoint.TryGetValue(pointId, out var coverage);
            return coverage;
        }

        static UPilotMonoHookInstallationService()
        {
            AssemblyReloadEvents.beforeAssemblyReload += UninstallAll;
            EditorApplication.quitting += UninstallAll;
        }

        public static UPilotMonoHookSupport CheckSupport(string pointId)
        {
            if (pointId == UPilotMonoHookPointId.GameObjectInstantiate)
                return CheckAnySafeObjectDefinition(BuildInstantiateAllDefinitions(), "Object.Instantiate");
            if (pointId == UPilotMonoHookPointId.GameObjectDestroy)
                return CheckAnySafeObjectDefinition(BuildDestroyAllDefinitions(), "Object.Destroy/DestroyImmediate");
            if (pointId == UPilotMonoHookPointId.ComponentBehaviourEnabled)
            {
                return TryGetBehaviourEnabledSetter(out _, out var reason)
                    ? UPilotMonoHookSupport.Supported()
                    : UPilotMonoHookSupport.Unsupported(reason);
            }
            if (pointId == UPilotMonoHookPointId.ComponentRendererEnabled)
                return CheckComponentEnabledSupport(typeof(Renderer), "Renderer.enabled");
            if (pointId == UPilotMonoHookPointId.ComponentColliderEnabled)
                return CheckComponentEnabledSupport(typeof(Collider), "Collider.enabled");
            if (pointId == UPilotMonoHookPointId.ComponentCollider2DEnabled)
                return CheckComponentEnabledSupport(typeof(Collider2D), "Collider2D.enabled");
            if (pointId == UPilotMonoHookPointId.GameObjectAddComponent)
            {
                return TryGetAddComponentTarget(out _, out var reason)
                    ? UPilotMonoHookSupport.Supported()
                    : UPilotMonoHookSupport.Unsupported(reason);
            }

            switch (pointId)
            {
                case UPilotMonoHookPointId.GameObjectSetActive:
                    return CheckMethodSupport(
                        typeof(GameObject),
                        "SetActive",
                        BindingFlags.Instance | BindingFlags.Public,
                        new[] { typeof(bool) },
                        "GameObject.SetActive");
                case UPilotMonoHookPointId.TransformPosition:
                    return CheckTransformPropertySupport("position");
                case UPilotMonoHookPointId.TransformLocalPosition:
                    return CheckTransformPropertySupport("localPosition");
                case UPilotMonoHookPointId.TransformRotation:
                    return CheckTransformPropertySupport("rotation");
                case UPilotMonoHookPointId.TransformLocalRotation:
                    return CheckTransformPropertySupport("localRotation");
                case UPilotMonoHookPointId.TransformEulerAngles:
                    return CheckTransformPropertySupport("eulerAngles");
                case UPilotMonoHookPointId.TransformLocalEulerAngles:
                    return CheckTransformPropertySupport("localEulerAngles");
                case UPilotMonoHookPointId.TransformLocalScale:
                    return CheckTransformPropertySupport("localScale");
                case UPilotMonoHookPointId.TransformSetPositionAndRotation:
                    return CheckTransformMethodSupport("SetPositionAndRotation", new[] { typeof(Vector3), typeof(Quaternion) });
                case UPilotMonoHookPointId.TransformSetLocalPositionAndRotation:
                    return CheckTransformMethodSupport("SetLocalPositionAndRotation", new[] { typeof(Vector3), typeof(Quaternion) });
                case UPilotMonoHookPointId.TransformSetParent:
                    return CheckAnySafeTransformDefinition(BuildSetParentDefinitions(true), "Transform.SetParent");
                case UPilotMonoHookPointId.TransformSetSiblingIndex:
                    return CheckTransformMethodSupport("SetSiblingIndex", new[] { typeof(int) });
                case UPilotMonoHookPointId.TransformTranslate:
                    return CheckAnySafeTransformDefinition(BuildTranslateDefinitions(true), "Transform.Translate");
                case UPilotMonoHookPointId.TransformRotate:
                    return CheckAnySafeTransformDefinition(BuildRotateDefinitions(true), "Transform.Rotate");
                case UPilotMonoHookPointId.TransformRotateAround:
                    return CheckAnySafeTransformDefinition(BuildRotateAroundDefinitions(true), "Transform.RotateAround");
                case UPilotMonoHookPointId.TransformLookAt:
                    return CheckAnySafeTransformDefinition(BuildLookAtDefinitions(true), "Transform.LookAt");
                case UPilotMonoHookPointId.TransformSetAsFirstSibling:
                    return CheckTransformMethodSupport("SetAsFirstSibling", Type.EmptyTypes);
                case UPilotMonoHookPointId.TransformSetAsLastSibling:
                    return CheckTransformMethodSupport("SetAsLastSibling", Type.EmptyTypes);
                case UPilotMonoHookPointId.TransformDetachChildren:
                    return CheckTransformMethodSupport("DetachChildren", Type.EmptyTypes);
            }

            return UPilotMonoHookPointId.IsBuiltIn(pointId)
                ? UPilotMonoHookSupport.Supported()
                : UPilotMonoHookSupport.Unsupported("未知的内置 MonoHook 点位：" + pointId);
        }

        public static void InstallPoint(string pointId, bool hookAllSafeOverloads = false)
        {
            switch (pointId)
            {
                case UPilotMonoHookPointId.LifecycleAwake: InstallLifecycle(pointId, "Awake"); return;
                case UPilotMonoHookPointId.LifecycleOnEnable: InstallLifecycle(pointId, "OnEnable"); return;
                case UPilotMonoHookPointId.LifecycleStart: InstallLifecycle(pointId, "Start"); return;
                case UPilotMonoHookPointId.LifecycleUpdate: InstallLifecycle(pointId, "Update"); return;
                case UPilotMonoHookPointId.LifecycleFixedUpdate: InstallLifecycle(pointId, "FixedUpdate"); return;
                case UPilotMonoHookPointId.LifecycleLateUpdate: InstallLifecycle(pointId, "LateUpdate"); return;
                case UPilotMonoHookPointId.LifecycleOnDisable: InstallLifecycle(pointId, "OnDisable"); return;
                case UPilotMonoHookPointId.LifecycleOnDestroy: InstallLifecycle(pointId, "OnDestroy"); return;
                case UPilotMonoHookPointId.GameObjectInstantiate: InstallInstantiate(hookAllSafeOverloads); return;
                case UPilotMonoHookPointId.GameObjectDestroy: InstallDestroy(hookAllSafeOverloads); return;
                case UPilotMonoHookPointId.GameObjectSetActive: InstallSetActive(); return;
                case UPilotMonoHookPointId.GameObjectAddComponent: InstallAddComponent(); return;
                case UPilotMonoHookPointId.TransformPosition: InstallTransform(pointId, new MethodHookDefinition("set_position", new[] { typeof(Vector3) }, nameof(PositionReplacement), nameof(PositionProxy))); return;
                case UPilotMonoHookPointId.TransformLocalPosition: InstallTransform(pointId, new MethodHookDefinition("set_localPosition", new[] { typeof(Vector3) }, nameof(LocalPositionReplacement), nameof(LocalPositionProxy))); return;
                case UPilotMonoHookPointId.TransformRotation: InstallTransform(pointId, new MethodHookDefinition("set_rotation", new[] { typeof(Quaternion) }, nameof(RotationReplacement), nameof(RotationProxy))); return;
                case UPilotMonoHookPointId.TransformLocalRotation: InstallTransform(pointId, new MethodHookDefinition("set_localRotation", new[] { typeof(Quaternion) }, nameof(LocalRotationReplacement), nameof(LocalRotationProxy))); return;
                case UPilotMonoHookPointId.TransformEulerAngles: InstallTransform(pointId, new MethodHookDefinition("set_eulerAngles", new[] { typeof(Vector3) }, nameof(EulerAnglesReplacement), nameof(EulerAnglesProxy))); return;
                case UPilotMonoHookPointId.TransformLocalEulerAngles: InstallTransform(pointId, new MethodHookDefinition("set_localEulerAngles", new[] { typeof(Vector3) }, nameof(LocalEulerAnglesReplacement), nameof(LocalEulerAnglesProxy))); return;
                case UPilotMonoHookPointId.TransformLocalScale: InstallTransform(pointId, new MethodHookDefinition("set_localScale", new[] { typeof(Vector3) }, nameof(LocalScaleReplacement), nameof(LocalScaleProxy))); return;
                case UPilotMonoHookPointId.TransformSetPositionAndRotation: InstallTransform(pointId, new MethodHookDefinition("SetPositionAndRotation", new[] { typeof(Vector3), typeof(Quaternion) }, nameof(SetPositionAndRotationReplacement), nameof(SetPositionAndRotationProxy))); return;
                case UPilotMonoHookPointId.TransformSetLocalPositionAndRotation: InstallTransform(pointId, new MethodHookDefinition("SetLocalPositionAndRotation", new[] { typeof(Vector3), typeof(Quaternion) }, nameof(SetLocalPositionAndRotationReplacement), nameof(SetLocalPositionAndRotationProxy))); return;
                case UPilotMonoHookPointId.TransformSetParent:
                    InstallTransform(pointId, hookAllSafeOverloads, BuildSetParentDefinitions(hookAllSafeOverloads));
                    return;
                case UPilotMonoHookPointId.TransformSetSiblingIndex: InstallTransform(pointId, new MethodHookDefinition("SetSiblingIndex", new[] { typeof(int) }, nameof(SetSiblingIndexReplacement), nameof(SetSiblingIndexProxy))); return;
                case UPilotMonoHookPointId.TransformTranslate: InstallTransform(pointId, hookAllSafeOverloads, BuildTranslateDefinitions(hookAllSafeOverloads)); return;
                case UPilotMonoHookPointId.TransformRotate: InstallTransform(pointId, hookAllSafeOverloads, BuildRotateDefinitions(hookAllSafeOverloads)); return;
                case UPilotMonoHookPointId.TransformRotateAround: InstallTransform(pointId, hookAllSafeOverloads, BuildRotateAroundDefinitions(hookAllSafeOverloads)); return;
                case UPilotMonoHookPointId.TransformLookAt: InstallTransform(pointId, hookAllSafeOverloads, BuildLookAtDefinitions(hookAllSafeOverloads)); return;
                case UPilotMonoHookPointId.TransformSetAsFirstSibling: InstallTransform(pointId, new MethodHookDefinition("SetAsFirstSibling", Type.EmptyTypes, nameof(SetAsFirstSiblingReplacement), nameof(SetAsFirstSiblingProxy))); return;
                case UPilotMonoHookPointId.TransformSetAsLastSibling: InstallTransform(pointId, new MethodHookDefinition("SetAsLastSibling", Type.EmptyTypes, nameof(SetAsLastSiblingReplacement), nameof(SetAsLastSiblingProxy))); return;
                case UPilotMonoHookPointId.TransformDetachChildren: InstallTransform(pointId, new MethodHookDefinition("DetachChildren", Type.EmptyTypes, nameof(DetachChildrenReplacement), nameof(DetachChildrenProxy))); return;
                case UPilotMonoHookPointId.ComponentBehaviourEnabled:
                    InstallBehaviourEnabled(); return;
                case UPilotMonoHookPointId.ComponentRendererEnabled:
                    InstallComponentEnabled(
                        ref _rendererEnabledHook,
                        typeof(Renderer),
                        "Renderer.enabled",
                        nameof(RendererEnabledReplacement),
                        nameof(RendererEnabledProxy));
                    return;
                case UPilotMonoHookPointId.ComponentColliderEnabled:
                    InstallComponentEnabled(
                        ref _colliderEnabledHook,
                        typeof(Collider),
                        "Collider.enabled",
                        nameof(ColliderEnabledReplacement),
                        nameof(ColliderEnabledProxy));
                    return;
                case UPilotMonoHookPointId.ComponentCollider2DEnabled:
                    InstallComponentEnabled(
                        ref _collider2DEnabledHook,
                        typeof(Collider2D),
                        "Collider2D.enabled",
                        nameof(Collider2DEnabledReplacement),
                        nameof(Collider2DEnabledProxy));
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(pointId), pointId, "Unknown built-in MonoHook point.");
            }
        }

        public static void UninstallPoint(string pointId)
        {
            switch (pointId)
            {
                case UPilotMonoHookPointId.LifecycleAwake:
                case UPilotMonoHookPointId.LifecycleOnEnable:
                case UPilotMonoHookPointId.LifecycleStart:
                case UPilotMonoHookPointId.LifecycleUpdate:
                case UPilotMonoHookPointId.LifecycleFixedUpdate:
                case UPilotMonoHookPointId.LifecycleLateUpdate:
                case UPilotMonoHookPointId.LifecycleOnDisable:
                case UPilotMonoHookPointId.LifecycleOnDestroy:
                    UninstallLifecycle(pointId); return;
                case UPilotMonoHookPointId.GameObjectInstantiate: UninstallInstantiate(); return;
                case UPilotMonoHookPointId.GameObjectDestroy: UninstallDestroy(); return;
                case UPilotMonoHookPointId.GameObjectSetActive: UninstallSetActive(); return;
                case UPilotMonoHookPointId.GameObjectAddComponent: UninstallAddComponent(); return;
                case UPilotMonoHookPointId.TransformPosition:
                case UPilotMonoHookPointId.TransformLocalPosition:
                case UPilotMonoHookPointId.TransformRotation:
                case UPilotMonoHookPointId.TransformLocalRotation:
                case UPilotMonoHookPointId.TransformEulerAngles:
                case UPilotMonoHookPointId.TransformLocalEulerAngles:
                case UPilotMonoHookPointId.TransformLocalScale:
                case UPilotMonoHookPointId.TransformSetPositionAndRotation:
                case UPilotMonoHookPointId.TransformSetLocalPositionAndRotation:
                case UPilotMonoHookPointId.TransformSetParent:
                case UPilotMonoHookPointId.TransformSetSiblingIndex:
                case UPilotMonoHookPointId.TransformTranslate:
                case UPilotMonoHookPointId.TransformRotate:
                case UPilotMonoHookPointId.TransformRotateAround:
                case UPilotMonoHookPointId.TransformLookAt:
                case UPilotMonoHookPointId.TransformSetAsFirstSibling:
                case UPilotMonoHookPointId.TransformSetAsLastSibling:
                case UPilotMonoHookPointId.TransformDetachChildren:
                    UninstallTransform(pointId); return;
                case UPilotMonoHookPointId.ComponentBehaviourEnabled: UninstallBehaviourEnabled(); return;
                case UPilotMonoHookPointId.ComponentRendererEnabled: UninstallHook(ref _rendererEnabledHook); return;
                case UPilotMonoHookPointId.ComponentColliderEnabled: UninstallHook(ref _colliderEnabledHook); return;
                case UPilotMonoHookPointId.ComponentCollider2DEnabled: UninstallHook(ref _collider2DEnabledHook); return;
                default: throw new ArgumentOutOfRangeException(nameof(pointId), pointId, "Unknown built-in MonoHook point.");
            }
        }

        private static MethodHookDefinition[] BuildInstantiateAllDefinitions()
        {
            return new[]
            {
                new MethodHookDefinition("Instantiate", new[] { typeof(UnityEngine.Object) }, nameof(InstantiateOriginalReplacement), nameof(InstantiateOriginalProxy)),
                new MethodHookDefinition("Instantiate", new[] { typeof(UnityEngine.Object), typeof(Vector3), typeof(Quaternion) }, nameof(InstantiatePositionRotationReplacement), nameof(InstantiatePositionRotationProxy)),
                new MethodHookDefinition("Instantiate", new[] { typeof(UnityEngine.Object), typeof(Transform) }, nameof(InstantiateReplacement), nameof(InstantiateProxy)),
                new MethodHookDefinition("Instantiate", new[] { typeof(UnityEngine.Object), typeof(Transform), typeof(bool) }, nameof(InstantiateParentWorldReplacement), nameof(InstantiateParentWorldProxy)),
                new MethodHookDefinition("Instantiate", new[] { typeof(UnityEngine.Object), typeof(Vector3), typeof(Quaternion), typeof(Transform) }, nameof(InstantiatePositionRotationParentReplacement), nameof(InstantiatePositionRotationParentProxy)),
                new MethodHookDefinition("Instantiate", new[] { typeof(UnityEngine.Object), typeof(UnityEngine.SceneManagement.Scene) }, nameof(InstantiateSceneReplacement), nameof(InstantiateSceneProxy)),
            };
        }

        private static MethodHookDefinition[] BuildDestroyAllDefinitions()
        {
            return new[]
            {
                new MethodHookDefinition("Destroy", new[] { typeof(UnityEngine.Object), typeof(float) }, nameof(DestroyDelayedReplacement), nameof(DestroyDelayedProxy)),
                new MethodHookDefinition("Destroy", new[] { typeof(UnityEngine.Object) }, nameof(DestroyReplacement), nameof(DestroyProxy)),
                new MethodHookDefinition("DestroyImmediate", new[] { typeof(UnityEngine.Object), typeof(bool) }, nameof(DestroyImmediateAllowAssetsReplacement), nameof(DestroyImmediateAllowAssetsProxy)),
                new MethodHookDefinition("DestroyImmediate", new[] { typeof(UnityEngine.Object) }, nameof(DestroyImmediateReplacement), nameof(DestroyImmediateProxy)),
            };
        }

        private static UPilotMonoHookSupport CheckAnySafeObjectDefinition(
            IEnumerable<MethodHookDefinition> definitions,
            string displayName)
        {
            var reasons = new List<string>();
            foreach (var definition in definitions ?? Array.Empty<MethodHookDefinition>())
            {
                var target = GetObjectTarget(definition);
                string reason = string.Empty;
                if (target != null && TryValidateHookTarget(target, false, out reason))
                    return UPilotMonoHookSupport.Supported();
                AddCoverageSample(reasons, definition.Signature + "：" + (target == null ? "未找到目标方法" : reason));
            }
            return UPilotMonoHookSupport.Unsupported(
                displayName + " 没有可安全 Hook 的公开托管重载" +
                (reasons.Count == 0 ? string.Empty : "：" + string.Join("；", reasons)));
        }

        private static UPilotMonoHookSupport CheckAnySafeTransformDefinition(
            IEnumerable<MethodHookDefinition> definitions,
            string displayName)
        {
            var reasons = new List<string>();
            foreach (var definition in definitions ?? Array.Empty<MethodHookDefinition>())
            {
                var target = GetTransformTarget(definition);
                string reason = string.Empty;
                if (target != null && TryValidateHookTarget(target, false, out reason))
                    return UPilotMonoHookSupport.Supported();
                AddCoverageSample(reasons, definition.Signature + "：" + (target == null ? "未找到目标方法" : reason));
            }
            return UPilotMonoHookSupport.Unsupported(
                displayName + " 没有可安全 Hook 的公开托管重载" +
                (reasons.Count == 0 ? string.Empty : "：" + string.Join("；", reasons)));
        }

        private static MethodHookDefinition[] BuildSetParentDefinitions(bool hookAllSafeOverloads)
        {
            var final = new MethodHookDefinition(
                "SetParent",
                new[] { typeof(Transform), typeof(bool) },
                nameof(SetParentWorldReplacement),
                nameof(SetParentWorldProxy));
            var wrapper = new MethodHookDefinition(
                "SetParent",
                new[] { typeof(Transform) },
                nameof(SetParentReplacement),
                nameof(SetParentProxy));
            return hookAllSafeOverloads
                ? new[] { final, wrapper }
                : new[] { SelectFirstSafeTransformDefinition(final, wrapper) };
        }

        private static MethodHookDefinition[] BuildTranslateDefinitions(bool hookAllSafeOverloads)
        {
            var vectorSpace = new MethodHookDefinition("Translate", new[] { typeof(Vector3), typeof(Space) }, nameof(TranslateReplacement), nameof(TranslateProxy));
            var vector = new MethodHookDefinition("Translate", new[] { typeof(Vector3) }, nameof(TranslateVectorReplacement), nameof(TranslateVectorProxy));
            var xyzSpace = new MethodHookDefinition("Translate", new[] { typeof(float), typeof(float), typeof(float), typeof(Space) }, nameof(TranslateXYZSpaceReplacement), nameof(TranslateXYZSpaceProxy));
            var xyz = new MethodHookDefinition("Translate", new[] { typeof(float), typeof(float), typeof(float) }, nameof(TranslateXYZReplacement), nameof(TranslateXYZProxy));
            var vectorTransform = new MethodHookDefinition("Translate", new[] { typeof(Vector3), typeof(Transform) }, nameof(TranslateTransformReplacement), nameof(TranslateTransformProxy));
            var xyzTransform = new MethodHookDefinition("Translate", new[] { typeof(float), typeof(float), typeof(float), typeof(Transform) }, nameof(TranslateXYZTransformReplacement), nameof(TranslateXYZTransformProxy));
            if (hookAllSafeOverloads)
                return new[] { vectorSpace, vector, xyzSpace, xyz, vectorTransform, xyzTransform };
            return new[]
            {
                SelectFirstSafeTransformDefinition(vectorSpace, vector, xyzSpace, xyz),
                SelectFirstSafeTransformDefinition(vectorTransform, xyzTransform),
            };
        }

        private static MethodHookDefinition[] BuildRotateDefinitions(bool hookAllSafeOverloads)
        {
            var vectorSpace = new MethodHookDefinition("Rotate", new[] { typeof(Vector3), typeof(Space) }, nameof(RotateReplacement), nameof(RotateProxy));
            var vector = new MethodHookDefinition("Rotate", new[] { typeof(Vector3) }, nameof(RotateVectorReplacement), nameof(RotateVectorProxy));
            var xyzSpace = new MethodHookDefinition("Rotate", new[] { typeof(float), typeof(float), typeof(float), typeof(Space) }, nameof(RotateXYZSpaceReplacement), nameof(RotateXYZSpaceProxy));
            var xyz = new MethodHookDefinition("Rotate", new[] { typeof(float), typeof(float), typeof(float) }, nameof(RotateXYZReplacement), nameof(RotateXYZProxy));
            var axisAngleSpace = new MethodHookDefinition("Rotate", new[] { typeof(Vector3), typeof(float), typeof(Space) }, nameof(RotateAxisAngleSpaceReplacement), nameof(RotateAxisAngleSpaceProxy));
            var axisAngle = new MethodHookDefinition("Rotate", new[] { typeof(Vector3), typeof(float) }, nameof(RotateAxisAngleReplacement), nameof(RotateAxisAngleProxy));
            if (hookAllSafeOverloads)
                return new[] { vectorSpace, vector, xyzSpace, xyz, axisAngleSpace, axisAngle };
            return new[]
            {
                SelectFirstSafeTransformDefinition(vectorSpace, vector, xyzSpace, xyz),
                SelectFirstSafeTransformDefinition(axisAngleSpace, axisAngle),
            };
        }

        private static MethodHookDefinition[] BuildRotateAroundDefinitions(bool hookAllSafeOverloads)
        {
            var current = new MethodHookDefinition(
                "RotateAround",
                new[] { typeof(Vector3), typeof(Vector3), typeof(float) },
                nameof(RotateAroundReplacement),
                nameof(RotateAroundProxy));
            var legacy = new MethodHookDefinition(
                "RotateAround",
                new[] { typeof(Vector3), typeof(float) },
                nameof(RotateAroundLegacyReplacement),
                nameof(RotateAroundLegacyProxy));
            return hookAllSafeOverloads
                ? new[] { current, legacy }
                : new[] { SelectFirstSafeTransformDefinition(current) };
        }

        private static MethodHookDefinition[] BuildLookAtDefinitions(bool hookAllSafeOverloads)
        {
            var vectorUp = new MethodHookDefinition("LookAt", new[] { typeof(Vector3), typeof(Vector3) }, nameof(LookAtPositionUpReplacement), nameof(LookAtPositionUpProxy));
            var transformUp = new MethodHookDefinition("LookAt", new[] { typeof(Transform), typeof(Vector3) }, nameof(LookAtReplacement), nameof(LookAtProxy));
            var vector = new MethodHookDefinition("LookAt", new[] { typeof(Vector3) }, nameof(LookAtPositionReplacement), nameof(LookAtPositionProxy));
            var transform = new MethodHookDefinition("LookAt", new[] { typeof(Transform) }, nameof(LookAtTransformReplacement), nameof(LookAtTransformProxy));
            if (hookAllSafeOverloads)
                return new[] { vectorUp, transformUp, vector, transform };

            var explicitUp = SelectFirstSafeTransformDefinition(vectorUp, transformUp);
            var selected = new List<MethodHookDefinition>
            {
                explicitUp,
                SelectFirstSafeTransformDefinition(vector),
            };
            if (!ReferenceEquals(explicitUp, vectorUp))
                selected.Add(SelectFirstSafeTransformDefinition(transform));
            return selected.ToArray();
        }

        private static MethodHookDefinition SelectFirstSafeTransformDefinition(
            params MethodHookDefinition[] definitions)
        {
            foreach (var definition in definitions ?? Array.Empty<MethodHookDefinition>())
            {
                var target = GetTransformTarget(definition);
                if (target != null && TryValidateHookTarget(target, false, out _))
                    return definition;
            }
            return definitions != null && definitions.Length > 0 ? definitions[0] : null;
        }

        private static MethodInfo GetTransformTarget(MethodHookDefinition definition)
        {
            if (definition == null) return null;
            if (definition.MethodName.StartsWith("set_", StringComparison.Ordinal))
            {
                var propertyName = definition.MethodName.Substring(4);
                return typeof(Transform)
                    .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)
                    ?.GetSetMethod(true);
            }
            return typeof(Transform).GetMethod(
                definition.MethodName,
                BindingFlags.Instance | BindingFlags.Public,
                null,
                definition.Parameters,
                null);
        }

        private static void InstallTransform(string pointId, params MethodHookDefinition[] definitions)
        {
            InstallTransform(pointId, false, definitions);
        }

        private static void InstallTransform(
            string pointId,
            bool hookAllSafeOverloads,
            params MethodHookDefinition[] definitions)
        {
            if (TransformHooks.TryGetValue(pointId, out var existing) &&
                existing.Count > 0 &&
                existing.All(hook => hook != null && hook.isHooked) &&
                (!SupportsHookAllSafeOverloads(pointId) ||
                 IsHookAllSafeOverloadsApplied(pointId) == hookAllSafeOverloads))
                return;

            UninstallTransform(pointId);
            var installed = new List<MethodHook>();
            var samples = new List<string>();
            int candidateCount = definitions?.Length ?? 0;
            int skippedCount = 0;
            int failedCount = 0;
            foreach (var definition in definitions ?? Array.Empty<MethodHookDefinition>())
            {
                var target = GetTransformTarget(definition);
                if (target == null)
                {
                    skippedCount++;
                    AddCoverageSample(samples, definition.Signature + "：未找到目标方法");
                    continue;
                }
                if (!TryValidateHookTarget(target, false, out var skipReason))
                {
                    skippedCount++;
                    AddCoverageSample(samples, definition.Signature + "：" + skipReason);
                    continue;
                }

                try
                {
                    installed.Add(CreateHook(
                        target,
                        definition.Replacement,
                        definition.Proxy,
                        "UPilot.MonoHook." + pointId + "." + definition.Signature));
                }
                catch (Exception ex)
                {
                    failedCount++;
                    AddCoverageSample(samples, definition.Signature + "：" + ex.GetBaseException().Message);
                }
            }

            CoverageByPoint[pointId] = new UPilotMonoHookCoverage(
                candidateCount,
                installed.Count,
                skippedCount,
                failedCount,
                samples);
            if (installed.Count == 0)
                throw new InvalidOperationException(CoverageByPoint[pointId].BuildSummary());
            TransformHooks[pointId] = installed;
            if (SupportsHookAllSafeOverloads(pointId))
                AppliedHookAllSafeOverloadsByPoint[pointId] = hookAllSafeOverloads;
        }

        private static void UninstallTransform(string pointId)
        {
            if (TransformHooks.TryGetValue(pointId, out var hooks))
            {
                foreach (var hook in hooks)
                    hook?.Uninstall();
                TransformHooks.Remove(pointId);
            }
            AppliedHookAllSafeOverloadsByPoint.Remove(pointId);
            CoverageByPoint.Remove(pointId);
        }

        public static void UninstallAll()
        {
            UninstallSetActive();
            UninstallAddComponent();
            UninstallBehaviourEnabled();
            UninstallHook(ref _rendererEnabledHook);
            UninstallHook(ref _colliderEnabledHook);
            UninstallHook(ref _collider2DEnabledHook);
            UninstallInstantiate();
            UninstallDestroy();
            foreach (var pointId in LifecycleHooks.Keys.ToArray())
                UninstallLifecycle(pointId);
            foreach (var pointId in TransformHooks.Keys.ToArray())
                UninstallTransform(pointId);
        }

        private static void InstallSetActive()
        {
            if (_setActiveHook != null && _setActiveHook.isHooked) return;

            var target = typeof(GameObject).GetMethod(
                "SetActive", BindingFlags.Instance | BindingFlags.Public,
                null, new[] { typeof(bool) }, null);
            if (target == null) throw new MissingMethodException(typeof(GameObject).FullName, "SetActive(bool)");
            if (!TryValidateHookTarget(target, false, out var reason))
                throw new NotSupportedException("GameObject.SetActive 无法安全 Hook：" + reason);

            _setActiveHook = CreateHook(target, nameof(SetActiveReplacement), nameof(SetActiveProxy), "UPilot.MonoHook.GameObject.SetActive");
        }

        private static void UninstallSetActive()
        {
            _setActiveHook?.Uninstall();
            _setActiveHook = null;
        }

        private static void InstallAddComponent()
        {
            if (_addComponentHook != null && _addComponentHook.isHooked)
                return;

            UninstallAddComponent();
            if (!TryGetAddComponentTarget(out var target, out var reason))
                throw new NotSupportedException(reason);

            _addComponentHook = CreateHook(
                target,
                nameof(AddComponentReplacement),
                nameof(AddComponentProxy),
                "UPilot.MonoHook.GameObject.AddComponent");
        }

        private static void UninstallAddComponent()
        {
            _addComponentHook?.Uninstall();
            _addComponentHook = null;
        }

        private static void InstallBehaviourEnabled()
        {
            if (_behaviourEnabledHook != null && _behaviourEnabledHook.isHooked)
                return;

            UninstallBehaviourEnabled();
            if (!TryGetBehaviourEnabledSetter(out var target, out var reason))
                throw new NotSupportedException(reason);

            _behaviourEnabledHook = CreateHook(
                target,
                nameof(BehaviourEnabledReplacement),
                nameof(BehaviourEnabledProxy),
                "UPilot.MonoHook.Behaviour.enabled");
        }

        private static void UninstallBehaviourEnabled()
        {
            _behaviourEnabledHook?.Uninstall();
            _behaviourEnabledHook = null;
        }

        private static void InstallComponentEnabled(
            ref MethodHook hook,
            Type componentType,
            string displayName,
            string replacement,
            string proxy)
        {
            if (hook != null && hook.isHooked)
                return;

            UninstallHook(ref hook);
            if (!TryGetComponentEnabledSetter(componentType, displayName, out var target, out var reason))
                throw new NotSupportedException(reason);

            hook = CreateHook(
                target,
                replacement,
                proxy,
                "UPilot.MonoHook." + displayName);
        }

        private static void UninstallHook(ref MethodHook hook)
        {
            hook?.Uninstall();
            hook = null;
        }

        private static void InstallInstantiate(bool hookAllSafeOverloads)
        {
            if (InstantiateHooks.Count > 0 &&
                InstantiateHooks.All(hook => hook != null && hook.isHooked) &&
                IsHookAllSafeOverloadsApplied(UPilotMonoHookPointId.GameObjectInstantiate) == hookAllSafeOverloads)
                return;

            UninstallInstantiate();
            var original = new MethodHookDefinition("Instantiate", new[] { typeof(UnityEngine.Object) }, nameof(InstantiateOriginalReplacement), nameof(InstantiateOriginalProxy));
            var positionRotation = new MethodHookDefinition("Instantiate", new[] { typeof(UnityEngine.Object), typeof(Vector3), typeof(Quaternion) }, nameof(InstantiatePositionRotationReplacement), nameof(InstantiatePositionRotationProxy));
            var parentWrapper = new MethodHookDefinition("Instantiate", new[] { typeof(UnityEngine.Object), typeof(Transform) }, nameof(InstantiateReplacement), nameof(InstantiateProxy));
            var parentWorld = new MethodHookDefinition("Instantiate", new[] { typeof(UnityEngine.Object), typeof(Transform), typeof(bool) }, nameof(InstantiateParentWorldReplacement), nameof(InstantiateParentWorldProxy));
            var positionRotationParent = new MethodHookDefinition("Instantiate", new[] { typeof(UnityEngine.Object), typeof(Vector3), typeof(Quaternion), typeof(Transform) }, nameof(InstantiatePositionRotationParentReplacement), nameof(InstantiatePositionRotationParentProxy));
            var scene = new MethodHookDefinition("Instantiate", new[] { typeof(UnityEngine.Object), typeof(UnityEngine.SceneManagement.Scene) }, nameof(InstantiateSceneReplacement), nameof(InstantiateSceneProxy));
            var definitions = hookAllSafeOverloads
                ? new[] { original, positionRotation, parentWrapper, parentWorld, positionRotationParent, scene }
                : new[]
                {
                    original,
                    positionRotation,
                    SelectFirstSafeObjectDefinition(parentWorld, parentWrapper),
                    positionRotationParent,
                    scene,
                };

            var samples = new List<string>();
            int skippedCount = 0;
            int failedCount = 0;
            foreach (var definition in definitions)
            {
                var target = typeof(UnityEngine.Object).GetMethod(
                    definition.MethodName,
                    BindingFlags.Static | BindingFlags.Public,
                    null,
                    definition.Parameters,
                    null);
                if (target == null)
                {
                    skippedCount++;
                    AddCoverageSample(samples, definition.Signature + "：未找到目标方法");
                    continue;
                }
                if (!TryValidateHookTarget(target, false, out var skipReason))
                {
                    skippedCount++;
                    AddCoverageSample(samples, definition.Signature + "：" + skipReason);
                    continue;
                }

                try
                {
                    InstantiateHooks.Add(CreateHook(
                        target,
                        definition.Replacement,
                        definition.Proxy,
                        "UPilot.MonoHook.Object." + definition.Signature));
                }
                catch (Exception ex)
                {
                    failedCount++;
                    AddCoverageSample(samples, definition.Signature + "：" + ex.GetBaseException().Message);
                }
            }

            CoverageByPoint[UPilotMonoHookPointId.GameObjectInstantiate] = new UPilotMonoHookCoverage(
                definitions.Length,
                InstantiateHooks.Count,
                skippedCount,
                failedCount,
                samples);
            if (InstantiateHooks.Count == 0)
                throw new InvalidOperationException(
                    CoverageByPoint[UPilotMonoHookPointId.GameObjectInstantiate].BuildSummary());
            AppliedHookAllSafeOverloadsByPoint[UPilotMonoHookPointId.GameObjectInstantiate] = hookAllSafeOverloads;
        }

        private static void UninstallInstantiate()
        {
            foreach (var hook in InstantiateHooks)
                hook?.Uninstall();
            InstantiateHooks.Clear();
            AppliedHookAllSafeOverloadsByPoint.Remove(UPilotMonoHookPointId.GameObjectInstantiate);
            CoverageByPoint.Remove(UPilotMonoHookPointId.GameObjectInstantiate);
        }

        private static void InstallDestroy(bool hookAllSafeOverloads)
        {
            if (DestroyHooks.Count > 0 &&
                DestroyHooks.All(hook => hook != null && hook.isHooked) &&
                IsHookAllSafeOverloadsApplied(UPilotMonoHookPointId.GameObjectDestroy) == hookAllSafeOverloads)
                return;

            UninstallDestroy();
            var destroyFinal = new MethodHookDefinition("Destroy", new[] { typeof(UnityEngine.Object), typeof(float) }, nameof(DestroyDelayedReplacement), nameof(DestroyDelayedProxy));
            var destroyWrapper = new MethodHookDefinition("Destroy", new[] { typeof(UnityEngine.Object) }, nameof(DestroyReplacement), nameof(DestroyProxy));
            var immediateFinal = new MethodHookDefinition("DestroyImmediate", new[] { typeof(UnityEngine.Object), typeof(bool) }, nameof(DestroyImmediateAllowAssetsReplacement), nameof(DestroyImmediateAllowAssetsProxy));
            var immediateWrapper = new MethodHookDefinition("DestroyImmediate", new[] { typeof(UnityEngine.Object) }, nameof(DestroyImmediateReplacement), nameof(DestroyImmediateProxy));
            var candidates = hookAllSafeOverloads
                ? new List<MethodHookDefinition> { destroyFinal, destroyWrapper, immediateFinal, immediateWrapper }
                : new List<MethodHookDefinition>
                {
                    SelectFirstSafeObjectDefinition(destroyFinal, destroyWrapper),
                    SelectFirstSafeObjectDefinition(immediateFinal, immediateWrapper),
                };

            var samples = new List<string>();
            int skippedCount = 0;
            int failedCount = 0;
            foreach (var candidate in candidates)
            {
                var target = typeof(UnityEngine.Object).GetMethod(
                    candidate.MethodName,
                    BindingFlags.Static | BindingFlags.Public,
                    null,
                    candidate.Parameters,
                    null);
                if (target == null)
                {
                    skippedCount++;
                    AddCoverageSample(samples, candidate.Signature + "：未找到目标方法");
                    continue;
                }
                if (!TryValidateHookTarget(target, false, out var skipReason))
                {
                    skippedCount++;
                    AddCoverageSample(samples, candidate.Signature + "：" + skipReason);
                    continue;
                }

                try
                {
                    DestroyHooks.Add(CreateHook(
                        target,
                        candidate.Replacement,
                        candidate.Proxy,
                        "UPilot.MonoHook.GameObject." + candidate.Signature));
                }
                catch (Exception ex)
                {
                    failedCount++;
                    AddCoverageSample(samples, candidate.Signature + "：" + ex.GetBaseException().Message);
                }
            }

            CoverageByPoint[UPilotMonoHookPointId.GameObjectDestroy] = new UPilotMonoHookCoverage(
                candidates.Count,
                DestroyHooks.Count,
                skippedCount,
                failedCount,
                samples);
            if (DestroyHooks.Count == 0)
                throw new InvalidOperationException(
                    CoverageByPoint[UPilotMonoHookPointId.GameObjectDestroy].BuildSummary());
            AppliedHookAllSafeOverloadsByPoint[UPilotMonoHookPointId.GameObjectDestroy] = hookAllSafeOverloads;
        }

        private static void UninstallDestroy()
        {
            foreach (var hook in DestroyHooks)
                hook?.Uninstall();
            DestroyHooks.Clear();
            AppliedHookAllSafeOverloadsByPoint.Remove(UPilotMonoHookPointId.GameObjectDestroy);
            CoverageByPoint.Remove(UPilotMonoHookPointId.GameObjectDestroy);
        }

        private static MethodHookDefinition SelectFirstSafeObjectDefinition(
            params MethodHookDefinition[] definitions)
        {
            foreach (var definition in definitions ?? Array.Empty<MethodHookDefinition>())
            {
                var target = GetObjectTarget(definition);
                if (target != null && TryValidateHookTarget(target, false, out _))
                    return definition;
            }
            return definitions != null && definitions.Length > 0 ? definitions[0] : null;
        }

        private static MethodInfo GetObjectTarget(MethodHookDefinition definition)
        {
            return definition == null
                ? null
                : typeof(UnityEngine.Object).GetMethod(
                    definition.MethodName,
                    BindingFlags.Static | BindingFlags.Public,
                    null,
                    definition.Parameters,
                    null);
        }

        private static void InstallLifecycle(string pointId, string methodName)
        {
            string filterSignature = GetLifecycleFilterSignature(pointId);
            if (LifecycleHooks.TryGetValue(pointId, out var existing) &&
                existing.Count > 0 &&
                LifecycleFilterSignatures.TryGetValue(pointId, out var existingSignature) &&
                string.Equals(existingSignature, filterSignature, StringComparison.Ordinal))
                return;

            UninstallLifecycle(pointId);

            var installed = new List<MethodHook>();
            var proxyByTarget = new Dictionary<MethodBase, UPilotMonoHookProxyFactory.ProxyEntry>();
            var dispatch = new Dictionary<Type, Action<MonoBehaviour>>();
            var details = new List<UPilotMonoHookInstallEntry>();
            var samples = new List<string>();
            int candidateCount = 0;
            int skippedCount = 0;
            int failedCount = 0;
            string replacement;
            string proxyTemplateName;
            switch (methodName)
            {
                case "Awake": replacement = nameof(AwakeReplacement); proxyTemplateName = nameof(AwakeProxy); break;
                case "OnEnable": replacement = nameof(OnEnableReplacement); proxyTemplateName = nameof(OnEnableProxy); break;
                case "Start": replacement = nameof(StartReplacement); proxyTemplateName = nameof(StartProxy); break;
                case "Update": replacement = nameof(UpdateReplacement); proxyTemplateName = nameof(UpdateProxy); break;
                case "FixedUpdate": replacement = nameof(FixedUpdateReplacement); proxyTemplateName = nameof(FixedUpdateProxy); break;
                case "LateUpdate": replacement = nameof(LateUpdateReplacement); proxyTemplateName = nameof(LateUpdateProxy); break;
                case "OnDisable": replacement = nameof(OnDisableReplacement); proxyTemplateName = nameof(OnDisableProxy); break;
                case "OnDestroy": replacement = nameof(OnDestroyReplacement); proxyTemplateName = nameof(OnDestroyProxy); break;
                default: throw new ArgumentOutOfRangeException(nameof(methodName), methodName, "Unsupported lifecycle method.");
            }

            var proxyTemplate = typeof(UPilotMonoHookInstallationService).GetMethod(
                proxyTemplateName,
                BindingFlags.Static | BindingFlags.NonPublic);
            foreach (var type in GetMonoBehaviourTypes())
            {
                var target = type.GetMethod(
                    methodName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null);
                if (target == null || target.ReturnType != typeof(void)) continue;
                candidateCount++;

                if (!UPilotMonoHookLifecycleFilter.Includes(type, UPilotMonoHookSettings.instance, pointId, out var filterReason))
                {
                    skippedCount++;
                    string reason = type.FullName + "：" + filterReason;
                    AddCoverageSample(samples, reason);
                    details.Add(new UPilotMonoHookInstallEntry(
                        type.FullName,
                        target.DeclaringType?.FullName,
                        BuildMethodSignature(target),
                        BuildTargetMethodId(target),
                        "Skipped",
                        filterReason));
                    continue;
                }

                if (!TryValidateManagedHookTarget(target, out var skipReason))
                {
                    skippedCount++;
                    AddCoverageSample(samples, type.FullName + "：" + skipReason);
                    details.Add(new UPilotMonoHookInstallEntry(
                        type.FullName,
                        target.DeclaringType?.FullName,
                        BuildMethodSignature(target),
                        BuildTargetMethodId(target),
                        "Skipped",
                        skipReason));
                    continue;
                }

                try
                {
                    if (!proxyByTarget.TryGetValue(target, out var proxyEntry))
                    {
                        if (proxyTemplate == null)
                            throw new MissingMethodException(typeof(UPilotMonoHookInstallationService).FullName, proxyTemplateName);
                        proxyEntry = UPilotMonoHookProxyFactory.GetOrCreate(target, proxyTemplate);
                        proxyByTarget.Add(target, proxyEntry);
                        var hook = CreateHook(
                            target,
                            replacement,
                            proxyEntry.Method,
                            "UPilot.MonoHook." + pointId + "." + type.FullName);
                        installed.Add(hook);
                    }

                    var original = (Action<MonoBehaviour>)Delegate.CreateDelegate(
                        typeof(Action<MonoBehaviour>),
                        proxyEntry.Method);
                    dispatch[type] = original;
                    details.Add(new UPilotMonoHookInstallEntry(
                        type.FullName,
                        target.DeclaringType?.FullName,
                        BuildMethodSignature(target),
                        BuildTargetMethodId(target),
                        "Installed",
                        "",
                        proxyEntry.Key));
                }
                catch (Exception ex)
                {
                    failedCount++;
                    string reason = ex.GetBaseException().Message;
                    AddCoverageSample(samples, type.FullName + "：" + reason);
                    details.Add(new UPilotMonoHookInstallEntry(
                        type.FullName,
                        target.DeclaringType?.FullName,
                        BuildMethodSignature(target),
                        BuildTargetMethodId(target),
                        "Failed",
                        reason));
                }
            }

            LifecycleHooks[pointId] = installed;
            LifecycleProxyDispatch[pointId] = dispatch;
            LifecycleFilterSignatures[pointId] = filterSignature;
            CoverageByPoint[pointId] = new UPilotMonoHookCoverage(
                candidateCount,
                installed.Count,
                skippedCount,
                failedCount,
                samples,
                details);
            if (installed.Count == 0)
                throw new InvalidOperationException(CoverageByPoint[pointId].BuildSummary());
        }

        private static void UninstallLifecycle(string pointId)
        {
            if (LifecycleHooks.TryGetValue(pointId, out var hooks))
            {
                foreach (var hook in hooks)
                    hook.Uninstall();
            }
            LifecycleHooks.Remove(pointId);
            LifecycleFilterSignatures.Remove(pointId);
            LifecycleProxyDispatch.Remove(pointId);
            CoverageByPoint.Remove(pointId);
        }

        private static string GetLifecycleFilterSignature(string pointId)
        {
            var settings = UPilotMonoHookSettings.instance;
            return string.Join("\u001f", new[]
            {
                settings.lifecycleAssemblyIncludes ?? string.Empty,
                settings.lifecycleAssemblyExcludes ?? string.Empty,
                settings.lifecycleNamespaceIncludes ?? string.Empty,
                settings.lifecycleNamespaceExcludes ?? string.Empty,
                settings.lifecycleTypeIncludes ?? string.Empty,
                settings.lifecycleTypeExcludes ?? string.Empty,
                UPilotTraceFilterEngine.GetProfileSignature(pointId, settings),
            });
        }

        private static string BuildTargetMethodId(MethodBase target)
        {
            if (target == null) return string.Empty;
            string moduleId = target.Module?.ModuleVersionId.ToString("N") ?? string.Empty;
            string declaringType = target.DeclaringType?.AssemblyQualifiedName ?? string.Empty;
            return moduleId + "|" + declaringType + "|" + target.MetadataToken.ToString("X8");
        }

        private static string BuildMethodSignature(MethodBase target)
        {
            if (target == null) return string.Empty;
            var parameters = target.GetParameters()
                .Select(parameter => parameter.ParameterType?.Name ?? string.Empty);
            return target.Name + "(" + string.Join(",", parameters) + ")";
        }

        private static bool TryGetLifecycleProxy(
            string pointId,
            MonoBehaviour instance,
            out Action<MonoBehaviour> proxy)
        {
            proxy = null;
            if (instance == null || !LifecycleProxyDispatch.TryGetValue(pointId, out var dispatch))
                return false;

            var type = instance.GetType();
            while (type != null && typeof(MonoBehaviour).IsAssignableFrom(type))
            {
                if (dispatch.TryGetValue(type, out proxy))
                    return proxy != null;
                type = type.BaseType;
            }
            return false;
        }

        private static void InvokeLifecycleOriginal(string pointId, MonoBehaviour instance)
        {
            if (instance == null) return;
            if (TryGetLifecycleProxy(pointId, instance, out var proxy))
            {
                proxy(instance);
                return;
            }

            // A missing dispatch entry means installation state and runtime type
            // discovery diverged. Never call a shared proxy that may point at a
            // different type; fail closed and keep the diagnostic counter visible.
            _traceFailureCount++;
        }

        private static bool TryGetBehaviourEnabledSetter(out MethodInfo target, out string reason)
        {
            target = typeof(Behaviour)
                .GetProperty("enabled", BindingFlags.Instance | BindingFlags.Public)
                ?.GetSetMethod(true);
            if (target == null)
            {
                reason = "未找到 Behaviour.enabled 托管 setter";
                return false;
            }

            if (!TryValidateManagedHookTarget(target, out reason))
            {
                reason = "Behaviour.enabled 无法安全 Hook：" + reason;
                return false;
            }

            return true;
        }

        private static UPilotMonoHookSupport CheckComponentEnabledSupport(Type componentType, string displayName)
        {
            return TryGetComponentEnabledSetter(componentType, displayName, out _, out var reason)
                ? UPilotMonoHookSupport.Supported()
                : UPilotMonoHookSupport.Unsupported(reason);
        }

        private static bool TryGetComponentEnabledSetter(
            Type componentType,
            string displayName,
            out MethodInfo target,
            out string reason)
        {
            target = componentType
                .GetProperty("enabled", BindingFlags.Instance | BindingFlags.Public)
                ?.GetSetMethod(true);
            if (target == null)
            {
                reason = "未找到 " + displayName + " 托管 setter";
                return false;
            }

            if (!TryValidateManagedHookTarget(target, out reason))
            {
                reason = displayName + " 无法安全 Hook：" + reason;
                return false;
            }

            return true;
        }

        private static bool TryGetAddComponentTarget(out MethodInfo target, out string reason)
        {
            target = typeof(GameObject).GetMethod(
                "AddComponent",
                BindingFlags.Instance | BindingFlags.Public,
                null,
                new[] { typeof(Type) },
                null);
            if (target == null)
            {
                reason = "未找到 GameObject.AddComponent(Type)";
                return false;
            }
            if (!TryValidateHookTarget(target, false, out reason))
            {
                reason = "GameObject.AddComponent(Type) 无法安全 Hook：" + reason;
                return false;
            }
            return true;
        }

        private static UPilotMonoHookSupport CheckTransformMethodSupport(string methodName, Type[] parameters)
        {
            var target = typeof(Transform).GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.Public,
                null,
                parameters,
                null);
            if (target == null)
                return UPilotMonoHookSupport.Unsupported("未找到 Transform." + methodName);
            return TryValidateHookTarget(target, false, out var reason)
                ? UPilotMonoHookSupport.Supported()
                : UPilotMonoHookSupport.Unsupported("Transform." + methodName + " 无法安全 Hook：" + reason);
        }

        private static UPilotMonoHookSupport CheckTransformPropertySupport(string propertyName)
        {
            var target = typeof(Transform)
                .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)
                ?.GetSetMethod(true);
            if (target == null)
                return UPilotMonoHookSupport.Unsupported("未找到 Transform." + propertyName + " setter");
            return TryValidateHookTarget(target, false, out var reason)
                ? UPilotMonoHookSupport.Supported()
                : UPilotMonoHookSupport.Unsupported("Transform." + propertyName + " 无法安全 Hook：" + reason);
        }

        private static UPilotMonoHookSupport CheckMethodSupport(
            Type declaringType,
            string methodName,
            BindingFlags flags,
            Type[] parameters,
            string displayName)
        {
            var target = declaringType.GetMethod(methodName, flags, null, parameters, null);
            if (target == null)
                return UPilotMonoHookSupport.Unsupported("未找到 " + displayName);
            return TryValidateHookTarget(target, false, out var reason)
                ? UPilotMonoHookSupport.Supported()
                : UPilotMonoHookSupport.Unsupported(displayName + " 无法安全 Hook：" + reason);
        }

        private static bool TryValidateManagedHookTarget(MethodInfo target, out string reason)
        {
            return TryValidateHookTarget(target, false, out reason);
        }

        private static bool TryValidateHookTarget(MethodInfo target, bool allowInternalCall, out string reason)
        {
            if (target == null)
            {
                reason = "目标方法为空";
                return false;
            }
            if (target.IsAbstract)
            {
                reason = "抽象方法";
                return false;
            }
            if (target.ContainsGenericParameters)
            {
                reason = "包含未绑定泛型参数";
                return false;
            }
            bool isInternalCall =
                (target.MethodImplementationFlags & MethodImplAttributes.InternalCall) != 0;
            if (isInternalCall && !allowInternalCall)
            {
                reason = "InternalCall 方法";
                return false;
            }

            try
            {
                var body = target.GetMethodBody();
                var il = body?.GetILAsByteArray();
                if (il == null)
                {
                    if (isInternalCall && allowInternalCall)
                    {
                        reason = string.Empty;
                        return true;
                    }
                    reason = "无法读取托管方法体";
                    return false;
                }
                if (il.Length < MinimumManagedMethodBodySize)
                {
                    reason = "方法体过短（" + il.Length + " bytes）";
                    return false;
                }
            }
            catch (Exception ex)
            {
                reason = "方法体检查失败：" + ex.GetBaseException().Message;
                return false;
            }

            reason = string.Empty;
            return true;
        }

        private static void AddCoverageSample(List<string> samples, string value)
        {
            if (samples == null || samples.Count >= MaximumCoverageSamples || string.IsNullOrEmpty(value))
                return;
            samples.Add(value);
        }

        private static MethodHook CreateHook(MethodBase target, string replacementName, string proxyName, string tag)
        {
            var flags = BindingFlags.Static | BindingFlags.NonPublic;
            var proxy = typeof(UPilotMonoHookInstallationService).GetMethod(proxyName, flags);
            return CreateHook(target, replacementName, proxy, tag);
        }

        private static MethodHook CreateHook(MethodBase target, string replacementName, MethodInfo proxy, string tag)
        {
            var existing = HookPool.GetHook(target);
            if (existing != null && existing.isHooked)
                throw new InvalidOperationException("目标方法已被其他 MonoHook 占用：" + target.DeclaringType + "." + target.Name);

            var flags = BindingFlags.Static | BindingFlags.NonPublic;
            var replacement = typeof(UPilotMonoHookInstallationService).GetMethod(replacementName, flags);
            var hook = new MethodHook(target, replacement, proxy, tag);
            hook.Install();
            if (!hook.isHooked)
                throw new InvalidOperationException("MonoHook 未安装：" + tag);
            return hook;
        }

        private sealed class MethodHookDefinition
        {
            public readonly string MethodName;
            public readonly Type[] Parameters;
            public readonly string Replacement;
            public readonly string Proxy;
            public string Signature => MethodName + "(" +
                                       string.Join(",", Parameters.Select(type => type.Name)) + ")";

            public MethodHookDefinition(string methodName, Type[] parameters, string replacement, string proxy)
            {
                MethodName = methodName;
                Parameters = parameters;
                Replacement = replacement;
                Proxy = proxy;
            }
        }

        private static IEnumerable<Type> GetMonoBehaviourTypes()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(type => type != null).ToArray(); }
                catch { continue; }

                foreach (var type in types)
                {
                    if (type == null || type.IsAbstract || !typeof(MonoBehaviour).IsAssignableFrom(type)) continue;
                    if (type.Namespace != null && type.Namespace.StartsWith("UnityEngine", StringComparison.Ordinal)) continue;
                    yield return type;
                }
            }
        }

        private static void Record(
            string kind,
            UnityEngine.Object target,
            string componentType = null,
            string before = null,
            string after = null,
            string phase = "after",
            string methodSignature = null)
        {
            if (_insideHook || target == null) return;
            _insideHook = true;
            try
            {
                RecordCore(
                    kind,
                    target,
                    componentType,
                    before,
                    after,
                    phase,
                    methodSignature);
            }
            catch
            {
                // Tracing is observational. A filter, stack, object lookup, or
                // buffer failure must never change the original Unity call.
                _traceFailureCount++;
            }
            finally
            {
                _insideHook = false;
            }
        }

        private static void RecordCore(
            string kind,
            UnityEngine.Object target,
            string componentType,
            string before,
            string after,
            string phase,
            string methodSignature)
        {
                if (!UPilotTraceFilterEngine.Evaluate(
                        kind,
                        target,
                        string.Empty,
                        string.Empty,
                        string.Empty,
                        componentType,
                        methodSignature,
                        before,
                        after,
                        phase,
                        EditorApplication.isPlaying,
                        true,
                        out var filterDecision,
                        target.GetType().FullName,
                        TryGetGlobalObjectId(target),
                        EditorApplication.isPlaying ? "PlayMode" : "EditMode",
                        GetObjectId(target)))
                {
                        return;
                }
                var gameObject = target as GameObject;
                var component = target as Component;
                var transform = gameObject != null ? gameObject.transform : component != null ? component.transform : null;
                Publish(new UPilotMonoHookEvent
                {
                    pointId = kind,
                    kind = kind,
                    phase = phase,
                    frame = Time.frameCount,
                    objectName = gameObject != null ? gameObject.name : component != null ? component.name : target.name,
                    instanceId = GetObjectId(target),
                    hierarchyPath = GetHierarchyPath(transform),
                    scenePath = transform != null && transform.gameObject.scene.IsValid() ? transform.gameObject.scene.path : string.Empty,
                    componentType = componentType ?? component?.GetType().FullName ?? target.GetType().FullName,
                    targetType = target.GetType().FullName,
                    targetGlobalObjectId = TryGetGlobalObjectId(target),
                    eventSource = EditorApplication.isPlaying ? "PlayMode" : "EditMode",
                    methodSignature = methodSignature ?? string.Empty,
                    beforeValue = before ?? string.Empty,
                    afterValue = after ?? string.Empty,
                    filterProfileId = filterDecision.ProfileId,
                    filterProfileName = filterDecision.ProfileName,
                    filterReason = filterDecision.Reason,
                    target = target,
                    filterEvaluated = true,
                });
        }

        private static string TryGetGlobalObjectId(UnityEngine.Object target)
        {
            if (target == null) return string.Empty;
            try { return GlobalObjectId.GetGlobalObjectIdSlow(target).ToString(); }
            catch { return string.Empty; }
        }

        private static bool TryAcquireEventSlot(int maxEventsPerSecond)
        {
            double now = EditorApplication.timeSinceStartup;
            if (_eventRateWindowStart <= 0d || now - _eventRateWindowStart >= 1d)
            {
                _eventRateWindowStart = now;
                _eventRateWindowCount = 0;
            }

            if (_eventRateWindowCount >= Math.Max(1, maxEventsPerSecond))
                return false;

            _eventRateWindowCount++;
            return true;
        }

        private static bool TryAcquirePerObjectEventSlot(
            UPilotMonoHookEvent hookEvent,
            double now,
            int maxEventsPerSecond)
        {
            string key = BuildEventObjectKey(hookEvent);
            if (!PerObjectRateWindowStarts.TryGetValue(key, out double start) || now - start >= 1d)
            {
                PerObjectRateWindowStarts[key] = now;
                PerObjectRateWindowCounts[key] = 0;
            }
            PerObjectRateWindowCounts.TryGetValue(key, out int count);
            if (count >= Math.Max(1, maxEventsPerSecond)) return false;
            PerObjectRateWindowCounts[key] = count + 1;
            return true;
        }

        private static bool IsDuplicateEvent(UPilotMonoHookEvent hookEvent, double now, int windowMilliseconds)
        {
            string key = BuildEventIdentityKey(hookEvent);
            if (!LastEventTimes.TryGetValue(key, out double previous)) return false;
            return now - previous <= Math.Max(1, windowMilliseconds) / 1000d;
        }

        private static string BuildEventObjectKey(UPilotMonoHookEvent hookEvent)
        {
            return (hookEvent?.pointId ?? hookEvent?.kind ?? string.Empty) + "\u001f" +
                   (hookEvent?.instanceId.ToString() ?? "0") + "\u001f" +
                   (hookEvent?.targetGlobalObjectId ?? string.Empty) + "\u001f" +
                   (hookEvent?.hierarchyPath ?? hookEvent?.objectName ?? string.Empty);
        }

        private static string BuildEventIdentityKey(UPilotMonoHookEvent hookEvent)
        {
            return BuildEventObjectKey(hookEvent) + "\u001f" +
                   (hookEvent?.methodSignature ?? string.Empty) + "\u001f" +
                   (hookEvent?.phase ?? string.Empty) + "\u001f" +
                   (hookEvent?.beforeValue ?? string.Empty) + "\u001f" +
                   (hookEvent?.afterValue ?? string.Empty);
        }

        private static bool TryAcquireConsoleLogSlot(int maxLogsPerSecond)
        {
            double now = EditorApplication.timeSinceStartup;
            if (_consoleRateWindowStart <= 0d || now - _consoleRateWindowStart >= 1d)
            {
                _consoleRateWindowStart = now;
                _consoleRateWindowCount = 0;
            }
            if (_consoleRateWindowCount >= Math.Max(1, maxLogsPerSecond))
                return false;
            _consoleRateWindowCount++;
            return true;
        }

        private static void WriteConsoleLog(UPilotMonoHookEvent hookEvent, int maxLogsPerSecond)
        {
            if (_insideConsoleLog)
                return;
            if (!TryAcquireConsoleLogSlot(maxLogsPerSecond))
            {
                _consoleDroppedCount++;
                return;
            }

            _insideConsoleLog = true;
            try
            {
                var context = hookEvent.instanceId != 0 && InstanceIdToObjectMethod != null
                    ? InstanceIdToObjectMethod.Invoke(null, new object[] { hookEvent.instanceId }) as UnityEngine.Object
                    : null;
                Debug.LogFormat(
                    LogType.Log,
                    LogOption.NoStacktrace,
                    context,
                    "{0}",
                    FormatConsoleLog(hookEvent));
            }
            catch
            {
                _consoleDroppedCount++;
            }
            finally
            {
                _insideConsoleLog = false;
            }
        }

        internal static string FormatConsoleLog(UPilotMonoHookEvent hookEvent)
        {
            if (hookEvent == null)
                return "[UPilot][Trace]";

            var builder = new StringBuilder("[UPilot][Trace]");
            if (hookEvent.sequence > 0)
                builder.Append(" #").Append(hookEvent.sequence);
            builder.Append(" F").Append(hookEvent.frame);
            AppendConsoleField(builder, "point", string.IsNullOrEmpty(hookEvent.pointId) ? hookEvent.kind : hookEvent.pointId);
            AppendConsoleField(builder, "phase", hookEvent.phase);
            AppendConsoleField(builder, "scene", hookEvent.scenePath);
            AppendConsoleField(
                builder,
                "object",
                string.IsNullOrEmpty(hookEvent.hierarchyPath) ? hookEvent.objectName : hookEvent.hierarchyPath);
            if (hookEvent.instanceId != 0)
                builder.Append(" id=").Append(hookEvent.instanceId);
            AppendConsoleField(builder, "component", hookEvent.componentType);
            AppendConsoleField(builder, "method", hookEvent.methodSignature);
            if (!string.IsNullOrEmpty(hookEvent.filterProfileId) &&
                !string.Equals(hookEvent.filterProfileId, UPilotTraceFilterProfileIds.None, StringComparison.Ordinal))
                AppendConsoleField(builder, "filter", hookEvent.filterProfileName);

            string value = string.Empty;
            if (!string.IsNullOrEmpty(hookEvent.beforeValue) && !string.IsNullOrEmpty(hookEvent.afterValue))
                value = hookEvent.beforeValue + " -> " + hookEvent.afterValue;
            else if (!string.IsNullOrEmpty(hookEvent.beforeValue))
                value = hookEvent.beforeValue;
            else if (!string.IsNullOrEmpty(hookEvent.afterValue))
                value = hookEvent.afterValue;
            AppendConsoleField(builder, "value", value);

            if (!string.IsNullOrWhiteSpace(hookEvent.stackTrace))
                builder.Append("\nHook caller:\n").Append(hookEvent.stackTrace.Trim());
            return builder.ToString();
        }

        private static void AppendConsoleField(StringBuilder builder, string name, string value)
        {
            if (string.IsNullOrEmpty(value))
                return;
            builder
                .Append(' ')
                .Append(name)
                .Append("=\"")
                .Append(value.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\"", "\\\""))
                .Append('"');
        }

        private static bool ShouldSampleStackTrace(string pointId, int everyN)
        {
            everyN = Math.Max(1, everyN);
            StackTraceSampleCounts.TryGetValue(pointId ?? string.Empty, out int count);
            StackTraceSampleCounts[pointId ?? string.Empty] = count + 1;
            return count % everyN == 0;
        }

        private static string CaptureStackTrace(int maxFrames)
        {
            var frames = new System.Diagnostics.StackTrace(2, false).GetFrames();
            if (frames == null || frames.Length == 0) return string.Empty;

            int limit = Math.Min(Math.Max(1, maxFrames), frames.Length);
            var builder = new StringBuilder();
            for (int i = 0; i < limit; i++)
            {
                var method = frames[i].GetMethod();
                if (method == null) continue;
                if (builder.Length > 0) builder.Append('\n');
                builder.Append(method.DeclaringType?.FullName ?? "<unknown>")
                    .Append('.')
                    .Append(method.Name);
            }
            return builder.ToString();
        }

        private static string GetHierarchyPath(Transform transform)
        {
            if (transform == null) return string.Empty;
            var names = new Stack<string>();
            while (transform != null)
            {
                names.Push(transform.name);
                transform = transform.parent;
            }
            return string.Join("/", names);
        }

        internal static int GetObjectId(UnityEngine.Object target)
        {
#if UNITY_6000_0_OR_NEWER
            // Unity 6 marks GetInstanceID obsolete-as-error. Reflection keeps the
            // event schema compatible with older UPilot consumers while avoiding
            // a compile-time reference to the obsolete API.
            var method = typeof(UnityEngine.Object).GetMethod("GetInstanceID", BindingFlags.Instance | BindingFlags.Public);
            return method == null ? 0 : (int)method.Invoke(target, null);
#else
            return target == null ? 0 : target.GetInstanceID();
#endif
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void SetActiveReplacement(GameObject __this, bool value)
        {
            bool before = __this != null && __this.activeSelf;
            SetActiveProxy(__this, value);
            bool after = __this != null && __this.activeSelf;
            Record("gameObject.setActive", __this, typeof(GameObject).FullName, before.ToString(), after.ToString());
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void SetActiveProxy(GameObject __this, bool value)
        {
            throw new InvalidOperationException("SetActiveProxy must only be called through MonoHook.");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static Component AddComponentReplacement(GameObject __this, Type componentType)
        {
            var component = AddComponentProxy(__this, componentType);
            Record(
                "gameObject.addComponent",
                component != null ? component : (UnityEngine.Object)__this,
                component != null ? component.GetType().FullName : componentType?.FullName,
                string.Empty,
                component != null ? component.GetType().FullName : string.Empty);
            return component;
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static Component AddComponentProxy(GameObject __this, Type componentType)
        {
            throw new InvalidOperationException("AddComponentProxy must only be called through MonoHook.");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void BehaviourEnabledReplacement(Behaviour __this, bool value)
        {
            bool before = __this != null && __this.enabled;
            BehaviourEnabledProxy(__this, value);
            bool after = __this != null && __this.enabled;
            Record("component.behaviourEnabled", __this, __this != null ? __this.GetType().FullName : typeof(Behaviour).FullName, before.ToString(), after.ToString());
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void BehaviourEnabledProxy(Behaviour __this, bool value)
        {
            throw new InvalidOperationException("BehaviourEnabledProxy must only be called through MonoHook.");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void RendererEnabledReplacement(Renderer __this, bool value)
        {
            bool before = __this != null && __this.enabled;
            RendererEnabledProxy(__this, value);
            bool after = __this != null && __this.enabled;
            Record(UPilotMonoHookPointId.ComponentRendererEnabled, __this, __this != null ? __this.GetType().FullName : typeof(Renderer).FullName, before.ToString(), after.ToString());
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void RendererEnabledProxy(Renderer __this, bool value)
        {
            throw new InvalidOperationException("RendererEnabledProxy must only be called through MonoHook.");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void ColliderEnabledReplacement(Collider __this, bool value)
        {
            bool before = __this != null && __this.enabled;
            ColliderEnabledProxy(__this, value);
            bool after = __this != null && __this.enabled;
            Record(UPilotMonoHookPointId.ComponentColliderEnabled, __this, __this != null ? __this.GetType().FullName : typeof(Collider).FullName, before.ToString(), after.ToString());
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void ColliderEnabledProxy(Collider __this, bool value)
        {
            throw new InvalidOperationException("ColliderEnabledProxy must only be called through MonoHook.");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void Collider2DEnabledReplacement(Collider2D __this, bool value)
        {
            bool before = __this != null && __this.enabled;
            Collider2DEnabledProxy(__this, value);
            bool after = __this != null && __this.enabled;
            Record(UPilotMonoHookPointId.ComponentCollider2DEnabled, __this, __this != null ? __this.GetType().FullName : typeof(Collider2D).FullName, before.ToString(), after.ToString());
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void Collider2DEnabledProxy(Collider2D __this, bool value)
        {
            throw new InvalidOperationException("Collider2DEnabledProxy must only be called through MonoHook.");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static UnityEngine.Object InstantiateOriginalReplacement(UnityEngine.Object original)
        {
            _instantiateHookDepth++;
            try
            {
                var clone = InstantiateOriginalProxy(original);
                if (ShouldRecordInstantiate())
                    RecordInstantiate(original, clone, "Instantiate(Object)");
                return clone;
            }
            finally
            {
                _instantiateHookDepth--;
            }
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static UnityEngine.Object InstantiateOriginalProxy(UnityEngine.Object original)
        {
            throw new InvalidOperationException("InstantiateOriginalProxy must only be called through MonoHook.");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static UnityEngine.Object InstantiatePositionRotationReplacement(
            UnityEngine.Object original,
            Vector3 position,
            Quaternion rotation)
        {
            _instantiateHookDepth++;
            try
            {
                var clone = InstantiatePositionRotationProxy(original, position, rotation);
                if (ShouldRecordInstantiate())
                    RecordInstantiate(original, clone, "Instantiate(Object,Vector3,Quaternion)");
                return clone;
            }
            finally
            {
                _instantiateHookDepth--;
            }
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static UnityEngine.Object InstantiatePositionRotationProxy(
            UnityEngine.Object original,
            Vector3 position,
            Quaternion rotation)
        {
            throw new InvalidOperationException("InstantiatePositionRotationProxy must only be called through MonoHook.");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static UnityEngine.Object InstantiateReplacement(UnityEngine.Object original, Transform parent)
        {
            _instantiateHookDepth++;
            try
            {
                var clone = InstantiateProxy(original, parent);
                if (ShouldRecordInstantiate())
                    RecordInstantiate(original, clone, "Instantiate(Object,Transform)");
                return clone;
            }
            finally
            {
                _instantiateHookDepth--;
            }
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static UnityEngine.Object InstantiateProxy(UnityEngine.Object original, Transform parent)
        {
            throw new InvalidOperationException("InstantiateProxy must only be called through MonoHook.");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static UnityEngine.Object InstantiateParentWorldReplacement(
            UnityEngine.Object original,
            Transform parent,
            bool instantiateInWorldSpace)
        {
            _instantiateHookDepth++;
            try
            {
                var clone = InstantiateParentWorldProxy(original, parent, instantiateInWorldSpace);
                if (ShouldRecordInstantiate())
                    RecordInstantiate(original, clone, "Instantiate(Object,Transform,bool)");
                return clone;
            }
            finally
            {
                _instantiateHookDepth--;
            }
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static UnityEngine.Object InstantiateParentWorldProxy(
            UnityEngine.Object original,
            Transform parent,
            bool instantiateInWorldSpace)
        {
            throw new InvalidOperationException("InstantiateParentWorldProxy must only be called through MonoHook.");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static UnityEngine.Object InstantiatePositionRotationParentReplacement(
            UnityEngine.Object original,
            Vector3 position,
            Quaternion rotation,
            Transform parent)
        {
            _instantiateHookDepth++;
            try
            {
                var clone = InstantiatePositionRotationParentProxy(original, position, rotation, parent);
                if (ShouldRecordInstantiate())
                    RecordInstantiate(original, clone, "Instantiate(Object,Vector3,Quaternion,Transform)");
                return clone;
            }
            finally
            {
                _instantiateHookDepth--;
            }
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static UnityEngine.Object InstantiatePositionRotationParentProxy(
            UnityEngine.Object original,
            Vector3 position,
            Quaternion rotation,
            Transform parent)
        {
            throw new InvalidOperationException("InstantiatePositionRotationParentProxy must only be called through MonoHook.");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static UnityEngine.Object InstantiateSceneReplacement(
            UnityEngine.Object original,
            UnityEngine.SceneManagement.Scene scene)
        {
            _instantiateHookDepth++;
            try
            {
                var clone = InstantiateSceneProxy(original, scene);
                if (ShouldRecordInstantiate())
                    RecordInstantiate(original, clone, "Instantiate(Object,Scene)");
                return clone;
            }
            finally
            {
                _instantiateHookDepth--;
            }
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static UnityEngine.Object InstantiateSceneProxy(
            UnityEngine.Object original,
            UnityEngine.SceneManagement.Scene scene)
        {
            throw new InvalidOperationException("InstantiateSceneProxy must only be called through MonoHook.");
        }

        private static bool ShouldRecordInstantiate()
        {
            return IsHookAllSafeOverloadsApplied(UPilotMonoHookPointId.GameObjectInstantiate) ||
                   _instantiateHookDepth == 1;
        }

        private static void RecordInstantiate(
            UnityEngine.Object original,
            UnityEngine.Object clone,
            string methodSignature)
        {
            Record(
                "gameObject.instantiate",
                clone,
                clone != null ? clone.GetType().FullName : string.Empty,
                original != null ? original.name : string.Empty,
                clone != null ? clone.name : string.Empty,
                "after",
                methodSignature);
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void DestroyReplacement(UnityEngine.Object target)
        {
            RecordDestroy(target, "Destroy", string.Empty, "Destroy(Object)");
            DestroyProxy(target);
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void DestroyProxy(UnityEngine.Object target)
        {
            throw new InvalidOperationException("DestroyProxy must only be called through MonoHook.");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void DestroyDelayedReplacement(UnityEngine.Object target, float delay)
        {
            RecordDestroy(target, "Destroy", delay.ToString(), "Destroy(Object,float)");
            DestroyDelayedProxy(target, delay);
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void DestroyDelayedProxy(UnityEngine.Object target, float delay)
        {
            throw new InvalidOperationException("DestroyDelayedProxy must only be called through MonoHook.");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void DestroyImmediateReplacement(UnityEngine.Object target)
        {
            RecordDestroy(target, "DestroyImmediate", string.Empty, "DestroyImmediate(Object)");
            DestroyImmediateProxy(target);
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void DestroyImmediateProxy(UnityEngine.Object target)
        {
            throw new InvalidOperationException("DestroyImmediateProxy must only be called through MonoHook.");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void DestroyImmediateAllowAssetsReplacement(UnityEngine.Object target, bool allowDestroyingAssets)
        {
            RecordDestroy(
                target,
                "DestroyImmediate",
                allowDestroyingAssets.ToString(),
                "DestroyImmediate(Object,bool)");
            DestroyImmediateAllowAssetsProxy(target, allowDestroyingAssets);
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void DestroyImmediateAllowAssetsProxy(UnityEngine.Object target, bool allowDestroyingAssets)
        {
            throw new InvalidOperationException("DestroyImmediateAllowAssetsProxy must only be called through MonoHook.");
        }

        private static void RecordDestroy(
            UnityEngine.Object target,
            string methodName,
            string detail,
            string methodSignature)
        {
            Record(
                "gameObject.destroy",
                target,
                target != null ? target.GetType().FullName : string.Empty,
                methodName,
                detail,
                "before",
                methodSignature);
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void AwakeReplacement(MonoBehaviour __this)
        {
            InvokeLifecycleOriginal(UPilotMonoHookPointId.LifecycleAwake, __this);
            Record("lifecycle.awake", __this, __this != null ? __this.GetType().FullName : string.Empty);
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void AwakeProxy(MonoBehaviour __this)
        {
            throw new InvalidOperationException("AwakeProxy must only be called through MonoHook.");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void StartReplacement(MonoBehaviour __this)
        {
            InvokeLifecycleOriginal(UPilotMonoHookPointId.LifecycleStart, __this);
            Record("lifecycle.start", __this, __this != null ? __this.GetType().FullName : string.Empty);
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void StartProxy(MonoBehaviour __this)
        {
            throw new InvalidOperationException("StartProxy must only be called through MonoHook.");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void UpdateReplacement(MonoBehaviour __this)
        {
            InvokeLifecycleOriginal(UPilotMonoHookPointId.LifecycleUpdate, __this);
            Record(UPilotMonoHookPointId.LifecycleUpdate, __this, __this != null ? __this.GetType().FullName : string.Empty);
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void UpdateProxy(MonoBehaviour __this)
        {
            throw new InvalidOperationException("UpdateProxy must only be called through MonoHook.");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void FixedUpdateReplacement(MonoBehaviour __this)
        {
            InvokeLifecycleOriginal(UPilotMonoHookPointId.LifecycleFixedUpdate, __this);
            Record(UPilotMonoHookPointId.LifecycleFixedUpdate, __this, __this != null ? __this.GetType().FullName : string.Empty);
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void FixedUpdateProxy(MonoBehaviour __this)
        {
            throw new InvalidOperationException("FixedUpdateProxy must only be called through MonoHook.");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void LateUpdateReplacement(MonoBehaviour __this)
        {
            InvokeLifecycleOriginal(UPilotMonoHookPointId.LifecycleLateUpdate, __this);
            Record(UPilotMonoHookPointId.LifecycleLateUpdate, __this, __this != null ? __this.GetType().FullName : string.Empty);
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void LateUpdateProxy(MonoBehaviour __this)
        {
            throw new InvalidOperationException("LateUpdateProxy must only be called through MonoHook.");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void OnEnableReplacement(MonoBehaviour __this)
        {
            InvokeLifecycleOriginal(UPilotMonoHookPointId.LifecycleOnEnable, __this);
            Record("lifecycle.onEnable", __this, __this != null ? __this.GetType().FullName : string.Empty);
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void OnEnableProxy(MonoBehaviour __this)
        {
            throw new InvalidOperationException("OnEnableProxy must only be called through MonoHook.");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void OnDisableReplacement(MonoBehaviour __this)
        {
            InvokeLifecycleOriginal(UPilotMonoHookPointId.LifecycleOnDisable, __this);
            Record("lifecycle.onDisable", __this, __this != null ? __this.GetType().FullName : string.Empty);
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void OnDisableProxy(MonoBehaviour __this)
        {
            throw new InvalidOperationException("OnDisableProxy must only be called through MonoHook.");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void OnDestroyReplacement(MonoBehaviour __this)
        {
            InvokeLifecycleOriginal(UPilotMonoHookPointId.LifecycleOnDestroy, __this);
            Record("lifecycle.onDestroy", __this, __this != null ? __this.GetType().FullName : string.Empty);
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void OnDestroyProxy(MonoBehaviour __this)
        {
            throw new InvalidOperationException("OnDestroyProxy must only be called through MonoHook.");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void PositionReplacement(Transform __this, Vector3 value)
        {
            var before = __this != null ? __this.position : Vector3.zero;
            PositionProxy(__this, value);
            Record("transform.position", __this, typeof(Transform).FullName, before.ToString(), (__this != null ? __this.position : value).ToString());
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void PositionProxy(Transform __this, Vector3 value) { throw new InvalidOperationException("PositionProxy must only be called through MonoHook."); }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void LocalPositionReplacement(Transform __this, Vector3 value)
        {
            var before = __this != null ? __this.localPosition : Vector3.zero;
            LocalPositionProxy(__this, value);
            Record("transform.localPosition", __this, typeof(Transform).FullName, before.ToString(), (__this != null ? __this.localPosition : value).ToString());
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void LocalPositionProxy(Transform __this, Vector3 value) { throw new InvalidOperationException("LocalPositionProxy must only be called through MonoHook."); }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void RotationReplacement(Transform __this, Quaternion value)
        {
            var before = __this != null ? __this.rotation : Quaternion.identity;
            RotationProxy(__this, value);
            Record("transform.rotation", __this, typeof(Transform).FullName, before.ToString(), (__this != null ? __this.rotation : value).ToString());
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void RotationProxy(Transform __this, Quaternion value) { throw new InvalidOperationException("RotationProxy must only be called through MonoHook."); }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void LocalRotationReplacement(Transform __this, Quaternion value)
        {
            var before = __this != null ? __this.localRotation : Quaternion.identity;
            LocalRotationProxy(__this, value);
            Record("transform.localRotation", __this, typeof(Transform).FullName, before.ToString(), (__this != null ? __this.localRotation : value).ToString());
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void LocalRotationProxy(Transform __this, Quaternion value) { throw new InvalidOperationException("LocalRotationProxy must only be called through MonoHook."); }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void EulerAnglesReplacement(Transform __this, Vector3 value)
        {
            var before = __this != null ? __this.eulerAngles : Vector3.zero;
            EulerAnglesProxy(__this, value);
            Record("transform.eulerAngles", __this, typeof(Transform).FullName, before.ToString(), (__this != null ? __this.eulerAngles : value).ToString());
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void EulerAnglesProxy(Transform __this, Vector3 value) { throw new InvalidOperationException("EulerAnglesProxy must only be called through MonoHook."); }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void LocalEulerAnglesReplacement(Transform __this, Vector3 value)
        {
            var before = __this != null ? __this.localEulerAngles : Vector3.zero;
            LocalEulerAnglesProxy(__this, value);
            Record("transform.localEulerAngles", __this, typeof(Transform).FullName, before.ToString(), (__this != null ? __this.localEulerAngles : value).ToString());
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void LocalEulerAnglesProxy(Transform __this, Vector3 value) { throw new InvalidOperationException("LocalEulerAnglesProxy must only be called through MonoHook."); }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void LocalScaleReplacement(Transform __this, Vector3 value)
        {
            var before = __this != null ? __this.localScale : Vector3.one;
            LocalScaleProxy(__this, value);
            Record("transform.localScale", __this, typeof(Transform).FullName, before.ToString(), (__this != null ? __this.localScale : value).ToString());
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void LocalScaleProxy(Transform __this, Vector3 value) { throw new InvalidOperationException("LocalScaleProxy must only be called through MonoHook."); }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void SetPositionAndRotationReplacement(Transform __this, Vector3 position, Quaternion rotation)
        {
            var before = __this != null
                ? __this.position + " | " + __this.rotation
                : string.Empty;
            SetPositionAndRotationProxy(__this, position, rotation);
            var after = __this != null
                ? __this.position + " | " + __this.rotation
                : position + " | " + rotation;
            Record("transform.setPositionAndRotation", __this, typeof(Transform).FullName, before, after);
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void SetPositionAndRotationProxy(Transform __this, Vector3 position, Quaternion rotation)
        {
            throw new InvalidOperationException("SetPositionAndRotationProxy must only be called through MonoHook.");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void SetLocalPositionAndRotationReplacement(Transform __this, Vector3 localPosition, Quaternion localRotation)
        {
            var before = __this != null
                ? __this.localPosition + " | " + __this.localRotation
                : string.Empty;
            SetLocalPositionAndRotationProxy(__this, localPosition, localRotation);
            var after = __this != null
                ? __this.localPosition + " | " + __this.localRotation
                : localPosition + " | " + localRotation;
            Record("transform.setLocalPositionAndRotation", __this, typeof(Transform).FullName, before, after);
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void SetLocalPositionAndRotationProxy(Transform __this, Vector3 localPosition, Quaternion localRotation)
        {
            throw new InvalidOperationException("SetLocalPositionAndRotationProxy must only be called through MonoHook.");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void SetParentReplacement(Transform __this, Transform parent)
        {
            var before = __this != null && __this.parent != null ? __this.parent.name : string.Empty;
            SetParentProxy(__this, parent);
            var after = __this != null && __this.parent != null ? __this.parent.name : string.Empty;
            Record("transform.setParent", __this, typeof(Transform).FullName, before, after, "after", "SetParent(Transform)");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void SetParentProxy(Transform __this, Transform parent) { throw new InvalidOperationException("SetParentProxy must only be called through MonoHook."); }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void SetParentWorldReplacement(Transform __this, Transform parent, bool worldPositionStays)
        {
            var before = __this != null && __this.parent != null ? __this.parent.name : string.Empty;
            SetParentWorldProxy(__this, parent, worldPositionStays);
            var after = __this != null && __this.parent != null ? __this.parent.name : string.Empty;
            Record("transform.setParent", __this, typeof(Transform).FullName, before, after, "after", "SetParent(Transform,bool)");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void SetParentWorldProxy(Transform __this, Transform parent, bool worldPositionStays)
        {
            throw new InvalidOperationException("SetParentWorldProxy must only be called through MonoHook.");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void SetSiblingIndexReplacement(Transform __this, int index)
        {
            var before = __this != null ? __this.GetSiblingIndex().ToString() : string.Empty;
            SetSiblingIndexProxy(__this, index);
            var after = __this != null ? __this.GetSiblingIndex().ToString() : index.ToString();
            Record("transform.setSiblingIndex", __this, typeof(Transform).FullName, before, after);
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void SetSiblingIndexProxy(Transform __this, int index) { throw new InvalidOperationException("SetSiblingIndexProxy must only be called through MonoHook."); }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void TranslateReplacement(Transform __this, Vector3 translation, Space relativeTo)
        {
            var before = __this != null ? __this.position : Vector3.zero;
            TranslateProxy(__this, translation, relativeTo);
            var after = __this != null ? __this.position : before;
            Record("transform.translate", __this, typeof(Transform).FullName, before.ToString(), after.ToString(), "after", "Translate(Vector3,Space)");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void TranslateProxy(Transform __this, Vector3 translation, Space relativeTo)
        {
            throw new InvalidOperationException("TranslateProxy must only be called through MonoHook.");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void TranslateVectorReplacement(Transform __this, Vector3 translation)
        {
            var before = __this != null ? __this.position : Vector3.zero;
            TranslateVectorProxy(__this, translation);
            var after = __this != null ? __this.position : before;
            Record("transform.translate", __this, typeof(Transform).FullName, before.ToString(), after.ToString(), "after", "Translate(Vector3)");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void TranslateVectorProxy(Transform __this, Vector3 translation)
        {
            throw new InvalidOperationException("TranslateVectorProxy must only be called through MonoHook.");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void TranslateXYZSpaceReplacement(
            Transform __this,
            float x,
            float y,
            float z,
            Space relativeTo)
        {
            var before = __this != null ? __this.position : Vector3.zero;
            TranslateXYZSpaceProxy(__this, x, y, z, relativeTo);
            var after = __this != null ? __this.position : before;
            Record("transform.translate", __this, typeof(Transform).FullName, before.ToString(), after.ToString(), "after", "Translate(float,float,float,Space)");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void TranslateXYZSpaceProxy(
            Transform __this,
            float x,
            float y,
            float z,
            Space relativeTo)
        {
            throw new InvalidOperationException("TranslateXYZSpaceProxy must only be called through MonoHook.");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void TranslateXYZReplacement(Transform __this, float x, float y, float z)
        {
            var before = __this != null ? __this.position : Vector3.zero;
            TranslateXYZProxy(__this, x, y, z);
            var after = __this != null ? __this.position : before;
            Record("transform.translate", __this, typeof(Transform).FullName, before.ToString(), after.ToString(), "after", "Translate(float,float,float)");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void TranslateXYZProxy(Transform __this, float x, float y, float z)
        {
            throw new InvalidOperationException("TranslateXYZProxy must only be called through MonoHook.");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void TranslateTransformReplacement(
            Transform __this,
            Vector3 translation,
            Transform relativeTo)
        {
            var before = __this != null ? __this.position : Vector3.zero;
            TranslateTransformProxy(__this, translation, relativeTo);
            var after = __this != null ? __this.position : before;
            Record("transform.translate", __this, typeof(Transform).FullName, before.ToString(), after.ToString(), "after", "Translate(Vector3,Transform)");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void TranslateTransformProxy(
            Transform __this,
            Vector3 translation,
            Transform relativeTo)
        {
            throw new InvalidOperationException("TranslateTransformProxy must only be called through MonoHook.");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void TranslateXYZTransformReplacement(
            Transform __this,
            float x,
            float y,
            float z,
            Transform relativeTo)
        {
            var before = __this != null ? __this.position : Vector3.zero;
            TranslateXYZTransformProxy(__this, x, y, z, relativeTo);
            var after = __this != null ? __this.position : before;
            Record("transform.translate", __this, typeof(Transform).FullName, before.ToString(), after.ToString(), "after", "Translate(float,float,float,Transform)");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void TranslateXYZTransformProxy(
            Transform __this,
            float x,
            float y,
            float z,
            Transform relativeTo)
        {
            throw new InvalidOperationException("TranslateXYZTransformProxy must only be called through MonoHook.");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void RotateReplacement(Transform __this, Vector3 eulers, Space relativeTo)
        {
            var before = __this != null ? __this.rotation : Quaternion.identity;
            RotateProxy(__this, eulers, relativeTo);
            var after = __this != null ? __this.rotation : before;
            Record("transform.rotate", __this, typeof(Transform).FullName, before.ToString(), after.ToString(), "after", "Rotate(Vector3,Space)");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void RotateProxy(Transform __this, Vector3 eulers, Space relativeTo)
        {
            throw new InvalidOperationException("RotateProxy must only be called through MonoHook.");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void RotateVectorReplacement(Transform __this, Vector3 eulers)
        {
            var before = __this != null ? __this.rotation : Quaternion.identity;
            RotateVectorProxy(__this, eulers);
            var after = __this != null ? __this.rotation : before;
            Record("transform.rotate", __this, typeof(Transform).FullName, before.ToString(), after.ToString(), "after", "Rotate(Vector3)");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void RotateVectorProxy(Transform __this, Vector3 eulers)
        {
            throw new InvalidOperationException("RotateVectorProxy must only be called through MonoHook.");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void RotateXYZSpaceReplacement(
            Transform __this,
            float xAngle,
            float yAngle,
            float zAngle,
            Space relativeTo)
        {
            var before = __this != null ? __this.rotation : Quaternion.identity;
            RotateXYZSpaceProxy(__this, xAngle, yAngle, zAngle, relativeTo);
            var after = __this != null ? __this.rotation : before;
            Record("transform.rotate", __this, typeof(Transform).FullName, before.ToString(), after.ToString(), "after", "Rotate(float,float,float,Space)");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void RotateXYZSpaceProxy(
            Transform __this,
            float xAngle,
            float yAngle,
            float zAngle,
            Space relativeTo)
        {
            throw new InvalidOperationException("RotateXYZSpaceProxy must only be called through MonoHook.");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void RotateXYZReplacement(
            Transform __this,
            float xAngle,
            float yAngle,
            float zAngle)
        {
            var before = __this != null ? __this.rotation : Quaternion.identity;
            RotateXYZProxy(__this, xAngle, yAngle, zAngle);
            var after = __this != null ? __this.rotation : before;
            Record("transform.rotate", __this, typeof(Transform).FullName, before.ToString(), after.ToString(), "after", "Rotate(float,float,float)");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void RotateXYZProxy(
            Transform __this,
            float xAngle,
            float yAngle,
            float zAngle)
        {
            throw new InvalidOperationException("RotateXYZProxy must only be called through MonoHook.");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void RotateAxisAngleSpaceReplacement(
            Transform __this,
            Vector3 axis,
            float angle,
            Space relativeTo)
        {
            var before = __this != null ? __this.rotation : Quaternion.identity;
            RotateAxisAngleSpaceProxy(__this, axis, angle, relativeTo);
            var after = __this != null ? __this.rotation : before;
            Record("transform.rotate", __this, typeof(Transform).FullName, before.ToString(), after.ToString(), "after", "Rotate(Vector3,float,Space)");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void RotateAxisAngleSpaceProxy(
            Transform __this,
            Vector3 axis,
            float angle,
            Space relativeTo)
        {
            throw new InvalidOperationException("RotateAxisAngleSpaceProxy must only be called through MonoHook.");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void RotateAxisAngleReplacement(Transform __this, Vector3 axis, float angle)
        {
            var before = __this != null ? __this.rotation : Quaternion.identity;
            RotateAxisAngleProxy(__this, axis, angle);
            var after = __this != null ? __this.rotation : before;
            Record("transform.rotate", __this, typeof(Transform).FullName, before.ToString(), after.ToString(), "after", "Rotate(Vector3,float)");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void RotateAxisAngleProxy(Transform __this, Vector3 axis, float angle)
        {
            throw new InvalidOperationException("RotateAxisAngleProxy must only be called through MonoHook.");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void RotateAroundReplacement(Transform __this, Vector3 point, Vector3 axis, float angle)
        {
            var before = __this != null ? __this.position + " | " + __this.rotation : string.Empty;
            RotateAroundProxy(__this, point, axis, angle);
            var after = __this != null ? __this.position + " | " + __this.rotation : string.Empty;
            Record("transform.rotateAround", __this, typeof(Transform).FullName, before, after, "after", "RotateAround(Vector3,Vector3,float)");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void RotateAroundProxy(Transform __this, Vector3 point, Vector3 axis, float angle)
        {
            throw new InvalidOperationException("RotateAroundProxy must only be called through MonoHook.");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void RotateAroundLegacyReplacement(Transform __this, Vector3 axis, float angle)
        {
            var before = __this != null ? __this.position + " | " + __this.rotation : string.Empty;
            RotateAroundLegacyProxy(__this, axis, angle);
            var after = __this != null ? __this.position + " | " + __this.rotation : string.Empty;
            Record("transform.rotateAround", __this, typeof(Transform).FullName, before, after, "after", "RotateAround(Vector3,float)");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void RotateAroundLegacyProxy(Transform __this, Vector3 axis, float angle)
        {
            throw new InvalidOperationException("RotateAroundLegacyProxy must only be called through MonoHook.");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void LookAtReplacement(Transform __this, Transform target, Vector3 worldUp)
        {
            var before = __this != null ? __this.rotation : Quaternion.identity;
            LookAtProxy(__this, target, worldUp);
            var after = __this != null ? __this.rotation : before;
            Record("transform.lookAt", __this, typeof(Transform).FullName, before.ToString(), after.ToString(), "after", "LookAt(Transform,Vector3)");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void LookAtProxy(Transform __this, Transform target, Vector3 worldUp)
        {
            throw new InvalidOperationException("LookAtProxy must only be called through MonoHook.");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void LookAtPositionUpReplacement(
            Transform __this,
            Vector3 worldPosition,
            Vector3 worldUp)
        {
            var before = __this != null ? __this.rotation : Quaternion.identity;
            LookAtPositionUpProxy(__this, worldPosition, worldUp);
            var after = __this != null ? __this.rotation : before;
            Record("transform.lookAt", __this, typeof(Transform).FullName, before.ToString(), after.ToString(), "after", "LookAt(Vector3,Vector3)");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void LookAtPositionUpProxy(
            Transform __this,
            Vector3 worldPosition,
            Vector3 worldUp)
        {
            throw new InvalidOperationException("LookAtPositionUpProxy must only be called through MonoHook.");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void LookAtPositionReplacement(Transform __this, Vector3 worldPosition)
        {
            var before = __this != null ? __this.rotation : Quaternion.identity;
            LookAtPositionProxy(__this, worldPosition);
            var after = __this != null ? __this.rotation : before;
            Record("transform.lookAt", __this, typeof(Transform).FullName, before.ToString(), after.ToString(), "after", "LookAt(Vector3)");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void LookAtPositionProxy(Transform __this, Vector3 worldPosition)
        {
            throw new InvalidOperationException("LookAtPositionProxy must only be called through MonoHook.");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void LookAtTransformReplacement(Transform __this, Transform target)
        {
            var before = __this != null ? __this.rotation : Quaternion.identity;
            LookAtTransformProxy(__this, target);
            var after = __this != null ? __this.rotation : before;
            Record("transform.lookAt", __this, typeof(Transform).FullName, before.ToString(), after.ToString(), "after", "LookAt(Transform)");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void LookAtTransformProxy(Transform __this, Transform target)
        {
            throw new InvalidOperationException("LookAtTransformProxy must only be called through MonoHook.");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void SetAsFirstSiblingReplacement(Transform __this)
        {
            var before = __this != null ? __this.GetSiblingIndex().ToString() : string.Empty;
            SetAsFirstSiblingProxy(__this);
            var after = __this != null ? __this.GetSiblingIndex().ToString() : string.Empty;
            Record("transform.setAsFirstSibling", __this, typeof(Transform).FullName, before, after);
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void SetAsFirstSiblingProxy(Transform __this)
        {
            throw new InvalidOperationException("SetAsFirstSiblingProxy must only be called through MonoHook.");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void SetAsLastSiblingReplacement(Transform __this)
        {
            var before = __this != null ? __this.GetSiblingIndex().ToString() : string.Empty;
            SetAsLastSiblingProxy(__this);
            var after = __this != null ? __this.GetSiblingIndex().ToString() : string.Empty;
            Record("transform.setAsLastSibling", __this, typeof(Transform).FullName, before, after);
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void SetAsLastSiblingProxy(Transform __this)
        {
            throw new InvalidOperationException("SetAsLastSiblingProxy must only be called through MonoHook.");
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void DetachChildrenReplacement(Transform __this)
        {
            var before = __this != null ? __this.childCount.ToString() : string.Empty;
            DetachChildrenProxy(__this);
            var after = __this != null ? __this.childCount.ToString() : string.Empty;
            Record("transform.detachChildren", __this, typeof(Transform).FullName, before, after);
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void DetachChildrenProxy(Transform __this)
        {
            throw new InvalidOperationException("DetachChildrenProxy must only be called through MonoHook.");
        }
    }
}
