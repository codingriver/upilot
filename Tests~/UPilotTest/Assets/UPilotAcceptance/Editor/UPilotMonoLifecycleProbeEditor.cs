// UPilot standard test project - manual probe Inspector.
// SPDX-License-Identifier: MIT

using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot.Tests
{
    [CustomEditor(typeof(UPilotMonoLifecycleProbe))]
    internal sealed class UPilotMonoLifecycleProbeEditor : Editor
    {
        private SerializedProperty _countCallbacks;
        private SerializedProperty _logToConsole;
        private SerializedProperty _logGroups;
        private SerializedProperty _callbackMask;
        private SerializedProperty _includeArguments;
        private SerializedProperty _maxConsoleLogsPerSecond;
        private SerializedProperty _allowStateMutations;
        private SerializedProperty _positionStep;
        private SerializedProperty _rotationStep;
        private SerializedProperty _scaleMultiplier;

        private void OnEnable()
        {
            _countCallbacks = serializedObject.FindProperty("countCallbacks");
            _logToConsole = serializedObject.FindProperty("logToConsole");
            _logGroups = serializedObject.FindProperty("logGroups");
            _callbackMask = serializedObject.FindProperty("callbackMask");
            _includeArguments = serializedObject.FindProperty("includeArguments");
            _maxConsoleLogsPerSecond = serializedObject.FindProperty("maxConsoleLogsPerSecond");
            _allowStateMutations = serializedObject.FindProperty("allowStateMutations");
            _positionStep = serializedObject.FindProperty("positionStep");
            _rotationStep = serializedObject.FindProperty("rotationStep");
            _scaleMultiplier = serializedObject.FindProperty("scaleMultiplier");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.LabelField("日志输出", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_countCallbacks, new GUIContent("统计回调"));
            EditorGUILayout.PropertyField(_logToConsole, new GUIContent("打印 Console"));
            EditorGUILayout.PropertyField(_includeArguments, new GUIContent("显示参数"));
            EditorGUILayout.PropertyField(_maxConsoleLogsPerSecond, new GUIContent("Console 上限"));

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("事件组", EditorStyles.boldLabel);
            DrawGroupToggle("Lifecycle", UPilotMonoLifecycleProbe.ProbeLogGroups.Lifecycle);
            DrawGroupToggle("Frame", UPilotMonoLifecycleProbe.ProbeLogGroups.Frame);
            DrawGroupToggle("Application", UPilotMonoLifecycleProbe.ProbeLogGroups.Application);
            DrawGroupToggle("Hierarchy", UPilotMonoLifecycleProbe.ProbeLogGroups.Hierarchy);
            DrawGroupToggle("Physics", UPilotMonoLifecycleProbe.ProbeLogGroups.Physics);
            DrawGroupToggle("Rendering", UPilotMonoLifecycleProbe.ProbeLogGroups.Rendering);
            DrawGroupToggle("UI", UPilotMonoLifecycleProbe.ProbeLogGroups.Ui);
            DrawGroupToggle("Input", UPilotMonoLifecycleProbe.ProbeLogGroups.Input);
            DrawGroupToggle("Animation / Particle", UPilotMonoLifecycleProbe.ProbeLogGroups.Animation);
            DrawGroupToggle("Editor", UPilotMonoLifecycleProbe.ProbeLogGroups.Editor);
            DrawGroupToggle("Audio", UPilotMonoLifecycleProbe.ProbeLogGroups.Audio);

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("核心事件", EditorStyles.boldLabel);
            DrawCallbackToggle("Awake", UPilotMonoLifecycleProbe.ProbeCallbackMask.Awake);
            DrawCallbackToggle("OnEnable", UPilotMonoLifecycleProbe.ProbeCallbackMask.OnEnable);
            DrawCallbackToggle("Start", UPilotMonoLifecycleProbe.ProbeCallbackMask.Start);
            DrawCallbackToggle("OnDisable", UPilotMonoLifecycleProbe.ProbeCallbackMask.OnDisable);
            DrawCallbackToggle("OnDestroy", UPilotMonoLifecycleProbe.ProbeCallbackMask.OnDestroy);
            DrawCallbackToggle("FixedUpdate", UPilotMonoLifecycleProbe.ProbeCallbackMask.FixedUpdate);
            DrawCallbackToggle("Update", UPilotMonoLifecycleProbe.ProbeCallbackMask.Update);
            DrawCallbackToggle("LateUpdate", UPilotMonoLifecycleProbe.ProbeCallbackMask.LateUpdate);
            DrawCallbackToggle("OnApplicationFocus", UPilotMonoLifecycleProbe.ProbeCallbackMask.OnApplicationFocus);
            DrawCallbackToggle("OnApplicationPause", UPilotMonoLifecycleProbe.ProbeCallbackMask.OnApplicationPause);
            DrawCallbackToggle("OnApplicationQuit", UPilotMonoLifecycleProbe.ProbeCallbackMask.OnApplicationQuit);
            DrawCallbackToggle("OnTransformParentChanged", UPilotMonoLifecycleProbe.ProbeCallbackMask.OnTransformParentChanged);
            DrawCallbackToggle("OnTransformChildrenChanged", UPilotMonoLifecycleProbe.ProbeCallbackMask.OnTransformChildrenChanged);
            DrawCallbackToggle("OnCollisionEnter", UPilotMonoLifecycleProbe.ProbeCallbackMask.OnCollisionEnter);
            DrawCallbackToggle("OnCollisionExit", UPilotMonoLifecycleProbe.ProbeCallbackMask.OnCollisionExit);
            DrawCallbackToggle("OnTriggerEnter", UPilotMonoLifecycleProbe.ProbeCallbackMask.OnTriggerEnter);
            DrawCallbackToggle("OnTriggerExit", UPilotMonoLifecycleProbe.ProbeCallbackMask.OnTriggerExit);
            DrawCallbackToggle("OnBecameVisible", UPilotMonoLifecycleProbe.ProbeCallbackMask.OnBecameVisible);
            DrawCallbackToggle("OnBecameInvisible", UPilotMonoLifecycleProbe.ProbeCallbackMask.OnBecameInvisible);
            DrawCallbackToggle("OnGUI", UPilotMonoLifecycleProbe.ProbeCallbackMask.OnGUI);
            DrawCallbackToggle("OnMouseDown", UPilotMonoLifecycleProbe.ProbeCallbackMask.OnMouseDown);
            DrawCallbackToggle("OnMouseUp", UPilotMonoLifecycleProbe.ProbeCallbackMask.OnMouseUp);
            DrawCallbackToggle("OnAnimatorMove", UPilotMonoLifecycleProbe.ProbeCallbackMask.OnAnimatorMove);
            DrawCallbackToggle("OnParticleSystemStopped", UPilotMonoLifecycleProbe.ProbeCallbackMask.OnParticleSystemStopped);
            DrawCallbackToggle("OnValidate", UPilotMonoLifecycleProbe.ProbeCallbackMask.OnValidate);
            DrawCallbackToggle("OnAudioFilterRead", UPilotMonoLifecycleProbe.ProbeCallbackMask.OnAudioFilterRead);
            DrawCallbackToggle("OnDrawGizmos", UPilotMonoLifecycleProbe.ProbeCallbackMask.OnDrawGizmos);
            DrawCallbackToggle("OnDrawGizmosSelected", UPilotMonoLifecycleProbe.ProbeCallbackMask.OnDrawGizmosSelected);
            DrawCallbackToggle("Other", UPilotMonoLifecycleProbe.ProbeCallbackMask.Other);

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("运行状态", EditorStyles.boldLabel);
            var probe = (UPilotMonoLifecycleProbe)target;
            EditorGUILayout.LabelField("已观察回调", probe.CallbackCount.ToString());
            EditorGUILayout.LabelField("最近回调", string.IsNullOrEmpty(probe.LastCallback) ? "-" : probe.LastCallback);
            EditorGUILayout.LabelField("丢弃日志", probe.ConsoleDroppedCount.ToString());

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("手动触发", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_allowStateMutations, new GUIContent("允许状态修改"));
            using (new EditorGUI.DisabledScope(!_allowStateMutations.boolValue))
            {
                EditorGUILayout.PropertyField(_positionStep, new GUIContent("位移步长"));
                EditorGUILayout.PropertyField(_rotationStep, new GUIContent("旋转增量"));
                EditorGUILayout.PropertyField(_scaleMultiplier, new GUIContent("缩放倍率"));
            }

            if (GUILayout.Button("Emit Manual Probe"))
                probe.EmitManualProbe();
            using (new EditorGUI.DisabledScope(!_allowStateMutations.boolValue))
            {
                if (GUILayout.Button("Toggle GameObject Active"))
                    probe.StimulateToggleGameObjectActive();
                if (GUILayout.Button("Toggle Component Enabled"))
                    probe.StimulateToggleComponentEnabled();
                if (GUILayout.Button("Transform Mutation"))
                    probe.StimulateTransformMutation();
            }

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawGroupToggle(string label, UPilotMonoLifecycleProbe.ProbeLogGroups flag)
        {
            int value = _logGroups.intValue;
            bool enabled = (value & (int)flag) != 0;
            bool next = EditorGUILayout.ToggleLeft(label, enabled);
            if (next != enabled)
                _logGroups.intValue = next ? value | (int)flag : value & ~(int)flag;
        }

        private void DrawCallbackToggle(string label, UPilotMonoLifecycleProbe.ProbeCallbackMask flag)
        {
            int value = _callbackMask.intValue;
            bool enabled = (value & (int)flag) != 0;
            bool next = EditorGUILayout.ToggleLeft(label, enabled);
            if (next != enabled)
                _callbackMask.intValue = next ? value | (int)flag : value & ~(int)flag;
        }
    }
}
