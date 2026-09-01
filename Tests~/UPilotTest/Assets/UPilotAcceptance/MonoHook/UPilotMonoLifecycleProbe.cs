// UPilot standard test project - manual UPilot Tracer probe.
// SPDX-License-Identifier: MIT

using System;
using UnityEngine;

namespace CodingRiver.UPilot.Tests
{
    /// <summary>
    /// Attach this component to a GameObject when manually checking UPilot Tracer
    /// lifecycle coverage. The component can count every callback, selectively log
    /// callback groups, and run a few safe Unity state changes from its Inspector.
    /// </summary>
    public sealed class UPilotMonoLifecycleProbe : MonoBehaviour
    {
        [Flags]
        public enum ProbeLogGroups
        {
            None = 0,
            Lifecycle = 1 << 0,
            Frame = 1 << 1,
            Application = 1 << 2,
            Hierarchy = 1 << 3,
            Physics = 1 << 4,
            Rendering = 1 << 5,
            Ui = 1 << 6,
            Input = 1 << 7,
            Animation = 1 << 8,
            Editor = 1 << 9,
            Audio = 1 << 10,
        }

        [Flags]
        public enum ProbeCallbackMask
        {
            None = 0,
            Awake = 1 << 0,
            OnEnable = 1 << 1,
            Start = 1 << 2,
            OnDisable = 1 << 3,
            OnDestroy = 1 << 4,
            FixedUpdate = 1 << 5,
            Update = 1 << 6,
            LateUpdate = 1 << 7,
            OnApplicationFocus = 1 << 8,
            OnApplicationPause = 1 << 9,
            OnApplicationQuit = 1 << 10,
            OnTransformParentChanged = 1 << 11,
            OnTransformChildrenChanged = 1 << 12,
            OnCollisionEnter = 1 << 13,
            OnCollisionExit = 1 << 14,
            OnTriggerEnter = 1 << 15,
            OnTriggerExit = 1 << 16,
            OnBecameVisible = 1 << 17,
            OnBecameInvisible = 1 << 18,
            OnGUI = 1 << 19,
            OnMouseDown = 1 << 20,
            OnMouseUp = 1 << 21,
            OnAnimatorMove = 1 << 22,
            OnParticleSystemStopped = 1 << 23,
            OnValidate = 1 << 24,
            OnAudioFilterRead = 1 << 25,
            OnDrawGizmos = 1 << 26,
            OnDrawGizmosSelected = 1 << 27,
            Other = 1 << 30,
            All = int.MaxValue,
        }

        [Header("日志输出")]
        [Tooltip("开启后统计回调次数；关闭后仅保留最近回调名称。")]
        [SerializeField] private bool countCallbacks = true;
        [Tooltip("开启后按下方事件组和核心事件选择输出 Console 日志。")]
        [SerializeField] private bool logToConsole = true;
        [Tooltip("选择要输出的回调组；回调计数不受此选项影响。")]
        [SerializeField] private ProbeLogGroups logGroups =
            ProbeLogGroups.Lifecycle | ProbeLogGroups.Application | ProbeLogGroups.Hierarchy;
        [Tooltip("核心回调可以单独关闭；Other 包含未列出的 Unity 消息。")]
        [SerializeField] private ProbeCallbackMask callbackMask = ProbeCallbackMask.All;
        [SerializeField] private bool includeArguments = true;
        [Min(1)] [SerializeField] private int maxConsoleLogsPerSecond = 20;

        [Header("手动触发")]
        [Tooltip("允许执行可能修改场景状态的测试动作。默认关闭。")]
        [SerializeField] private bool allowStateMutations;
        [Min(0.01f)] [SerializeField] private float positionStep = 0.5f;
        [SerializeField] private Vector3 rotationStep = new Vector3(0f, 15f, 0f);
        [Min(0.01f)] [SerializeField] private float scaleMultiplier = 1.1f;

        [NonSerialized] private int _callbackCount;
        [NonSerialized] private string _lastCallback = string.Empty;
        [NonSerialized] private float _logWindowStart;
        [NonSerialized] private int _logsInWindow;
        [NonSerialized] private int _consoleDroppedCount;

        public int CallbackCount => _callbackCount;
        public string LastCallback => _lastCallback;
        public int ConsoleDroppedCount => _consoleDroppedCount;

        public void EmitManualProbe() => Emit(ProbeCallbackMask.Other, ProbeLogGroups.Editor, nameof(EmitManualProbe));

        private void Emit(ProbeCallbackMask callback, ProbeLogGroups group, string callbackName, string details = null,
            bool highFrequency = false)
        {
            if (countCallbacks)
                _callbackCount++;
            _lastCallback = callbackName;

            if (!logToConsole || (logGroups & group) == 0 || (callbackMask & callback) == 0)
                return;

            float now = Time.realtimeSinceStartup;
            if (_logWindowStart <= 0f || now - _logWindowStart >= 1f)
            {
                _logWindowStart = now;
                _logsInWindow = 0;
            }

            if (_logsInWindow >= Mathf.Max(1, maxConsoleLogsPerSecond))
            {
                _consoleDroppedCount++;
                return;
            }

            _logsInWindow++;
            string suffix = includeArguments && !string.IsNullOrEmpty(details) ? " " + details : string.Empty;
            Debug.Log("[UPilotMonoLifecycleProbe] " + name + "." + callbackName + suffix, this);
        }

        public void StimulateToggleGameObjectActive()
        {
            if (!allowStateMutations) return;
            gameObject.SetActive(!gameObject.activeSelf);
        }

        public void StimulateToggleComponentEnabled()
        {
            if (!allowStateMutations) return;
            enabled = !enabled;
        }

        public void StimulateTransformMutation()
        {
            if (!allowStateMutations) return;
            transform.position += Vector3.right * positionStep;
            transform.Rotate(rotationStep, Space.Self);
            transform.localScale *= scaleMultiplier;
        }

        // Initialization and lifetime.
        private void Reset() => Emit(ProbeCallbackMask.Other, ProbeLogGroups.Editor, nameof(Reset));
        private void Awake() => Emit(ProbeCallbackMask.Awake, ProbeLogGroups.Lifecycle, nameof(Awake));
        private void OnEnable() => Emit(ProbeCallbackMask.OnEnable, ProbeLogGroups.Lifecycle, nameof(OnEnable));
        private void Start() => Emit(ProbeCallbackMask.Start, ProbeLogGroups.Lifecycle, nameof(Start));
        private void OnDisable() => Emit(ProbeCallbackMask.OnDisable, ProbeLogGroups.Lifecycle, nameof(OnDisable));
        private void OnDestroy() => Emit(ProbeCallbackMask.OnDestroy, ProbeLogGroups.Lifecycle, nameof(OnDestroy));

        // Per-frame callbacks.
        private void FixedUpdate() => Emit(ProbeCallbackMask.FixedUpdate, ProbeLogGroups.Frame, nameof(FixedUpdate), highFrequency: true);
        private void Update() => Emit(ProbeCallbackMask.Update, ProbeLogGroups.Frame, nameof(Update), highFrequency: true);
        private void LateUpdate() => Emit(ProbeCallbackMask.LateUpdate, ProbeLogGroups.Frame, nameof(LateUpdate), highFrequency: true);

        // Application callbacks.
        private void OnApplicationFocus(bool hasFocus) => Emit(ProbeCallbackMask.OnApplicationFocus, ProbeLogGroups.Application, nameof(OnApplicationFocus), hasFocus.ToString());
        private void OnApplicationPause(bool pauseStatus) => Emit(ProbeCallbackMask.OnApplicationPause, ProbeLogGroups.Application, nameof(OnApplicationPause), pauseStatus.ToString());
        private void OnApplicationQuit() => Emit(ProbeCallbackMask.OnApplicationQuit, ProbeLogGroups.Application, nameof(OnApplicationQuit));

        // Transform and hierarchy callbacks.
        private void OnBeforeTransformParentChanged() => Emit(ProbeCallbackMask.Other, ProbeLogGroups.Hierarchy, nameof(OnBeforeTransformParentChanged));
        private void OnTransformParentChanged() => Emit(ProbeCallbackMask.OnTransformParentChanged, ProbeLogGroups.Hierarchy, nameof(OnTransformParentChanged));
        private void OnTransformChildrenChanged() => Emit(ProbeCallbackMask.OnTransformChildrenChanged, ProbeLogGroups.Hierarchy, nameof(OnTransformChildrenChanged));

        // Rendering callbacks.
        private void OnBecameVisible() => Emit(ProbeCallbackMask.OnBecameVisible, ProbeLogGroups.Rendering, nameof(OnBecameVisible));
        private void OnBecameInvisible() => Emit(ProbeCallbackMask.OnBecameInvisible, ProbeLogGroups.Rendering, nameof(OnBecameInvisible));
        private void OnPreCull() => Emit(ProbeCallbackMask.Other, ProbeLogGroups.Rendering, nameof(OnPreCull));
        private void OnPreRender() => Emit(ProbeCallbackMask.Other, ProbeLogGroups.Rendering, nameof(OnPreRender));
        private void OnPostRender() => Emit(ProbeCallbackMask.Other, ProbeLogGroups.Rendering, nameof(OnPostRender));
        private void OnRenderObject() => Emit(ProbeCallbackMask.Other, ProbeLogGroups.Rendering, nameof(OnRenderObject));
        private void OnWillRenderObject() => Emit(ProbeCallbackMask.Other, ProbeLogGroups.Rendering, nameof(OnWillRenderObject));
        private void OnRenderImage(RenderTexture source, RenderTexture destination) => Emit(ProbeCallbackMask.Other, ProbeLogGroups.Rendering, nameof(OnRenderImage));

        // 3D physics callbacks.
        private void OnCollisionEnter(Collision collision) => Emit(ProbeCallbackMask.OnCollisionEnter, ProbeLogGroups.Physics, nameof(OnCollisionEnter));
        private void OnCollisionStay(Collision collision) => Emit(ProbeCallbackMask.Other, ProbeLogGroups.Physics, nameof(OnCollisionStay));
        private void OnCollisionExit(Collision collision) => Emit(ProbeCallbackMask.OnCollisionExit, ProbeLogGroups.Physics, nameof(OnCollisionExit));
        private void OnTriggerEnter(Collider other) => Emit(ProbeCallbackMask.OnTriggerEnter, ProbeLogGroups.Physics, nameof(OnTriggerEnter));
        private void OnTriggerStay(Collider other) => Emit(ProbeCallbackMask.Other, ProbeLogGroups.Physics, nameof(OnTriggerStay));
        private void OnTriggerExit(Collider other) => Emit(ProbeCallbackMask.OnTriggerExit, ProbeLogGroups.Physics, nameof(OnTriggerExit));
        private void OnControllerColliderHit(ControllerColliderHit hit) => Emit(ProbeCallbackMask.Other, ProbeLogGroups.Physics, nameof(OnControllerColliderHit));
        private void OnJointBreak(float breakForce) => Emit(ProbeCallbackMask.Other, ProbeLogGroups.Physics, nameof(OnJointBreak));

        // 2D physics callbacks.
        private void OnCollisionEnter2D(Collision2D collision) => Emit(ProbeCallbackMask.Other, ProbeLogGroups.Physics, nameof(OnCollisionEnter2D));
        private void OnCollisionStay2D(Collision2D collision) => Emit(ProbeCallbackMask.Other, ProbeLogGroups.Physics, nameof(OnCollisionStay2D));
        private void OnCollisionExit2D(Collision2D collision) => Emit(ProbeCallbackMask.Other, ProbeLogGroups.Physics, nameof(OnCollisionExit2D));
        private void OnTriggerEnter2D(Collider2D other) => Emit(ProbeCallbackMask.Other, ProbeLogGroups.Physics, nameof(OnTriggerEnter2D));
        private void OnTriggerStay2D(Collider2D other) => Emit(ProbeCallbackMask.Other, ProbeLogGroups.Physics, nameof(OnTriggerStay2D));
        private void OnTriggerExit2D(Collider2D other) => Emit(ProbeCallbackMask.Other, ProbeLogGroups.Physics, nameof(OnTriggerExit2D));
        private void OnJointBreak2D(Joint2D brokenJoint) => Emit(ProbeCallbackMask.Other, ProbeLogGroups.Physics, nameof(OnJointBreak2D));

        // Animator and particle callbacks.
        private void OnAnimatorMove() => Emit(ProbeCallbackMask.OnAnimatorMove, ProbeLogGroups.Animation, nameof(OnAnimatorMove), highFrequency: true);
        private void OnAnimatorIK(int layerIndex) => Emit(ProbeCallbackMask.Other, ProbeLogGroups.Animation, nameof(OnAnimatorIK));
        private void OnParticleCollision(GameObject other) => Emit(ProbeCallbackMask.Other, ProbeLogGroups.Physics, nameof(OnParticleCollision));
        private void OnParticleTrigger() => Emit(ProbeCallbackMask.Other, ProbeLogGroups.Animation, nameof(OnParticleTrigger));
        private void OnParticleSystemStopped() => Emit(ProbeCallbackMask.OnParticleSystemStopped, ProbeLogGroups.Animation, nameof(OnParticleSystemStopped));

        // Mouse callbacks (requires a Collider on this GameObject).
        private void OnMouseDown() => Emit(ProbeCallbackMask.OnMouseDown, ProbeLogGroups.Input, nameof(OnMouseDown));
        private void OnMouseUp() => Emit(ProbeCallbackMask.OnMouseUp, ProbeLogGroups.Input, nameof(OnMouseUp));
        private void OnMouseEnter() => Emit(ProbeCallbackMask.Other, ProbeLogGroups.Input, nameof(OnMouseEnter));
        private void OnMouseExit() => Emit(ProbeCallbackMask.Other, ProbeLogGroups.Input, nameof(OnMouseExit));
        private void OnMouseOver() => Emit(ProbeCallbackMask.Other, ProbeLogGroups.Input, nameof(OnMouseOver), highFrequency: true);
        private void OnMouseDrag() => Emit(ProbeCallbackMask.Other, ProbeLogGroups.Input, nameof(OnMouseDrag), highFrequency: true);

        // UI/animation callbacks.
        private void OnRectTransformDimensionsChange() => Emit(ProbeCallbackMask.Other, ProbeLogGroups.Ui, nameof(OnRectTransformDimensionsChange));
        private void OnRectTransformRemoved() => Emit(ProbeCallbackMask.Other, ProbeLogGroups.Ui, nameof(OnRectTransformRemoved));
        private void OnCanvasGroupChanged() => Emit(ProbeCallbackMask.Other, ProbeLogGroups.Ui, nameof(OnCanvasGroupChanged));
        private void OnCanvasHierarchyChanged() => Emit(ProbeCallbackMask.Other, ProbeLogGroups.Ui, nameof(OnCanvasHierarchyChanged));
        private void OnDidApplyAnimationProperties() => Emit(ProbeCallbackMask.Other, ProbeLogGroups.Ui, nameof(OnDidApplyAnimationProperties));

        // Editor and GUI callbacks.
        private void OnGUI() => Emit(ProbeCallbackMask.OnGUI, ProbeLogGroups.Editor, nameof(OnGUI), highFrequency: true);
        private void OnValidate() => Emit(ProbeCallbackMask.OnValidate, ProbeLogGroups.Editor, nameof(OnValidate));
        private void OnDrawGizmos() => Emit(ProbeCallbackMask.OnDrawGizmos, ProbeLogGroups.Editor, nameof(OnDrawGizmos), highFrequency: true);
        private void OnDrawGizmosSelected() => Emit(ProbeCallbackMask.OnDrawGizmosSelected, ProbeLogGroups.Editor, nameof(OnDrawGizmosSelected), highFrequency: true);

        // Audio callback (requires an AudioSource).
        private void OnAudioFilterRead(float[] data, int channels) => Emit(ProbeCallbackMask.OnAudioFilterRead, ProbeLogGroups.Audio, nameof(OnAudioFilterRead), highFrequency: true);
    }
}
