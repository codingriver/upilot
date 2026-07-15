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
    public static class ColumnActionHelpers
    {
        public static Columns GetColumnsOrThrow(VisualElement element, string actionName)
        {
            switch (element)
            {
                case MultiColumnListView mclv:
                    return mclv.columns;
                case MultiColumnTreeView mctv:
                    return mctv.columns;
                default:
                    throw new UPilotFlowException(ErrorCodes.ActionTargetTypeInvalid,
                        $"Action {actionName} target is not a MultiColumnListView or MultiColumnTreeView: {element.GetType().Name}");
            }
        }

        public static Column FindColumnOrThrow(VisualElement element, Dictionary<string, string> parameters, string actionName)
        {
            Columns columns = GetColumnsOrThrow(element, actionName);
            parameters.TryGetValue("column", out string columnParam);
            parameters.TryGetValue("index", out string indexParam);

            if (string.IsNullOrWhiteSpace(columnParam) && string.IsNullOrWhiteSpace(indexParam))
            {
                throw new UPilotFlowException(ErrorCodes.ActionParameterMissing,
                    $"Action {actionName} requires 'column' (name or title) or 'index' parameter.");
            }

            if (!string.IsNullOrWhiteSpace(columnParam))
            {
                foreach (Column col in columns)
                {
                    if (col.name == columnParam || col.title == columnParam)
                    {
                        return col;
                    }
                }

                throw new UPilotFlowException(ErrorCodes.ActionOptionNotFound,
                    $"Action {actionName}: column '{columnParam}' was not found.");
            }

            if (!int.TryParse(indexParam, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx))
            {
                throw new UPilotFlowException(ErrorCodes.ActionParameterInvalid,
                    $"Action {actionName}: column index '{indexParam}' is not a valid integer.");
            }

            if (idx < 0 || idx >= columns.Count)
            {
                throw new UPilotFlowException(ErrorCodes.ActionIndexOutOfRange,
                    $"Action {actionName}: column index {idx} is out of range [0,{columns.Count - 1}].");
            }

            return columns[idx];
        }
    }

    public sealed class SortColumnAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            VisualElement element = await ActionHelpers.RequireElementAsync(context, parameters, "sort_column");
            Column column = ColumnActionHelpers.FindColumnOrThrow(element, parameters, "sort_column");
            parameters.TryGetValue("direction", out string directionParam);
            parameters.TryGetValue("mode", out string modeParam);

            SortDirection direction = SortDirection.Ascending;
            if (!string.IsNullOrWhiteSpace(directionParam))
            {
                string dir = directionParam.Trim().ToLowerInvariant();
                if (dir == "descending" || dir == "desc")
                {
                    direction = SortDirection.Descending;
                }
                else if (dir != "ascending" && dir != "asc")
                {
                    throw new UPilotFlowException(ErrorCodes.ActionParameterInvalid,
                        $"Action sort_column: invalid direction '{directionParam}'. Use 'ascending' or 'descending'.");
                }
            }

            ColumnSortingMode sortingMode = ColumnSortingMode.Default;
            if (!string.IsNullOrWhiteSpace(modeParam))
            {
                string mode = modeParam.Trim().ToLowerInvariant();
                switch (mode)
                {
                    case "default":
                        sortingMode = ColumnSortingMode.Default;
                        break;
                    case "none":
                        sortingMode = ColumnSortingMode.None;
                        break;
                    case "custom":
                        sortingMode = ColumnSortingMode.Custom;
                        break;
                    default:
                        throw new UPilotFlowException(ErrorCodes.ActionParameterInvalid,
                            $"Action sort_column: invalid mode '{modeParam}'. Use 'default', 'none', or 'custom'.");
                }
            }

            context.Log($"sort_column: column={column.name} direction={direction} mode={sortingMode}");

            switch (element)
            {
                case MultiColumnListView mclv:
                    mclv.sortingMode = sortingMode;
                    mclv.sortColumnDescriptions.Clear();
                    mclv.sortColumnDescriptions.Add(new SortColumnDescription(column.name, direction));
                    if (sortingMode == ColumnSortingMode.Custom)
                    {
                        MethodInfo raiseMethod = mclv.GetType().GetMethod("RaiseColumnSortingChanged", BindingFlags.NonPublic | BindingFlags.Instance);
                        if (raiseMethod != null)
                        {
                            try { raiseMethod.Invoke(mclv, null); }
                            catch { /* ignored */ }
                        }
                    }
                    break;
                case MultiColumnTreeView mctv:
                    mctv.sortingMode = sortingMode;
                    mctv.sortColumnDescriptions.Clear();
                    mctv.sortColumnDescriptions.Add(new SortColumnDescription(column.name, direction));
                    if (sortingMode == ColumnSortingMode.Custom)
                    {
                        MethodInfo raiseMethod = mctv.GetType().GetMethod("RaiseColumnSortingChanged", BindingFlags.NonPublic | BindingFlags.Instance);
                        if (raiseMethod != null)
                        {
                            try { raiseMethod.Invoke(mctv, null); }
                            catch { /* ignored */ }
                        }
                    }
                    break;
            }

            await EditorAsyncUtility.NextFrameAsync(context.CancellationToken);
        }
    }

    public sealed class ResizeColumnAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            VisualElement element = await ActionHelpers.RequireElementAsync(context, parameters, "resize_column");
            Column column = ColumnActionHelpers.FindColumnOrThrow(element, parameters, "resize_column");
            string widthLiteral = ActionHelpers.Require(parameters, "resize_column", "width");

            if (!float.TryParse(widthLiteral, NumberStyles.Float, CultureInfo.InvariantCulture, out float widthPx) || widthPx <= 0f)
            {
                throw new UPilotFlowException(ErrorCodes.ActionParameterInvalid,
                    $"Action resize_column: invalid width '{widthLiteral}'. Must be a positive pixel value.");
            }

            context.Log($"resize_column: column={column.name} width={widthPx}px");
            column.width = new Length(widthPx, LengthUnit.Pixel);
            await EditorAsyncUtility.NextFrameAsync(context.CancellationToken);
        }
    }

    public sealed class ClickPopupItemAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            VisualElement element = await ActionHelpers.RequireElementAsync(context, parameters, "click_popup_item");
            string value = null;
            int? index = null;
            if (parameters.TryGetValue("value", out string valueLiteral) && !string.IsNullOrWhiteSpace(valueLiteral))
            {
                value = valueLiteral;
            }
            else if (parameters.TryGetValue("index", out string indexLiteral) && int.TryParse(indexLiteral, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedIndex))
            {
                index = parsedIndex;
            }
            else
            {
                throw new UPilotFlowException(ErrorCodes.ActionParameterMissing, "Action click_popup_item requires value or index.");
            }

            context.Log($"click_popup_item: selecting item '{value ?? index.ToString()}' on {ActionContext.ElementInfo(element)}");

            switch (element)
            {
                // LayerMaskField must come before MaskField because it inherits from MaskField
                case LayerMaskField layerMaskField:
                    int layerIndex = ResolveIndexOrValue(layerMaskField.choices, index, value, "LayerMaskField");
                    int layerBit = 1 << layerIndex;
                    layerMaskField.value = layerMaskField.value | layerBit;
                    break;

                case MaskField maskField:
                    int maskIndex = ResolveIndexOrValue(maskField.choices, index, value, "MaskField");
                    int maskBit = 1 << maskIndex;
                    maskField.value = maskField.value | maskBit;
                    break;

                case EnumFlagsField enumFlagsField when enumFlagsField.value != null:
                    int enumIndex = ResolveIndexOrValue(enumFlagsField.choices, index, value, "EnumFlagsField");
                    Type enumType = enumFlagsField.value.GetType();
                    int currentEnumValue = Convert.ToInt32(enumFlagsField.value);
                    int newEnumValue = currentEnumValue | (1 << enumIndex);
                    enumFlagsField.value = (Enum)Enum.ToObject(enumType, newEnumValue);
                    break;

                default:
                    if (!TryHandlePopupField(element, index, value, out string popupError))
                    {
                        throw new UPilotFlowException(ErrorCodes.ActionExecutionFailed,
                            $"Action click_popup_item does not support element type {element.GetType().Name}. {popupError}");
                    }
                    break;
            }

            await EditorAsyncUtility.NextFrameAsync(context.CancellationToken);
        }

        private static int ResolveIndexOrValue(List<string> choices, int? index, string value, string fieldType)
        {
            if (index.HasValue)
            {
                if (index.Value < 0 || index.Value >= choices.Count)
                {
                    throw new UPilotFlowException(ErrorCodes.ActionParameterInvalid,
                        $"Action click_popup_item: index {index.Value} out of range for {fieldType} (count={choices.Count}).");
                }
                return index.Value;
            }

            int foundIndex = choices.IndexOf(value);
            if (foundIndex < 0)
            {
                throw new UPilotFlowException(ErrorCodes.ActionParameterInvalid,
                    $"Action click_popup_item: value '{value}' not found in {fieldType} choices.");
            }
            return foundIndex;
        }

        private static bool TryHandlePopupField(VisualElement element, int? index, string value, out string error)
        {
            error = null;
            Type type = element.GetType();
            if (!type.Name.StartsWith("PopupField"))
            {
                error = "Element is not a PopupField.";
                return false;
            }

            PropertyInfo choicesProp = type.GetProperty("choices", BindingFlags.Public | BindingFlags.Instance);
            PropertyInfo indexProp = type.GetProperty("index", BindingFlags.Public | BindingFlags.Instance);
            if (choicesProp == null || indexProp == null || !indexProp.CanWrite)
            {
                error = "Missing choices or writable index property.";
                return false;
            }

            System.Collections.IList choices = choicesProp.GetValue(element) as System.Collections.IList;
            if (choices == null)
            {
                error = "choices is null.";
                return false;
            }

            int targetIndex;
            if (index.HasValue)
            {
                targetIndex = index.Value;
                if (targetIndex < 0 || targetIndex >= choices.Count)
                {
                    error = $"Index {targetIndex} out of range (count={choices.Count}).";
                    return false;
                }
            }
            else
            {
                targetIndex = -1;
                for (int i = 0; i < choices.Count; i++)
                {
                    if (string.Equals(choices[i]?.ToString(), value, StringComparison.Ordinal))
                    {
                        targetIndex = i;
                        break;
                    }
                }
                if (targetIndex < 0)
                {
                    error = $"Value '{value}' not found in PopupField choices.";
                    return false;
                }
            }

            indexProp.SetValue(element, targetIndex);
            return true;
        }
    }
}
