// -----------------------------------------------------------------------
// UPilot Editor - built-in attribute-discovered MonoHook points.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

namespace CodingRiver.UPilot
{
    internal abstract class UPilotBuiltInMonoHookPointBase : UPilotMonoHookPointBase, IUPilotMonoHookCoverageProvider
    {
        protected abstract string PointId { get; }

        public override bool IsInstalled => UPilotMonoHookInstallationService.IsInstalled(PointId);
        public UPilotMonoHookCoverage Coverage => UPilotMonoHookInstallationService.GetCoverage(PointId);

        public override UPilotMonoHookSupport CheckSupport(UPilotMonoHookContext context)
        {
            return UPilotMonoHookInstallationService.CheckSupport(PointId);
        }

        protected override void InstallCore(UPilotMonoHookContext context)
        {
            UPilotMonoHookInstallationService.InstallPoint(PointId);
        }

        protected override void UninstallCore(UPilotMonoHookContext context)
        {
            UPilotMonoHookInstallationService.UninstallPoint(PointId);
        }
    }

    [UPilotMonoHookPoint(UPilotMonoHookPointId.LifecycleAwake, "Awake", UPilotMonoHookCategoryId.Lifecycle,
        CategoryDisplayName = "生命周期", CategoryOrder = 100, Order = 10)]
    internal sealed class UPilotAwakeHookPoint : UPilotBuiltInMonoHookPointBase
    {
        protected override string PointId => UPilotMonoHookPointId.LifecycleAwake;
    }

    [UPilotMonoHookPoint(UPilotMonoHookPointId.LifecycleOnEnable, "OnEnable", UPilotMonoHookCategoryId.Lifecycle,
        CategoryDisplayName = "生命周期", CategoryOrder = 100, Order = 20)]
    internal sealed class UPilotOnEnableHookPoint : UPilotBuiltInMonoHookPointBase
    {
        protected override string PointId => UPilotMonoHookPointId.LifecycleOnEnable;
    }

    [UPilotMonoHookPoint(UPilotMonoHookPointId.LifecycleStart, "Start", UPilotMonoHookCategoryId.Lifecycle,
        CategoryDisplayName = "生命周期", CategoryOrder = 100, Order = 30)]
    internal sealed class UPilotStartHookPoint : UPilotBuiltInMonoHookPointBase
    {
        protected override string PointId => UPilotMonoHookPointId.LifecycleStart;
    }

    [UPilotMonoHookPoint(UPilotMonoHookPointId.LifecycleUpdate, "Update", UPilotMonoHookCategoryId.Lifecycle,
        CategoryDisplayName = "生命周期", CategoryOrder = 100, Order = 32, HighFrequency = true)]
    internal sealed class UPilotUpdateHookPoint : UPilotBuiltInMonoHookPointBase
    {
        protected override string PointId => UPilotMonoHookPointId.LifecycleUpdate;
    }

    [UPilotMonoHookPoint(UPilotMonoHookPointId.LifecycleFixedUpdate, "FixedUpdate", UPilotMonoHookCategoryId.Lifecycle,
        CategoryDisplayName = "生命周期", CategoryOrder = 100, Order = 34, HighFrequency = true)]
    internal sealed class UPilotFixedUpdateHookPoint : UPilotBuiltInMonoHookPointBase
    {
        protected override string PointId => UPilotMonoHookPointId.LifecycleFixedUpdate;
    }

    [UPilotMonoHookPoint(UPilotMonoHookPointId.LifecycleLateUpdate, "LateUpdate", UPilotMonoHookCategoryId.Lifecycle,
        CategoryDisplayName = "生命周期", CategoryOrder = 100, Order = 36, HighFrequency = true)]
    internal sealed class UPilotLateUpdateHookPoint : UPilotBuiltInMonoHookPointBase
    {
        protected override string PointId => UPilotMonoHookPointId.LifecycleLateUpdate;
    }

    [UPilotMonoHookPoint(UPilotMonoHookPointId.LifecycleOnDisable, "OnDisable", UPilotMonoHookCategoryId.Lifecycle,
        CategoryDisplayName = "生命周期", CategoryOrder = 100, Order = 40)]
    internal sealed class UPilotOnDisableHookPoint : UPilotBuiltInMonoHookPointBase
    {
        protected override string PointId => UPilotMonoHookPointId.LifecycleOnDisable;
    }

    [UPilotMonoHookPoint(UPilotMonoHookPointId.LifecycleOnDestroy, "OnDestroy", UPilotMonoHookCategoryId.Lifecycle,
        CategoryDisplayName = "生命周期", CategoryOrder = 100, Order = 50)]
    internal sealed class UPilotOnDestroyHookPoint : UPilotBuiltInMonoHookPointBase
    {
        protected override string PointId => UPilotMonoHookPointId.LifecycleOnDestroy;
    }

    [UPilotMonoHookPoint(UPilotMonoHookPointId.GameObjectInstantiate, "Instantiate", UPilotMonoHookCategoryId.GameObject,
        CategoryDisplayName = "GameObject", CategoryOrder = 200, Order = 10)]
    internal sealed class UPilotInstantiateHookPoint : UPilotBuiltInMonoHookPointBase
    {
        protected override string PointId => UPilotMonoHookPointId.GameObjectInstantiate;
    }

    [UPilotMonoHookPoint(UPilotMonoHookPointId.GameObjectDestroy, "Destroy", UPilotMonoHookCategoryId.GameObject,
        CategoryDisplayName = "GameObject", CategoryOrder = 200, Order = 20)]
    internal sealed class UPilotDestroyHookPoint : UPilotBuiltInMonoHookPointBase
    {
        protected override string PointId => UPilotMonoHookPointId.GameObjectDestroy;
    }

    [UPilotMonoHookPoint(UPilotMonoHookPointId.GameObjectSetActive, "SetActive", UPilotMonoHookCategoryId.GameObject,
        CategoryDisplayName = "GameObject", CategoryOrder = 200, Order = 30)]
    internal sealed class UPilotSetActiveHookPoint : UPilotBuiltInMonoHookPointBase
    {
        protected override string PointId => UPilotMonoHookPointId.GameObjectSetActive;
    }

    [UPilotMonoHookPoint(UPilotMonoHookPointId.GameObjectAddComponent, "AddComponent", UPilotMonoHookCategoryId.GameObject,
        CategoryDisplayName = "GameObject", CategoryOrder = 200, Order = 40)]
    internal sealed class UPilotAddComponentHookPoint : UPilotBuiltInMonoHookPointBase
    {
        protected override string PointId => UPilotMonoHookPointId.GameObjectAddComponent;
    }

    [UPilotMonoHookPoint(UPilotMonoHookPointId.ComponentBehaviourEnabled, "Behaviour.enabled", UPilotMonoHookCategoryId.Component,
        CategoryDisplayName = "组件", CategoryOrder = 300, Order = 10)]
    internal sealed class UPilotBehaviourEnabledHookPoint : UPilotBuiltInMonoHookPointBase
    {
        protected override string PointId => UPilotMonoHookPointId.ComponentBehaviourEnabled;
    }

    [UPilotMonoHookPoint(UPilotMonoHookPointId.ComponentRendererEnabled, "Renderer.enabled", UPilotMonoHookCategoryId.Component,
        CategoryDisplayName = "组件", CategoryOrder = 300, Order = 20)]
    internal sealed class UPilotRendererEnabledHookPoint : UPilotBuiltInMonoHookPointBase
    {
        protected override string PointId => UPilotMonoHookPointId.ComponentRendererEnabled;
    }

    [UPilotMonoHookPoint(UPilotMonoHookPointId.ComponentColliderEnabled, "Collider.enabled", UPilotMonoHookCategoryId.Component,
        CategoryDisplayName = "组件", CategoryOrder = 300, Order = 30)]
    internal sealed class UPilotColliderEnabledHookPoint : UPilotBuiltInMonoHookPointBase
    {
        protected override string PointId => UPilotMonoHookPointId.ComponentColliderEnabled;
    }

    [UPilotMonoHookPoint(UPilotMonoHookPointId.ComponentCollider2DEnabled, "Collider2D.enabled", UPilotMonoHookCategoryId.Component,
        CategoryDisplayName = "组件", CategoryOrder = 300, Order = 40)]
    internal sealed class UPilotCollider2DEnabledHookPoint : UPilotBuiltInMonoHookPointBase
    {
        protected override string PointId => UPilotMonoHookPointId.ComponentCollider2DEnabled;
    }

    [UPilotMonoHookPoint(UPilotMonoHookPointId.TransformPosition, "position", UPilotMonoHookCategoryId.Transform,
        CategoryDisplayName = "Transform", CategoryOrder = 400, Order = 10, HighFrequency = true)]
    internal sealed class UPilotTransformPositionHookPoint : UPilotBuiltInMonoHookPointBase
    {
        protected override string PointId => UPilotMonoHookPointId.TransformPosition;
    }

    [UPilotMonoHookPoint(UPilotMonoHookPointId.TransformLocalPosition, "localPosition", UPilotMonoHookCategoryId.Transform,
        CategoryDisplayName = "Transform", CategoryOrder = 400, Order = 20, HighFrequency = true)]
    internal sealed class UPilotTransformLocalPositionHookPoint : UPilotBuiltInMonoHookPointBase
    {
        protected override string PointId => UPilotMonoHookPointId.TransformLocalPosition;
    }

    [UPilotMonoHookPoint(UPilotMonoHookPointId.TransformRotation, "rotation", UPilotMonoHookCategoryId.Transform,
        CategoryDisplayName = "Transform", CategoryOrder = 400, Order = 30, HighFrequency = true)]
    internal sealed class UPilotTransformRotationHookPoint : UPilotBuiltInMonoHookPointBase
    {
        protected override string PointId => UPilotMonoHookPointId.TransformRotation;
    }

    [UPilotMonoHookPoint(UPilotMonoHookPointId.TransformLocalRotation, "localRotation", UPilotMonoHookCategoryId.Transform,
        CategoryDisplayName = "Transform", CategoryOrder = 400, Order = 40, HighFrequency = true)]
    internal sealed class UPilotTransformLocalRotationHookPoint : UPilotBuiltInMonoHookPointBase
    {
        protected override string PointId => UPilotMonoHookPointId.TransformLocalRotation;
    }

    [UPilotMonoHookPoint(UPilotMonoHookPointId.TransformEulerAngles, "eulerAngles", UPilotMonoHookCategoryId.Transform,
        CategoryDisplayName = "Transform", CategoryOrder = 400, Order = 50, HighFrequency = true)]
    internal sealed class UPilotTransformEulerAnglesHookPoint : UPilotBuiltInMonoHookPointBase
    {
        protected override string PointId => UPilotMonoHookPointId.TransformEulerAngles;
    }

    [UPilotMonoHookPoint(UPilotMonoHookPointId.TransformLocalEulerAngles, "localEulerAngles", UPilotMonoHookCategoryId.Transform,
        CategoryDisplayName = "Transform", CategoryOrder = 400, Order = 60, HighFrequency = true)]
    internal sealed class UPilotTransformLocalEulerAnglesHookPoint : UPilotBuiltInMonoHookPointBase
    {
        protected override string PointId => UPilotMonoHookPointId.TransformLocalEulerAngles;
    }

    [UPilotMonoHookPoint(UPilotMonoHookPointId.TransformLocalScale, "localScale", UPilotMonoHookCategoryId.Transform,
        CategoryDisplayName = "Transform", CategoryOrder = 400, Order = 70, HighFrequency = true)]
    internal sealed class UPilotTransformLocalScaleHookPoint : UPilotBuiltInMonoHookPointBase
    {
        protected override string PointId => UPilotMonoHookPointId.TransformLocalScale;
    }

    [UPilotMonoHookPoint(UPilotMonoHookPointId.TransformSetPositionAndRotation, "SetPositionAndRotation", UPilotMonoHookCategoryId.Transform,
        CategoryDisplayName = "Transform", CategoryOrder = 400, Order = 80, HighFrequency = true)]
    internal sealed class UPilotTransformSetPositionAndRotationHookPoint : UPilotBuiltInMonoHookPointBase
    {
        protected override string PointId => UPilotMonoHookPointId.TransformSetPositionAndRotation;
    }

    [UPilotMonoHookPoint(UPilotMonoHookPointId.TransformSetLocalPositionAndRotation, "SetLocalPositionAndRotation", UPilotMonoHookCategoryId.Transform,
        CategoryDisplayName = "Transform", CategoryOrder = 400, Order = 90, HighFrequency = true)]
    internal sealed class UPilotTransformSetLocalPositionAndRotationHookPoint : UPilotBuiltInMonoHookPointBase
    {
        protected override string PointId => UPilotMonoHookPointId.TransformSetLocalPositionAndRotation;
    }

    [UPilotMonoHookPoint(UPilotMonoHookPointId.TransformSetParent, "SetParent", UPilotMonoHookCategoryId.Transform,
        CategoryDisplayName = "Transform", CategoryOrder = 400, Order = 100)]
    internal sealed class UPilotTransformSetParentHookPoint : UPilotBuiltInMonoHookPointBase
    {
        protected override string PointId => UPilotMonoHookPointId.TransformSetParent;
    }

    [UPilotMonoHookPoint(UPilotMonoHookPointId.TransformSetSiblingIndex, "SetSiblingIndex", UPilotMonoHookCategoryId.Transform,
        CategoryDisplayName = "Transform", CategoryOrder = 400, Order = 110)]
    internal sealed class UPilotTransformSetSiblingIndexHookPoint : UPilotBuiltInMonoHookPointBase
    {
        protected override string PointId => UPilotMonoHookPointId.TransformSetSiblingIndex;
    }

    [UPilotMonoHookPoint(UPilotMonoHookPointId.TransformTranslate, "Translate", UPilotMonoHookCategoryId.Transform,
        CategoryDisplayName = "Transform", CategoryOrder = 400, Order = 120, HighFrequency = true)]
    internal sealed class UPilotTransformTranslateHookPoint : UPilotBuiltInMonoHookPointBase
    {
        protected override string PointId => UPilotMonoHookPointId.TransformTranslate;
    }

    [UPilotMonoHookPoint(UPilotMonoHookPointId.TransformRotate, "Rotate", UPilotMonoHookCategoryId.Transform,
        CategoryDisplayName = "Transform", CategoryOrder = 400, Order = 130, HighFrequency = true)]
    internal sealed class UPilotTransformRotateHookPoint : UPilotBuiltInMonoHookPointBase
    {
        protected override string PointId => UPilotMonoHookPointId.TransformRotate;
    }

    [UPilotMonoHookPoint(UPilotMonoHookPointId.TransformRotateAround, "RotateAround", UPilotMonoHookCategoryId.Transform,
        CategoryDisplayName = "Transform", CategoryOrder = 400, Order = 140, HighFrequency = true)]
    internal sealed class UPilotTransformRotateAroundHookPoint : UPilotBuiltInMonoHookPointBase
    {
        protected override string PointId => UPilotMonoHookPointId.TransformRotateAround;
    }

    [UPilotMonoHookPoint(UPilotMonoHookPointId.TransformLookAt, "LookAt", UPilotMonoHookCategoryId.Transform,
        CategoryDisplayName = "Transform", CategoryOrder = 400, Order = 150, HighFrequency = true)]
    internal sealed class UPilotTransformLookAtHookPoint : UPilotBuiltInMonoHookPointBase
    {
        protected override string PointId => UPilotMonoHookPointId.TransformLookAt;
    }

    [UPilotMonoHookPoint(UPilotMonoHookPointId.TransformSetAsFirstSibling, "SetAsFirstSibling", UPilotMonoHookCategoryId.Transform,
        CategoryDisplayName = "Transform", CategoryOrder = 400, Order = 160)]
    internal sealed class UPilotTransformSetAsFirstSiblingHookPoint : UPilotBuiltInMonoHookPointBase
    {
        protected override string PointId => UPilotMonoHookPointId.TransformSetAsFirstSibling;
    }

    [UPilotMonoHookPoint(UPilotMonoHookPointId.TransformSetAsLastSibling, "SetAsLastSibling", UPilotMonoHookCategoryId.Transform,
        CategoryDisplayName = "Transform", CategoryOrder = 400, Order = 170)]
    internal sealed class UPilotTransformSetAsLastSiblingHookPoint : UPilotBuiltInMonoHookPointBase
    {
        protected override string PointId => UPilotMonoHookPointId.TransformSetAsLastSibling;
    }

    [UPilotMonoHookPoint(UPilotMonoHookPointId.TransformDetachChildren, "DetachChildren", UPilotMonoHookCategoryId.Transform,
        CategoryDisplayName = "Transform", CategoryOrder = 400, Order = 180)]
    internal sealed class UPilotTransformDetachChildrenHookPoint : UPilotBuiltInMonoHookPointBase
    {
        protected override string PointId => UPilotMonoHookPointId.TransformDetachChildren;
    }
}
