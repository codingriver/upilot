using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot.Automation
{
    [Serializable]
    public sealed class AutomationStepGizmos
    {
        public bool scene;
        public bool game;
    }

    [Serializable]
    public sealed class AutomationGizmosObservation
    {
        public string phase, windowId, view, status, code, message;
        public bool desired, actualKnown, actual, changed;
    }

    [Serializable]
    public sealed class AutomationGizmosState
    {
        public int policyVersion = 1;
        public bool initializationAttempted, initializationCompleted, finalCloseAttempted, finalCloseVerified;
        public List<AutomationGizmosObservation> observations = new();
    }

    internal sealed class AutomationGizmosWindow
    {
        internal string id, view;
        internal EditorWindow native;
    }

    // Kept internal so isolated executor tests never mutate the actual Editor UI.
    internal interface IAutomationGizmosAccess
    {
        AutomationGizmosWindow[] Find(bool scene, bool game);
        bool Read(AutomationGizmosWindow window);
        void Set(AutomationGizmosWindow window, bool enabled);
    }

    internal sealed class UnityAutomationGizmosAccess : IAutomationGizmosAccess
    {
        private static readonly Type GameViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
        private static readonly PropertyInfo GameDrawGizmos = GameViewType?.GetProperty("drawGizmos",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        public AutomationGizmosWindow[] Find(bool scene, bool game) => Resources.FindObjectsOfTypeAll<EditorWindow>()
            .Where(w => w != null && ((scene && w is SceneView) || (game && w.GetType().FullName == "UnityEditor.GameView")))
            .Select(w => new AutomationGizmosWindow
            {
                id = UPilotEntityIds.ToWireId(w).ToString(CultureInfo.InvariantCulture),
                view = w is SceneView ? "scene" : "game", native = w,
            }).ToArray();

        public bool Read(AutomationGizmosWindow window)
        {
            RequireWindow(window);
            if (window.native is SceneView scene) return scene.drawGizmos;
            if (GameDrawGizmos?.GetGetMethod(true) == null) throw new MissingMemberException("GameView.drawGizmos getter is unavailable.");
            return (bool)GameDrawGizmos.GetValue(window.native);
        }

        public void Set(AutomationGizmosWindow window, bool enabled)
        {
            RequireWindow(window);
            if (window.native is SceneView scene) scene.drawGizmos = enabled;
            else
            {
                if (GameDrawGizmos?.GetSetMethod(true) == null) throw new MissingMemberException("GameView.drawGizmos setter is unavailable.");
                GameDrawGizmos.SetValue(window.native, enabled);
            }
            window.native.Repaint();
        }

        private static void RequireWindow(AutomationGizmosWindow window)
        {
            if (window.native == null || UPilotEntityIds.ToWireId(window.native).ToString(CultureInfo.InvariantCulture) != window.id)
                throw new InvalidOperationException("Gizmos target window is no longer available.");
        }
    }

    public sealed partial class AutomationStepExecutor
    {
        private readonly IAutomationGizmosAccess _gizmosAccess;
        private bool ManagesGizmos => _run?.gizmosPolicyVersion == 1 && _gizmosAccess != null;
        private bool HasGizmosWarnings => _run?.gizmosState?.observations?.Any(o => o.status == "Warning") == true;

        private void InitializeGizmos()
        {
            if (!ManagesGizmos) return;
            _run.gizmosState = new AutomationGizmosState { initializationAttempted = true };
            _store.Save(_run); // Intent is durable before any Editor setting changes.
            ApplyGizmosGroup("initialize", false, true, true);
            _run.gizmosState.initializationCompleted = true;
            _store.Save(_run);
        }

        private void ObserveInterruptedGizmosInitialization()
        {
            if (!ManagesGizmos || _run.gizmosState?.initializationCompleted == true) return;
            // Reload observes the original attempt, never performs the task-start scan again.
            AddGizmosWarning("initialize", "", "", false, "GIZMOS_INITIALIZATION_UNVERIFIED",
                "Task-start Gizmos initialization did not finish before interruption; it was not replayed.");
            _store.Save(_run);
        }

        private void EnableStepGizmos()
        {
            if (!ManagesGizmos || Current.gizmosEnableAttempted || Item.gizmos == null
                || (!Item.gizmos.scene && !Item.gizmos.game)) return;
            Current.gizmosEnableAttempted = true;
            var phase = "enable:" + Item.instanceId;
            var windows = FindGizmosWindows(phase, true, Item.gizmos.scene, Item.gizmos.game);
            Current.gizmosWindowIds = windows.Select(w => w.id).ToArray();
            _store.Save(_run); // Retain exact targets, including an uncertain setter, for bounded close.
            foreach (var window in windows)
            {
                if (_run.terminal) break;
                ApplyGizmosWindow(phase, window, true);
            }
            _store.Save(_run);
        }

        private void CloseStepGizmos()
        {
            if (!ManagesGizmos || !Current.gizmosEnableAttempted || Current.gizmosCloseAttempted) return;
            Current.gizmosCloseAttempted = true;
            _store.Save(_run);
            if (Current.gizmosWindowIds?.Length > 0)
                ApplyGizmosGroup("disable:" + Item.instanceId, false, Item.gizmos.scene, Item.gizmos.game, Current.gizmosWindowIds);
            _store.Save(_run);
        }

        private void FinalizeGizmos(Func<bool> withinDeadline = null)
        {
            if (!ManagesGizmos) return;
            if (withinDeadline != null && !withinDeadline()) return;
            if (_run.gizmosState?.finalCloseAttempted == true)
            {
                if (!_run.gizmosState.finalCloseVerified && !_run.gizmosState.observations.Any(o => o.phase == "finalize"))
                {
                    AddGizmosWarning("finalize", "", "", false, "GIZMOS_FINAL_CLOSE_UNVERIFIED",
                        "Final Gizmos close was interrupted before verification; the original attempt was not replayed.");
                    _store.Save(_run);
                }
                return;
            }
            _run.gizmosState ??= new AutomationGizmosState();
            _run.gizmosState.finalCloseAttempted = true;
            _store.Save(_run);
            bool complete = ApplyGizmosGroup("finalize", false, true, true, withinDeadline: withinDeadline);
            _run.gizmosState.finalCloseVerified = complete && !_run.gizmosState.observations.Any(o => o.phase == "finalize" && o.status == "Warning");
            _store.Save(_run);
        }

        private AutomationGizmosWindow[] FindGizmosWindows(string phase, bool desired, bool scene, bool game)
        {
            try
            {
                var windows = _gizmosAccess.Find(scene, game);
                if (windows.Length == 0) RecordGizmos(new AutomationGizmosObservation
                { phase = phase, windowId = "", view = scene && game ? "scene/game" : scene ? "scene" : "game", desired = desired, status = "NoTargets" });
                return windows;
            }
            catch (Exception ex)
            {
                AddGizmosWarning(phase, "", "", desired, "GIZMOS_ENUMERATION_FAILED", ex.ToString());
                return Array.Empty<AutomationGizmosWindow>();
            }
        }

        private bool ApplyGizmosGroup(string phase, bool desired, bool scene, bool game, string[] onlyIds = null, Func<bool> withinDeadline = null)
        {
            if (withinDeadline != null && !withinDeadline()) return false;
            foreach (var window in FindGizmosWindows(phase, desired, scene, game))
                if ((onlyIds == null || onlyIds.Contains(window.id)) && !ApplyGizmosWindow(phase, window, desired, withinDeadline)) return false;
            return true;
        }

        private bool ApplyGizmosWindow(string phase, AutomationGizmosWindow window, bool desired, Func<bool> withinDeadline = null)
        {
            if (withinDeadline != null && !withinDeadline()) return false;
            var observation = new AutomationGizmosObservation
            { phase = phase, windowId = window.id, view = window.view, desired = desired };
            string stage = "READ";
            try
            {
                observation.actual = _gizmosAccess.Read(window);
                observation.actualKnown = true;
                if (observation.actual != desired)
                {
                    if (withinDeadline != null && !withinDeadline())
                    {
                        observation.status = "Warning"; observation.code = "GIZMOS_RELEASE_DEADLINE";
                        observation.message = "The original cleanup deadline expired before the Gizmos setter; no setter was dispatched.";
                        RecordGizmos(observation);
                        return false;
                    }
                    stage = "SET";
                    observation.actualKnown = false;
                    _gizmosAccess.Set(window, desired);
                    observation.changed = true;
                    stage = "VERIFY";
                    observation.actual = _gizmosAccess.Read(window);
                    observation.actualKnown = true;
                    if (observation.actual != desired) throw new InvalidOperationException("Gizmos state did not match the requested value.");
                }
                observation.status = "Verified";
            }
            catch (Exception ex)
            {
                observation.status = "Warning";
                observation.code = "GIZMOS_" + stage + "_FAILED";
                observation.message = ex.ToString();
            }
            RecordGizmos(observation);
            return true;
        }

        private void AddGizmosWarning(string phase, string windowId, string view, bool desired, string code, string message) =>
            RecordGizmos(new AutomationGizmosObservation
            { phase = phase, windowId = windowId, view = view, desired = desired, status = "Warning", code = code, message = message });

        private void RecordGizmos(AutomationGizmosObservation observation)
        {
            _run.gizmosState ??= new AutomationGizmosState();
            var observations = _run.gizmosState.observations;
            if (observations.Any(o => o.phase == observation.phase && o.windowId == observation.windowId && o.code == observation.code)) return;
            string fullMessage = observation.message;
            if (observation.message?.Length > 2048) observation.message = observation.message.Substring(0, 2048);
            observations.Add(observation);
            if (observation.status == "Warning")
            {
                string message = $"{observation.code} runId={_run.runId} operationId={_run.operationId} phase={observation.phase} window={observation.windowId}: {fullMessage}";
                if (_failureDiagnostic != null) _failureDiagnostic(message);
                else Logger.ReportWarning("STEP", message, _run.runId);
            }
            Trace("run.gizmos", "Gizmos 状态", () => JsonUtility.ToJson(observation));
        }
    }
}
