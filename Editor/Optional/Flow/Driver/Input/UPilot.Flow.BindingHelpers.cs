using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace CodingRiver.UPilot.Flow
{
    public static partial class AdvancedActionHelpers
    {
        public static void SetBoundValueOrThrow(VisualElement element, Dictionary<string, string> parameters, string actionName)
        {
            string propertyPath = ResolveBoundPropertyPath(element, parameters, actionName, requireExplicitProperty: false);
            string value = ActionHelpers.Require(parameters, actionName, "value");
            bool infer = parameters.TryGetValue("infer", out string inferValue) && !string.Equals(inferValue, "false", StringComparison.OrdinalIgnoreCase);

            if (infer && string.IsNullOrWhiteSpace(propertyPath))
            {
                List<VisualElement> candidates = element.Query<VisualElement>().ToList();

                // Phase 1: bool-specific controls
                if (TryParseBool(value, out _))
                {
                    foreach (VisualElement candidate in candidates)
                    {
                        if ((candidate is Toggle || candidate is Foldout) && ActionHelpers.TryAssignFieldValue(candidate, value))
                        {
                            return;
                        }
                    }
                }

                // Phase 2: enum / discrete controls
                foreach (VisualElement candidate in candidates)
                {
                    if ((candidate is EnumField || candidate is EnumFlagsField || candidate is MaskField || candidate is LayerMaskField || candidate is RadioButtonGroup || candidate is DropdownField) && ActionHelpers.TryAssignFieldValue(candidate, value))
                    {
                        return;
                    }
                }

                // Phase 3: numeric controls
                if (TryParseFloat(value, out _) || TryParseInt(value, out _))
                {
                    foreach (VisualElement candidate in candidates)
                    {
                        if ((candidate is FloatField || candidate is IntegerField || candidate is LongField || candidate is DoubleField || candidate is Slider || candidate is SliderInt || candidate is MinMaxSlider) && ActionHelpers.TryAssignFieldValue(candidate, value))
                        {
                            return;
                        }
                    }
                }

                // Phase 4: any writable control (fallback to TextField, etc.)
                foreach (VisualElement candidate in candidates)
                {
                    if (candidate.GetType().GetProperty("value", PublicInstance)?.CanWrite == true && ActionHelpers.TryAssignFieldValue(candidate, value))
                    {
                        return;
                    }
                }

                throw new UPilotFlowException(
                    ErrorCodes.ActionTargetTypeInvalid,
                    $"Action {actionName} could not infer a writable bound child matching value '{value}'.");
            }

            VisualElement targetField = FindBoundTargetOrThrow(element, propertyPath, actionName, infer);
            if (!ActionHelpers.TryAssignFieldValue(targetField, value))
            {
                throw new UPilotFlowException(
                    ErrorCodes.ActionTargetTypeInvalid,
                    $"Action {actionName} target property '{propertyPath}' is not writable: {targetField.GetType().Name}");
            }
        }

        private static VisualElement FindBoundTargetOrThrow(VisualElement root, string propertyPath, string actionName, bool infer = false)
        {
            List<VisualElement> candidates = root.Query<VisualElement>().ToList();
            if (candidates.Count == 0)
            {
                throw new UPilotFlowException(ErrorCodes.ActionTargetTypeInvalid, $"Action {actionName} target does not contain bound child controls.");
            }

            foreach (VisualElement candidate in candidates)
            {
                IBindable bindable = candidate as IBindable;
                if (bindable == null || string.IsNullOrWhiteSpace(bindable.bindingPath))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(propertyPath) && !string.Equals(bindable.bindingPath, propertyPath, StringComparison.Ordinal))
                {
                    continue;
                }

                if (candidate.GetType().GetProperty("value", PublicInstance)?.CanWrite == true)
                {
                    return candidate;
                }
            }

            if (infer && string.IsNullOrWhiteSpace(propertyPath))
            {
                foreach (VisualElement candidate in candidates)
                {
                    if (candidate.GetType().GetProperty("value", PublicInstance)?.CanWrite == true)
                    {
                        return candidate;
                    }
                }
            }

            throw new UPilotFlowException(ErrorCodes.ActionOptionNotFound, $"Action {actionName} could not find bound property '{propertyPath}'.");
        }

        private static string TryInferBoundPropertyPath(VisualElement element)
        {
            List<VisualElement> candidates = element.Query<VisualElement>().ToList();
            foreach (VisualElement candidate in candidates)
            {
                if (candidate is IBindable childBindable && !string.IsNullOrWhiteSpace(childBindable.bindingPath))
                {
                    return childBindable.bindingPath;
                }
            }
            return null;
        }

        public static void AssertBoundValueOrThrow(VisualElement element, Dictionary<string, string> parameters, string actionName)
        {
            string propertyPath = ResolveBoundPropertyPath(element, parameters, actionName, requireExplicitProperty: false);
            string expected = ActionHelpers.Require(parameters, actionName, "expected");
            bool infer = parameters.TryGetValue("infer", out string inferValue) && !string.Equals(inferValue, "false", StringComparison.OrdinalIgnoreCase);

            if (infer && string.IsNullOrWhiteSpace(propertyPath))
            {
                List<VisualElement> candidates = element.Query<VisualElement>().ToList();

                // Phase 1: bool-specific controls
                if (TryParseBool(expected, out _))
                {
                    foreach (VisualElement candidate in candidates)
                    {
                        if ((candidate is Toggle || candidate is Foldout)
                            && TryReadValue(candidate, out object inferredActualValue, out Type inferredValueType)
                            && TryConvertStringValue(expected, inferredValueType, out object inferredExpectedValue)
                            && ValuesEqual(inferredActualValue, inferredExpectedValue, inferredValueType))
                        {
                            return;
                        }
                    }
                }

                // Phase 2: enum / discrete controls
                foreach (VisualElement candidate in candidates)
                {
                    if ((candidate is EnumField || candidate is EnumFlagsField || candidate is MaskField || candidate is LayerMaskField || candidate is RadioButtonGroup || candidate is DropdownField)
                        && TryReadValue(candidate, out object inferredActualValue, out Type inferredValueType)
                        && TryConvertStringValue(expected, inferredValueType, out object inferredExpectedValue)
                        && ValuesEqual(inferredActualValue, inferredExpectedValue, inferredValueType))
                    {
                        return;
                    }
                }

                // Phase 3: numeric controls
                if (TryParseFloat(expected, out _) || TryParseInt(expected, out _))
                {
                    foreach (VisualElement candidate in candidates)
                    {
                        if ((candidate is FloatField || candidate is IntegerField || candidate is LongField || candidate is DoubleField || candidate is Slider || candidate is SliderInt || candidate is MinMaxSlider)
                            && TryReadValue(candidate, out object inferredActualValue, out Type inferredValueType)
                            && TryConvertStringValue(expected, inferredValueType, out object inferredExpectedValue)
                            && ValuesEqual(inferredActualValue, inferredExpectedValue, inferredValueType))
                        {
                            return;
                        }
                    }
                }

                // Phase 4: any readable control
                foreach (VisualElement candidate in candidates)
                {
                    if (candidate.GetType().GetProperty("value", PublicInstance)?.CanRead != true)
                    {
                        continue;
                    }

                    if (TryReadValue(candidate, out object inferredActualValue, out Type inferredValueType)
                        && TryConvertStringValue(expected, inferredValueType, out object inferredExpectedValue))
                    {
                        if (ValuesEqual(inferredActualValue, inferredExpectedValue, inferredValueType))
                        {
                            return;
                        }

                        continue;
                    }

                    string inferredActual = ActionHelpers.GetValueText(candidate);
                    if (string.Equals(inferredActual, expected, StringComparison.Ordinal))
                    {
                        return;
                    }
                }

                throw new UPilotFlowException(
                    ErrorCodes.ActionExecutionFailed,
                    $"Action {actionName} could not infer a bound child matching expected '{expected}'.");
            }

            VisualElement targetField = FindBoundTargetOrThrow(element, propertyPath, actionName, infer);
            if (TryReadValue(targetField, out object actualValue, out Type valueType)
                && TryConvertStringValue(expected, valueType, out object expectedValue))
            {
                if (!ValuesEqual(actualValue, expectedValue, valueType))
                {
                    throw new UPilotFlowException(
                        ErrorCodes.ActionExecutionFailed,
                        $"Action {actionName} failed for bound property '{propertyPath}': expected '{expected}', actual '{FormatValue(actualValue, valueType)}'");
                }

                return;
            }

            string actual = ActionHelpers.GetValueText(targetField);
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                throw new UPilotFlowException(
                    ErrorCodes.ActionExecutionFailed,
                    $"Action {actionName} failed for bound property '{propertyPath}': expected '{expected}', actual '{actual}'");
            }
        }

        private static string ResolveBoundPropertyPath(VisualElement element, Dictionary<string, string> parameters, string actionName, bool requireExplicitProperty)
        {
            if (parameters.TryGetValue("property", out string propertyLiteral) && !string.IsNullOrWhiteSpace(propertyLiteral))
            {
                return propertyLiteral.Trim();
            }

            IBindable bindable = element as IBindable;
            if (bindable != null && !string.IsNullOrWhiteSpace(bindable.bindingPath))
            {
                return bindable.bindingPath;
            }

            if (requireExplicitProperty)
            {
                throw new UPilotFlowException(ErrorCodes.ActionParameterMissing, $"Action {actionName} requires property.");
            }

            bool infer = parameters.TryGetValue("infer", out string inferValue) && !string.Equals(inferValue, "false", StringComparison.OrdinalIgnoreCase);
            if (infer)
            {
                string inferred = TryInferBoundPropertyPath(element);
                if (!string.IsNullOrWhiteSpace(inferred))
                {
                    return inferred;
                }
                return null;
            }

            throw new UPilotFlowException(ErrorCodes.ActionParameterMissing, $"Action {actionName} requires property or a directly bound target.");
        }
    }
}
