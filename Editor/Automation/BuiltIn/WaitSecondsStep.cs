using System;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot.Automation
{
    [AutomationStep("upilot.wait_seconds", Description = "Nonblocking Editor-time wait.", ArgumentsExample = "1.5")]
    public sealed class WaitSecondsStep : AutomationStepBase
    {
        [Serializable] private sealed class Checkpoint { public double until; public bool initialized; }
        private static bool Parse(string arguments, out double seconds) =>
            double.TryParse(arguments, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)
            && AutomationStepJson.Finite(seconds) && seconds >= 0
            && AutomationStepJson.Finite(EditorApplication.timeSinceStartup + seconds);
        public override string Validate(string runId, string instanceId, string contextJson, string arguments) =>
            Parse(arguments, out _) ? ValidationOk()
                : ValidationError("STEP_ARGUMENTS_INVALID", "Expected nonnegative finite seconds.");
        public override void Execute(string runId, string instanceId, string contextJson, string arguments)
        {
            if (!Parse(arguments, out double seconds)) { Fail("STEP_ARGUMENTS_INVALID", "Invalid seconds."); return; }
            SaveCheckpoint(runId, instanceId, JsonUtility.ToJson(new Checkpoint
                { until = EditorApplication.timeSinceStartup + seconds, initialized = true }));
        }
        public override string Poll(string runId, string instanceId, string contextJson, string arguments)
        {
            if (!IsRunning) return base.Poll(runId, instanceId, contextJson, arguments);
            var checkpoint = ReadCheckpoint(contextJson);
            return ResultJson(EditorApplication.timeSinceStartup >= checkpoint.until ? "Succeeded" : "Running");
        }
        public override string Restore(string runId, string instanceId, string contextJson, string arguments)
        {
            try { ReadCheckpoint(contextJson); return "{\"status\":\"Restored\"}"; }
            catch { return "{\"status\":\"Unsupported\"}"; }
        }
        private static Checkpoint ReadCheckpoint(string contextJson)
        {
            string json = AutomationStepContextJson.Checkpoint(contextJson);
            var root = AutomationStepJsonCodec.ParseObject(json);
            AutomationStepJsonCodec.RequireType(root.Element("until"), "number");
            AutomationStepJsonCodec.RequireType(root.Element("initialized"), "boolean");
            var value = JsonUtility.FromJson<Checkpoint>(json);
            if (!value.initialized || !AutomationStepJson.Finite(value.until) || value.until < 0)
                throw new InvalidOperationException("STEP_CHECKPOINT_INVALID");
            return value;
        }
    }
}
