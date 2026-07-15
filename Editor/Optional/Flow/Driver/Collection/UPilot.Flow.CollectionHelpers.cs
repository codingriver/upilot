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
        public static void SelectOptionOrThrow(VisualElement element, Dictionary<string, string> parameters, string actionName)
        {
            parameters.TryGetValue("value", out string valueLiteral);
            parameters.TryGetValue("index", out string indexLiteral);
            parameters.TryGetValue("indices", out string indicesLiteral);

            if (string.IsNullOrWhiteSpace(valueLiteral) && string.IsNullOrWhiteSpace(indexLiteral) && string.IsNullOrWhiteSpace(indicesLiteral))
            {
                throw new UPilotFlowException(ErrorCodes.ActionParameterMissing, $"Action {actionName} requires value, index, or indices.");
            }

            if (element is DropdownField dropdown)
            {
                ApplyChoiceSelection(element, dropdown.choices, dropdown, valueLiteral, indexLiteral, actionName);
                return;
            }

            if (element is EnumField enumField && enumField.value != null)
            {
                if (TryResolveEnumSelection(enumField.value.GetType(), valueLiteral, indexLiteral, out Enum enumValue))
                {
                    enumField.value = enumValue;
                    return;
                }

                throw new UPilotFlowException(ErrorCodes.ActionOptionNotFound, $"Action {actionName} option {valueLiteral ?? indexLiteral} was not found for {element.GetType().Name}.");
            }

            if (element is EnumFlagsField enumFlagsField && enumFlagsField.value != null)
            {
                if (TryResolveEnumFlagsSelection(enumFlagsField.value.GetType(), valueLiteral, indexLiteral, indicesLiteral, out Enum enumFlagsValue))
                {
                    enumFlagsField.value = enumFlagsValue;
                    return;
                }

                throw new UPilotFlowException(ErrorCodes.ActionOptionNotFound, $"Action {actionName} option {valueLiteral ?? indicesLiteral ?? indexLiteral} was not found for {element.GetType().Name}.");
            }

            if (element is MaskField maskField)
            {
                if (TryResolveMaskValue(maskField.choices, valueLiteral, indexLiteral, indicesLiteral, out int maskValue))
                {
                    maskField.value = maskValue;
                    return;
                }

                throw new UPilotFlowException(ErrorCodes.ActionOptionNotFound, $"Action {actionName} option {valueLiteral ?? indicesLiteral ?? indexLiteral} was not found for {element.GetType().Name}.");
            }

            if (element is LayerMaskField layerMaskField)
            {
                if (TryResolveMaskValue(layerMaskField.choices, valueLiteral, indexLiteral, indicesLiteral, out int layerMaskValue))
                {
                    layerMaskField.value = layerMaskValue;
                    return;
                }

                throw new UPilotFlowException(ErrorCodes.ActionOptionNotFound, $"Action {actionName} option {valueLiteral ?? indicesLiteral ?? indexLiteral} was not found for {element.GetType().Name}.");
            }

            if (element is RadioButtonGroup radioButtonGroup)
            {
                if (!TryResolveIndex(valueLiteral, indexLiteral, out int radioIndex))
                {
                    throw new UPilotFlowException(ErrorCodes.ActionParameterInvalid, $"Action {actionName} requires a numeric index for {element.GetType().Name}.");
                }

                radioButtonGroup.value = radioIndex;
                return;
            }

            PropertyInfo choicesProperty = element.GetType().GetProperty("choices", PublicInstance);
            PropertyInfo valueProperty = element.GetType().GetProperty("value", PublicInstance);
            PropertyInfo indexProperty = element.GetType().GetProperty("index", PublicInstance);

            if (choicesProperty?.GetValue(element) is IList choices)
            {
                ApplyChoiceSelection(element, choices, valueProperty, indexProperty, valueLiteral, indexLiteral, actionName);
                return;
            }

            if (valueProperty != null && valueProperty.CanWrite)
            {
                if (TryResolveValueForSelection(valueProperty.PropertyType, valueLiteral, indexLiteral, out object converted))
                {
                    valueProperty.SetValue(element, converted);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(indexLiteral))
                {
                    throw new UPilotFlowException(
                        ErrorCodes.ActionIndexOutOfRange,
                        $"Action {actionName} index {indexLiteral} is out of range for {element.GetType().Name}.");
                }

                throw new UPilotFlowException(
                    ErrorCodes.ActionOptionNotFound,
                    $"Action {actionName} option {valueLiteral} was not found for {element.GetType().Name}.");
            }

            throw new UPilotFlowException(ErrorCodes.ActionTargetTypeInvalid, $"Action {actionName} target is not a selectable control: {element.GetType().Name}");
        }

        private static IList GetItemsSource(object control)
        {
            return control?.GetType().GetProperty("itemsSource", PublicInstance)?.GetValue(control) as IList;
        }

        private static bool TrySetIntProperty(object target, string propertyName, int value)
        {
            PropertyInfo property = target?.GetType().GetProperty(propertyName, PublicInstance);
            if (property == null || !property.CanWrite)
            {
                return false;
            }

            if (property.PropertyType != typeof(int) && property.PropertyType != typeof(int?))
            {
                return false;
            }

            property.SetValue(target, value);
            return true;
        }

        private static bool TryApplyMultiSelection(object control, IList<int> indices)
        {
            if (indices == null || indices.Count == 0)
            {
                return false;
            }

            if (TrySetProperty(control, "selectedIndices", new List<int>(indices)))
            {
                return true;
            }

            foreach (string methodName in new[] { "SetSelection", "SetSelectionWithoutNotify" })
            {
                MethodInfo[] methods = control.GetType().GetMethods(PublicInstance);
                foreach (MethodInfo method in methods)
                {
                    if (!string.Equals(method.Name, methodName, StringComparison.Ordinal) || method.GetParameters().Length != 1)
                    {
                        continue;
                    }

                    Type paramType = method.GetParameters()[0].ParameterType;
                    if (paramType == typeof(int))
                    {
                        continue;
                    }

                    if (TryBuildSelectionArgument(paramType, indices, out object argument))
                    {
                        method.Invoke(control, new[] { argument });
                        return true;
                    }
                }
            }

            return false;
        }

        private static void RefreshCollectionView(object control)
        {
            foreach (string methodName in new[] { "RefreshItems", "Refresh", "Rebuild" })
            {
                MethodInfo method = control?.GetType().GetMethod(methodName, PublicInstance, null, Type.EmptyTypes, null);
                if (method == null)
                {
                    continue;
                }

                method.Invoke(control, null);
                return;
            }
        }

        public static void SelectListItemsOrThrow(VisualElement element, IList<int> indices, string actionName)
        {
            IList itemsSource = GetItemsSource(element);
            if (itemsSource == null)
            {
                throw new UPilotFlowException(ErrorCodes.ActionTargetTypeInvalid, $"Action {actionName} target is not a list control: {element.GetType().Name}");
            }

            if (indices == null || indices.Count == 0)
            {
                throw new UPilotFlowException(ErrorCodes.ActionParameterMissing, $"Action {actionName} requires indices.");
            }

            var distinctIndices = new List<int>();
            var seen = new HashSet<int>();
            foreach (int index in indices)
            {
                ValidateIndex(index, itemsSource.Count, actionName);
                if (seen.Add(index))
                {
                    distinctIndices.Add(index);
                }
            }

            if (TryApplyMultiSelection(element, distinctIndices))
            {
                return;
            }

            throw new UPilotFlowException(ErrorCodes.ActionTargetTypeInvalid, $"Action {actionName} target does not expose multi-selection APIs: {element.GetType().Name}");
        }

        public static void SelectListItemOrThrow(VisualElement element, int index, string actionName)
        {
            IList itemsSource = GetItemsSource(element);
            if (itemsSource == null)
            {
                throw new UPilotFlowException(ErrorCodes.ActionTargetTypeInvalid, $"Action {actionName} target is not a list control: {element.GetType().Name}");
            }

            ValidateIndex(index, itemsSource.Count, actionName);
            if (TryApplySingleSelection(element, index))
            {
                return;
            }

            throw new UPilotFlowException(ErrorCodes.ActionTargetTypeInvalid, $"Action {actionName} target does not expose selection APIs: {element.GetType().Name}");
        }

        private static bool TryResolveValueForSelection(Type targetType, string valueLiteral, string indexLiteral, out object converted)
        {
            converted = null;

            if (!string.IsNullOrWhiteSpace(valueLiteral))
            {
                return TryConvertStringValue(valueLiteral, targetType, out converted);
            }

            if (targetType.IsEnum)
            {
                Array values = Enum.GetValues(targetType);
                if (!TryParseInt(indexLiteral, out int index) || index < 0 || index >= values.Length)
                {
                    return false;
                }

                converted = values.GetValue(index);
                return true;
            }

            return TryConvertStringValue(indexLiteral, targetType, out converted);
        }

        private static bool TrySetObjectProperty(object target, string propertyName, object value)
        {
            PropertyInfo property = target?.GetType().GetProperty(propertyName, PublicInstance);
            if (property == null || !property.CanWrite)
            {
                return false;
            }

            if (value != null && !property.PropertyType.IsInstanceOfType(value))
            {
                return false;
            }

            property.SetValue(target, value);
            return true;
        }

        private static void ValidateIndex(int index, int count, string actionName)
        {
            if (index < 0 || index >= count)
            {
                throw new UPilotFlowException(
                    ErrorCodes.ActionIndexOutOfRange,
                    $"Action {actionName} index {index} is out of range; valid range is [0, {Math.Max(count - 1, 0)}].");
            }
        }

        private static bool TryApplySelectionById(object control, int itemId)
        {
            foreach (string propertyName in new[] { "selectedId", "selectedItemId" })
            {
                if (TrySetIntProperty(control, propertyName, itemId))
                {
                    return true;
                }
            }

            foreach (string methodName in new[] { "SetSelectionById", "SetSelectionByIds", "SetSelectionInternal" })
            {
                MethodInfo[] methods = control.GetType().GetMethods(PublicInstance);
                foreach (MethodInfo method in methods)
                {
                    if (!string.Equals(method.Name, methodName, StringComparison.Ordinal) || method.GetParameters().Length != 1)
                    {
                        continue;
                    }

                    if (TryBuildSelectionArgument(method.GetParameters()[0].ParameterType, itemId, out object argument))
                    {
                        method.Invoke(control, new[] { argument });
                        PropertyInfo selectedIndexProp = control.GetType().GetProperty("selectedIndex", PublicInstance);
                        if (selectedIndexProp != null)
                        {
                            int selectedIndex = Convert.ToInt32(selectedIndexProp.GetValue(control));
                            if (selectedIndex < 0)
                            {
                                return false;
                            }
                        }
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryResolveMaskToggleIndex(IList<string> choices, string valueLiteral, string indexLiteral, out int bitIndex)
        {
            bitIndex = 0;
            if (!string.IsNullOrWhiteSpace(indexLiteral) && TryParseInt(indexLiteral, out int idx) && idx >= 0 && idx < choices.Count)
            {
                bitIndex = idx;
                return true;
            }
            if (!string.IsNullOrWhiteSpace(valueLiteral))
            {
                for (int i = 0; i < choices.Count; i++)
                {
                    if (string.Equals(choices[i], valueLiteral, StringComparison.Ordinal))
                    {
                        bitIndex = i;
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool TryResolveEnumFlagsSelection(Type enumType, string valueLiteral, string indexLiteral, string indicesLiteral, out Enum value)
        {
            value = null;
            if (!string.IsNullOrWhiteSpace(valueLiteral))
            {
                if (TryConvertStringValue(valueLiteral, enumType, out object converted))
                {
                    value = (Enum)converted;
                    return true;
                }

                return false;
            }

            Array enumValues = Enum.GetValues(enumType);
            if (!string.IsNullOrWhiteSpace(indicesLiteral))
            {
                if (!TryParseIndexList(indicesLiteral, out List<int> indices))
                {
                    return false;
                }

                long combined = 0L;
                foreach (int index in indices)
                {
                    if (index < 0 || index >= enumValues.Length)
                    {
                        return false;
                    }

                    combined |= Convert.ToInt64(enumValues.GetValue(index), InvariantCulture);
                }

                value = (Enum)Enum.ToObject(enumType, combined);
                return true;
            }

            if (!TryParseInt(indexLiteral, out int singleIndex) || singleIndex < 0 || singleIndex >= enumValues.Length)
            {
                return false;
            }

            value = (Enum)enumValues.GetValue(singleIndex);
            return true;
        }

        private static bool TryResolveMaskValue(IList choices, string valueLiteral, string indexLiteral, string indicesLiteral, out int value)
        {
            value = 0;

            if (!string.IsNullOrWhiteSpace(valueLiteral) && TryParseInt(valueLiteral, out int rawMask))
            {
                value = rawMask;
                return true;
            }

            if (!string.IsNullOrWhiteSpace(indicesLiteral))
            {
                if (!TryParseIndexList(indicesLiteral, out List<int> indices))
                {
                    return false;
                }

                int mask = 0;
                foreach (int index in indices)
                {
                    if (choices == null || index < 0 || index >= choices.Count)
                    {
                        return false;
                    }

                    mask |= 1 << index;
                }

                value = mask;
                return true;
            }

            if (!string.IsNullOrWhiteSpace(indexLiteral))
            {
                if (choices == null || !TryParseInt(indexLiteral, out int index) || index < 0 || index >= choices.Count)
                {
                    return false;
                }

                value = 1 << index;
                return true;
            }

            if (string.IsNullOrWhiteSpace(valueLiteral) || choices == null)
            {
                return false;
            }

            int combinedMask = 0;
            string[] names = valueLiteral.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string rawName in names)
            {
                string name = rawName.Trim();
                bool matched = false;
                for (int i = 0; i < choices.Count; i++)
                {
                    if (string.Equals(choices[i]?.ToString(), name, StringComparison.Ordinal))
                    {
                        combinedMask |= 1 << i;
                        matched = true;
                        break;
                    }
                }

                if (!matched)
                {
                    return false;
                }
            }

            value = combinedMask;
            return true;
        }

        public static void ToggleMaskOptionOrThrow(VisualElement element, Dictionary<string, string> parameters, string actionName)
        {
            parameters.TryGetValue("value", out string valueLiteral);
            parameters.TryGetValue("index", out string indexLiteral);

            if (string.IsNullOrWhiteSpace(valueLiteral) && string.IsNullOrWhiteSpace(indexLiteral))
            {
                throw new UPilotFlowException(ErrorCodes.ActionParameterMissing, $"Action {actionName} requires value or index.");
            }

            if (element is EnumFlagsField enumFlagsField && enumFlagsField.value != null)
            {
                Type enumType = enumFlagsField.value.GetType();
                if (!TryResolveEnumFlagsMaskIndex(enumType, valueLiteral, indexLiteral, out int bitIndex))
                {
                    throw new UPilotFlowException(ErrorCodes.ActionOptionNotFound, $"Action {actionName} option {valueLiteral ?? indexLiteral} was not found for {element.GetType().Name}.");
                }
                int intValue = Convert.ToInt32(enumFlagsField.value);
                int newValue = intValue ^ (1 << bitIndex);
                enumFlagsField.value = (Enum)Enum.ToObject(enumType, newValue);
                return;
            }

            if (element is MaskField maskField)
            {
                if (!TryResolveMaskToggleIndex(maskField.choices, valueLiteral, indexLiteral, out int maskBitIndex))
                {
                    throw new UPilotFlowException(ErrorCodes.ActionOptionNotFound, $"Action {actionName} option {valueLiteral ?? indexLiteral} was not found for {element.GetType().Name}.");
                }
                maskField.value ^= (1 << maskBitIndex);
                return;
            }

            if (element is LayerMaskField layerMaskField)
            {
                if (!TryResolveMaskToggleIndex(layerMaskField.choices, valueLiteral, indexLiteral, out int layerMaskBitIndex))
                {
                    throw new UPilotFlowException(ErrorCodes.ActionOptionNotFound, $"Action {actionName} option {valueLiteral ?? indexLiteral} was not found for {element.GetType().Name}.");
                }
                layerMaskField.value ^= (1 << layerMaskBitIndex);
                return;
            }

            throw new UPilotFlowException(ErrorCodes.ActionTargetTypeInvalid, $"Action {actionName} target is not a mask/flags control: {element.GetType().Name}");
        }

        private static void ApplyChoiceSelection(VisualElement element, IList choices, DropdownField dropdown, string valueLiteral, string indexLiteral, string actionName)
        {
            if (!TryResolveChoice(choices, valueLiteral, indexLiteral, out int _, out object selectedChoice))
            {
                ThrowChoiceSelectionError(element, choices?.Count ?? 0, valueLiteral, indexLiteral, actionName);
            }

            dropdown.value = selectedChoice?.ToString() ?? string.Empty;
        }

        private static void ApplyChoiceSelection(VisualElement element, IList choices, PropertyInfo valueProperty, PropertyInfo indexProperty, string valueLiteral, string indexLiteral, string actionName)
        {
            if (!TryResolveChoice(choices, valueLiteral, indexLiteral, out int selectedIndex, out object selectedChoice))
            {
                ThrowChoiceSelectionError(element, choices?.Count ?? 0, valueLiteral, indexLiteral, actionName);
            }

            if (indexProperty != null && indexProperty.CanWrite)
            {
                indexProperty.SetValue(element, selectedIndex);
                return;
            }

            if (valueProperty == null || !valueProperty.CanWrite)
            {
                throw new UPilotFlowException(ErrorCodes.ActionTargetTypeInvalid, $"Action {actionName} target does not expose writable choice state: {element.GetType().Name}");
            }

            object valueToAssign = selectedChoice;
            Type targetType = valueProperty.PropertyType;
            if (valueToAssign == null)
            {
                valueToAssign = targetType == typeof(string) ? string.Empty : null;
            }
            else if (!targetType.IsInstanceOfType(valueToAssign))
            {
                if (!TryConvertStringValue(valueToAssign.ToString(), targetType, out valueToAssign))
                {
                    throw new UPilotFlowException(ErrorCodes.ActionTargetTypeInvalid, $"Action {actionName} choice cannot be assigned to {targetType.Name}");
                }
            }

            valueProperty.SetValue(element, valueToAssign);
        }

        public static void SelectTreeItemOrThrow(VisualElement element, Dictionary<string, string> parameters, string actionName)
        {
            parameters.TryGetValue("id", out string idLiteral);
            parameters.TryGetValue("index", out string indexLiteral);

            if (string.IsNullOrWhiteSpace(idLiteral) && string.IsNullOrWhiteSpace(indexLiteral))
            {
                throw new UPilotFlowException(ErrorCodes.ActionParameterMissing, $"Action {actionName} requires id or index.");
            }

            if (!string.IsNullOrWhiteSpace(idLiteral))
            {
                if (!TryParseInt(idLiteral, out int itemId))
                {
                    throw new UPilotFlowException(ErrorCodes.ActionParameterInvalid, $"Action {actionName} id is invalid: {idLiteral}");
                }

                if (TryApplySelectionById(element, itemId))
                {
                    return;
                }
            }

            if (!string.IsNullOrWhiteSpace(indexLiteral))
            {
                if (!TryParseInt(indexLiteral, out int index))
                {
                    throw new UPilotFlowException(ErrorCodes.ActionParameterInvalid, $"Action {actionName} index is invalid: {indexLiteral}");
                }

                SelectListItemOrThrow(element, index, actionName);
                return;
            }

            throw new UPilotFlowException(ErrorCodes.ActionTargetTypeInvalid, $"Action {actionName} target is not a tree control: {element.GetType().Name}");
        }

        private static bool TrySetFloatProperty(object target, string propertyName, float value)
        {
            PropertyInfo property = target?.GetType().GetProperty(propertyName, PublicInstance);
            if (property == null || !property.CanWrite)
            {
                return false;
            }

            property.SetValue(target, value);
            return true;
        }

        private static bool TryResolveChoice(IList choices, string valueLiteral, string indexLiteral, out int selectedIndex, out object selectedChoice)
        {
            selectedIndex = -1;
            selectedChoice = null;

            if (choices == null)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(valueLiteral))
            {
                for (int i = 0; i < choices.Count; i++)
                {
                    if (string.Equals(choices[i]?.ToString(), valueLiteral, StringComparison.Ordinal))
                    {
                        selectedIndex = i;
                        selectedChoice = choices[i];
                        return true;
                    }
                }

                return false;
            }

            if (!TryParseInt(indexLiteral, out int parsedIndex) || parsedIndex < 0 || parsedIndex >= choices.Count)
            {
                return false;
            }

            selectedIndex = parsedIndex;
            selectedChoice = choices[parsedIndex];
            return true;
        }

        private static bool TryApplySingleSelection(object control, int index)
        {
            if (TrySetIntProperty(control, "selectedIndex", index))
            {
                return true;
            }

            foreach (string methodName in new[] { "SetSelection", "SetSelectionWithoutNotify" })
            {
                MethodInfo[] methods = control.GetType().GetMethods(PublicInstance);
                foreach (MethodInfo method in methods)
                {
                    if (!string.Equals(method.Name, methodName, StringComparison.Ordinal) || method.GetParameters().Length != 1)
                    {
                        continue;
                    }

                    if (TryBuildSelectionArgument(method.GetParameters()[0].ParameterType, index, out object argument))
                    {
                        method.Invoke(control, new[] { argument });
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryResolveEnumFlagsMaskIndex(Type enumType, string valueLiteral, string indexLiteral, out int bitIndex)
        {
            bitIndex = 0;
            Array values = Enum.GetValues(enumType);
            string[] names = Enum.GetNames(enumType);
            if (!string.IsNullOrWhiteSpace(indexLiteral) && TryParseInt(indexLiteral, out int idx) && idx >= 0 && idx < values.Length)
            {
                bitIndex = (int)Math.Log(Convert.ToInt32(values.GetValue(idx)), 2);
                return true;
            }
            if (!string.IsNullOrWhiteSpace(valueLiteral))
            {
                for (int i = 0; i < names.Length; i++)
                {
                    if (string.Equals(names[i], valueLiteral, StringComparison.OrdinalIgnoreCase))
                    {
                        bitIndex = (int)Math.Log(Convert.ToInt32(values.GetValue(i)), 2);
                        return true;
                    }
                }
            }
            return false;
        }

        public static void ReorderListItemOrThrow(VisualElement element, int fromIndex, int toIndex, string actionName)
        {
            IList itemsSource = GetItemsSource(element);
            if (itemsSource == null)
            {
                throw new UPilotFlowException(ErrorCodes.ActionTargetTypeInvalid, $"Action {actionName} target is not a list control: {element.GetType().Name}");
            }

            ValidateIndex(fromIndex, itemsSource.Count, actionName);
            ValidateIndex(toIndex, itemsSource.Count, actionName);

            if (fromIndex == toIndex)
            {
                return;
            }

            object item = itemsSource[fromIndex];
            itemsSource.RemoveAt(fromIndex);
            itemsSource.Insert(Math.Min(toIndex, itemsSource.Count), item);

            RefreshCollectionView(element);
            TryApplySingleSelection(element, Math.Min(toIndex, itemsSource.Count - 1));
        }

        private static void ThrowChoiceSelectionError(VisualElement element, int choiceCount, string valueLiteral, string indexLiteral, string actionName)
        {
            if (!string.IsNullOrWhiteSpace(indexLiteral))
            {
                throw new UPilotFlowException(
                    ErrorCodes.ActionIndexOutOfRange,
                    $"Action {actionName} index {indexLiteral} is out of range for {element.GetType().Name}; valid range is [0, {Math.Max(choiceCount - 1, 0)}].");
            }

            throw new UPilotFlowException(
                ErrorCodes.ActionOptionNotFound,
                $"Action {actionName} option {valueLiteral} was not found for {element.GetType().Name}.");
        }

        private static bool TryResolveEnumSelection(Type enumType, string valueLiteral, string indexLiteral, out Enum value)
        {
            value = null;
            if (!string.IsNullOrWhiteSpace(valueLiteral))
            {
                if (TryConvertStringValue(valueLiteral, enumType, out object converted))
                {
                    value = (Enum)converted;
                    return true;
                }

                return false;
            }

            Array values = Enum.GetValues(enumType);
            if (!TryParseInt(indexLiteral, out int index) || index < 0 || index >= values.Length)
            {
                return false;
            }

            value = (Enum)values.GetValue(index);
            return true;
        }

        private static bool TrySetProperty(object target, string propertyName, object value)
        {
            PropertyInfo property = target?.GetType().GetProperty(propertyName, PublicInstance);
            if (property == null || !property.CanWrite)
            {
                return false;
            }

            try
            {
                if (value is List<int> values && property.PropertyType == typeof(int[]))
                {
                    int[] array = new int[values.Count];
                    values.CopyTo(array, 0);
                    property.SetValue(target, array);
                    return true;
                }

                if (value != null && !property.PropertyType.IsInstanceOfType(value))
                {
                    return false;
                }

                property.SetValue(target, value);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryBuildSelectionArgument(Type parameterType, int index, out object argument)
        {
            argument = null;

            if (parameterType == typeof(int))
            {
                argument = index;
                return true;
            }

            if (parameterType == typeof(int[]))
            {
                argument = new[] { index };
                return true;
            }

            if (parameterType.IsAssignableFrom(typeof(List<int>)))
            {
                argument = new List<int> { index };
                return true;
            }

            if (parameterType.IsInterface && parameterType.IsGenericType && parameterType.GetGenericArguments().Length == 1 && parameterType.GetGenericArguments()[0] == typeof(int))
            {
                argument = new List<int> { index };
                return true;
            }

            return false;
        }

        private static bool TryBuildSelectionArgument(Type parameterType, IList<int> indices, out object argument)
        {
            argument = null;

            if (parameterType == typeof(int))
            {
                argument = indices[0];
                return true;
            }

            if (parameterType == typeof(int[]))
            {
                int[] values = new int[indices.Count];
                for (int i = 0; i < indices.Count; i++)
                {
                    values[i] = indices[i];
                }

                argument = values;
                return true;
            }

            if (parameterType.IsAssignableFrom(typeof(List<int>)))
            {
                argument = new List<int>(indices);
                return true;
            }

            if (parameterType.IsInterface
                && parameterType.IsGenericType
                && parameterType.GetGenericArguments().Length == 1
                && parameterType.GetGenericArguments()[0] == typeof(int))
            {
                argument = new List<int>(indices);
                return true;
            }

            return false;
        }
    }
}
