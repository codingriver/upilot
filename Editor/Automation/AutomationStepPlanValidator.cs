using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace CodingRiver.UPilot.Automation
{
    public static class AutomationStepPlanValidator
    {
        /// <summary>Checks JSON token types before JsonUtility can coerce them.</summary>
        public static AutomationStepValidation Parse(string json, out AutomationStepPlan plan)
        {
            plan = null;
            var result = new AutomationStepValidation();
            try
            {
                var root = AutomationStepJsonCodec.ParseJson(json);
                if ((string)root.Attribute("type") != "object" || (string)root.Element("steps")?.Attribute("type") != "array")
                    return AutomationStepValidation.Invalid("STEP_PLAN_INVALID", "Plan must contain a steps array.");
                if (root.Element("version") != null
                    && ((string)root.Element("version").Attribute("type") != "number" || root.Element("version").Value != "1"))
                    AutomationCatalog.Add(result.diagnostics, "STEP_PLAN_VERSION_INVALID", "Expected numeric version 1.");
                if (root.Element("logPolicy") != null && (string)root.Element("logPolicy").Attribute("type") != "array")
                {
                    AutomationCatalog.Add(result.diagnostics, "STEP_FIELD_TYPE_INVALID", "logPolicy must be an array.");
                    root.Element("logPolicy").Remove();
                }
                int index = 0;
                foreach (var element in root.Element("steps").Elements())
                {
                    if ((string)element.Attribute("type") != "object")
                    {
                        AutomationCatalog.Add(result.diagnostics, "STEP_ITEM_INVALID", "Expected a step object.", "", index);
                        element.RemoveNodes();
                        element.SetAttributeValue("type", "object");
                    }
                    foreach (var name in new[] { "instanceId", "stepId", "arguments", "phase" })
                        if (element.Element(name) != null && (string)element.Element(name).Attribute("type") != "string")
                        {
                            AutomationCatalog.Add(result.diagnostics, "STEP_FIELD_TYPE_INVALID", name + " must be a string.", "", index);
                            element.Element(name).Remove();
                        }
                    foreach (var name in new[] { "timeoutSeconds", "pollIntervalSeconds", "cleanupTimeoutSeconds" })
                        if (element.Element(name) != null && (string)element.Element(name).Attribute("type") != "number")
                        {
                            AutomationCatalog.Add(result.diagnostics, "STEP_FIELD_TYPE_INVALID", name + " must be a number.", "", index);
                            element.Element(name).Remove();
                        }
                    if (element.Element("allowSkipped") != null && (string)element.Element("allowSkipped").Attribute("type") != "boolean")
                    {
                        AutomationCatalog.Add(result.diagnostics, "STEP_FIELD_TYPE_INVALID", "allowSkipped must be boolean.", "", index);
                        element.Element("allowSkipped").Remove();
                    }
                    index++;
                }
                result.ok = result.diagnostics.Count == 0;
                // The sanitized copy is only for aggregating later diagnostics, never for starting an invalid plan.
                plan = JsonUtility.FromJson<AutomationStepPlan>(AutomationStepJsonCodec.WriteJson(root));
            }
            catch (Exception ex) { return AutomationStepValidation.Invalid("STEP_PLAN_INVALID", ex.Message); }
            return result;
        }

        public static AutomationStepValidation Validate(AutomationStepPlan plan, AutomationStepRegistry registry)
        {
            var result = new AutomationStepValidation();
            result.diagnostics.AddRange(registry.Diagnostics);
            if (plan == null || plan.version != 1 || plan.steps == null || plan.steps.Length == 0)
            {
                AutomationCatalog.Add(result.diagnostics, "STEP_PLAN_INVALID", "A nonempty version 1 plan is required.");
                result.ok = false; return result;
            }
            var snapshot = AutomationStepJson.Copy(plan);
            var run = new AutomationStepRun { plan = snapshot, runId = "", operationId = "" };
            run.steps.AddRange(snapshot.steps.Select(_ => new AutomationStepRecord()));
            var ids = new HashSet<string>(StringComparer.Ordinal);
            bool finallySeen = false;
            int captureStarts = 0;
            for (int index = 0; index < snapshot.steps.Length; index++)
            {
                var item = snapshot.steps[index];
                void Error(string code, string text) => AutomationCatalog.Add(result.diagnostics, code, text, item?.instanceId, index);
                if (item == null) { Error("STEP_ITEM_INVALID", "Null step."); continue; }
                if (string.IsNullOrWhiteSpace(item.instanceId) || !ids.Add(item.instanceId))
                    Error("STEP_INSTANCE_INVALID", "instanceId must be unique and nonempty.");
                if (plan.steps[index]?.arguments == null) Error("STEP_ARGUMENTS_INVALID", "arguments must be a string.");
                if (item.phase != "Normal" && item.phase != "Finally") Error("STEP_PHASE_INVALID", "Use Normal or Finally.");
                if (finallySeen && item.phase == "Normal") Error("STEP_FINALLY_ORDER_INVALID", "Finally must be a contiguous suffix.");
                finallySeen |= item.phase == "Finally";
                if (item.stepId == "upilot.console_capture_start")
                {
                    captureStarts++;
                    if (captureStarts > 1 || index != 0 || item.phase != "Normal")
                        Error("STEP_CAPTURE_ORDER_INVALID", "Capture must start exactly once as the first Normal item.");
                }
                if (!registry.TryGet(item.stepId, out var descriptor))
                { Error("STEP_NOT_FOUND", "Step is missing or has an invalid registration: " + item.stepId); continue; }
                double timeout = item.timeoutSeconds == -1 ? descriptor.timeoutSeconds : item.timeoutSeconds;
                double interval = item.pollIntervalSeconds == -1 ? descriptor.pollIntervalSeconds : item.pollIntervalSeconds;
                if (!AutomationStepJson.Finite(timeout) || timeout <= 0
                    || !AutomationStepJson.Finite(item.cleanupTimeoutSeconds) || item.cleanupTimeoutSeconds <= 0
                    || !AutomationStepJson.Finite(interval) || interval < 0)
                    Error("STEP_TIMING_INVALID", "Timeouts must be finite and positive; polling must be nonnegative.");
                else result.budgetSeconds += timeout + item.cleanupTimeoutSeconds;
                if (item.arguments == null) continue;
                try
                {
                    using (UPilotAutomationStepService.EnterCallback(null, false))
                    {
                        var validation = AutomationStepJsonCodec.Validation(registry.Create(item.stepId).Validate(
                            "", item.instanceId, AutomationStepContextJson.Create(run, index, true), item.arguments));
                        foreach (var diagnostic in validation.diagnostics)
                        {
                            var copy = AutomationStepJson.Copy(diagnostic);
                            copy.index = index; copy.subjectId = item.instanceId;
                            result.diagnostics.Add(copy);
                        }
                    }
                }
                catch (FormatException ex) { Error("STEP_VALIDATION_INVALID", ex.Message); }
                catch (Exception ex) { Error("STEP_VALIDATION_EXCEPTION", ex.GetBaseException().Message); }
            }
            var policy = AutomationLogPolicy.Evaluate(null, null, snapshot.logPolicy);
            result.diagnostics.AddRange(policy.diagnostics);
            result.budgetSeconds += 60; // Evidence finalization and observer/transport allowance.
            if (!AutomationStepJson.Finite(result.budgetSeconds))
                AutomationCatalog.Add(result.diagnostics, "STEP_TIMING_INVALID", "The total plan budget is not finite.");
            result.ok = result.diagnostics.All(d => d.severity != "error");
            return result;
        }
    }
}
