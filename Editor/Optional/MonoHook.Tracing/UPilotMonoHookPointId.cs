// -----------------------------------------------------------------------
// UPilot Editor - manually managed MonoHook point identifiers.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

namespace CodingRiver.UPilot
{
    public static class UPilotMonoHookPointId
    {
        public const string LifecycleAwake = "lifecycle.awake";
        public const string LifecycleOnEnable = "lifecycle.onEnable";
        public const string LifecycleStart = "lifecycle.start";
        public const string LifecycleUpdate = "lifecycle.update";
        public const string LifecycleFixedUpdate = "lifecycle.fixedUpdate";
        public const string LifecycleLateUpdate = "lifecycle.lateUpdate";
        public const string LifecycleOnDisable = "lifecycle.onDisable";
        public const string LifecycleOnDestroy = "lifecycle.onDestroy";

        public const string GameObjectInstantiate = "gameObject.instantiate";
        public const string GameObjectDestroy = "gameObject.destroy";
        public const string GameObjectSetActive = "gameObject.setActive";
        public const string GameObjectAddComponent = "gameObject.addComponent";

        public const string ComponentBehaviourEnabled = "component.behaviourEnabled";
        public const string ComponentRendererEnabled = "component.rendererEnabled";
        public const string ComponentColliderEnabled = "component.colliderEnabled";
        public const string ComponentCollider2DEnabled = "component.collider2DEnabled";

        public const string TransformPosition = "transform.position";
        public const string TransformLocalPosition = "transform.localPosition";
        public const string TransformRotation = "transform.rotation";
        public const string TransformLocalRotation = "transform.localRotation";
        public const string TransformEulerAngles = "transform.eulerAngles";
        public const string TransformLocalEulerAngles = "transform.localEulerAngles";
        public const string TransformLocalScale = "transform.localScale";
        public const string TransformSetPositionAndRotation = "transform.setPositionAndRotation";
        public const string TransformSetLocalPositionAndRotation = "transform.setLocalPositionAndRotation";
        public const string TransformSetParent = "transform.setParent";
        public const string TransformSetSiblingIndex = "transform.setSiblingIndex";
        public const string TransformTranslate = "transform.translate";
        public const string TransformRotate = "transform.rotate";
        public const string TransformRotateAround = "transform.rotateAround";
        public const string TransformLookAt = "transform.lookAt";
        public const string TransformSetAsFirstSibling = "transform.setAsFirstSibling";
        public const string TransformSetAsLastSibling = "transform.setAsLastSibling";
        public const string TransformDetachChildren = "transform.detachChildren";

        public static bool IsBuiltIn(string pointId)
        {
            switch (pointId)
            {
                case LifecycleAwake:
                case LifecycleOnEnable:
                case LifecycleStart:
                case LifecycleUpdate:
                case LifecycleFixedUpdate:
                case LifecycleLateUpdate:
                case LifecycleOnDisable:
                case LifecycleOnDestroy:
                case GameObjectInstantiate:
                case GameObjectDestroy:
                case GameObjectSetActive:
                case GameObjectAddComponent:
                case ComponentBehaviourEnabled:
                case ComponentRendererEnabled:
                case ComponentColliderEnabled:
                case ComponentCollider2DEnabled:
                case TransformPosition:
                case TransformLocalPosition:
                case TransformRotation:
                case TransformLocalRotation:
                case TransformEulerAngles:
                case TransformLocalEulerAngles:
                case TransformLocalScale:
                case TransformSetPositionAndRotation:
                case TransformSetLocalPositionAndRotation:
                case TransformSetParent:
                case TransformSetSiblingIndex:
                case TransformTranslate:
                case TransformRotate:
                case TransformRotateAround:
                case TransformLookAt:
                case TransformSetAsFirstSibling:
                case TransformSetAsLastSibling:
                case TransformDetachChildren:
                    return true;
                default:
                    return false;
            }
        }
    }
}
