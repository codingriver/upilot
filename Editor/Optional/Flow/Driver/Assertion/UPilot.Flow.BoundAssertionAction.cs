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
    public sealed class AssertBoundValueAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            VisualElement element = await ActionHelpers.RequireElementAsync(context, parameters, "assert_bound_value");
            context.Log($"assert_bound_value: target {ActionContext.ElementInfo(element)}");
            AdvancedActionHelpers.AssertBoundValueOrThrow(element, parameters, "assert_bound_value");
            await EditorAsyncUtility.NextFrameAsync(context.CancellationToken);
        }
    }
}
