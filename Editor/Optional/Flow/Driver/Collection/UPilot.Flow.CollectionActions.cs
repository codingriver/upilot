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
    public sealed class SelectOptionAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            VisualElement element = await ActionHelpers.RequireElementAsync(context, parameters, "select_option");
            context.Log($"select_option: target {ActionContext.ElementInfo(element)}");
            AdvancedActionHelpers.SelectOptionOrThrow(element, parameters, "select_option");
            await EditorAsyncUtility.NextFrameAsync(context.CancellationToken);
        }
    }

    public sealed class ToggleMaskOptionAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            VisualElement element = await ActionHelpers.RequireElementAsync(context, parameters, "toggle_mask_option");
            context.Log($"toggle_mask_option: target {ActionContext.ElementInfo(element)}");
            AdvancedActionHelpers.ToggleMaskOptionOrThrow(element, parameters, "toggle_mask_option");
            await EditorAsyncUtility.NextFrameAsync(context.CancellationToken);
        }
    }

    public sealed class SelectListItemAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            VisualElement element = await ActionHelpers.RequireElementAsync(context, parameters, "select_list_item");
            if (parameters.TryGetValue("indices", out string indicesLiteral) && !string.IsNullOrWhiteSpace(indicesLiteral))
            {
                var indices = new List<int>();
                foreach (string part in indicesLiteral.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedIndex))
                    {
                        throw new UPilotFlowException(ErrorCodes.ActionParameterInvalid, $"Action select_list_item indices is invalid: {indicesLiteral}");
                    }

                    indices.Add(parsedIndex);
                }

                context.Log($"select_list_item: target {ActionContext.ElementInfo(element)} indices={indicesLiteral}");
                AdvancedActionHelpers.SelectListItemsOrThrow(element, indices, "select_list_item");
                await EditorAsyncUtility.NextFrameAsync(context.CancellationToken);
                return;
            }

            string indexLiteral = ActionHelpers.Require(parameters, "select_list_item", "index");
            if (!int.TryParse(indexLiteral, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
            {
                throw new UPilotFlowException(ErrorCodes.ActionParameterInvalid, $"Action select_list_item index is invalid: {indexLiteral}");
            }

            context.Log($"select_list_item: target {ActionContext.ElementInfo(element)} index={index}");
            AdvancedActionHelpers.SelectListItemOrThrow(element, index, "select_list_item");
            await EditorAsyncUtility.NextFrameAsync(context.CancellationToken);
        }
    }

    public sealed class DragReorderAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            VisualElement element = await ActionHelpers.RequireElementAsync(context, parameters, "drag_reorder");
            string fromLiteral = ActionHelpers.Require(parameters, "drag_reorder", "from_index");
            string toLiteral = ActionHelpers.Require(parameters, "drag_reorder", "to_index");
            if (!int.TryParse(fromLiteral, NumberStyles.Integer, CultureInfo.InvariantCulture, out int fromIndex)
                || !int.TryParse(toLiteral, NumberStyles.Integer, CultureInfo.InvariantCulture, out int toIndex))
            {
                throw new UPilotFlowException(ErrorCodes.ActionParameterInvalid, $"Action drag_reorder indices are invalid: from={fromLiteral}, to={toLiteral}");
            }

            context.Log($"drag_reorder: target {ActionContext.ElementInfo(element)} from={fromIndex} to={toIndex}");
            AdvancedActionHelpers.ReorderListItemOrThrow(element, fromIndex, toIndex, "drag_reorder");
            await EditorAsyncUtility.NextFrameAsync(context.CancellationToken);
        }
    }

    public sealed class SelectTreeItemAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            VisualElement element = await ActionHelpers.RequireElementAsync(context, parameters, "select_tree_item");
            context.Log($"select_tree_item: target {ActionContext.ElementInfo(element)}");
            AdvancedActionHelpers.SelectTreeItemOrThrow(element, parameters, "select_tree_item");
            await EditorAsyncUtility.NextFrameAsync(context.CancellationToken);
        }
    }

    public sealed class ToggleFoldoutAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            VisualElement element = await ActionHelpers.RequireElementAsync(context, parameters, "toggle_foldout");
            context.Log($"toggle_foldout: target {ActionContext.ElementInfo(element)}");
            AdvancedActionHelpers.ToggleFoldoutOrThrow(element, parameters, "toggle_foldout");
            await EditorAsyncUtility.NextFrameAsync(context.CancellationToken);
        }
    }
}
