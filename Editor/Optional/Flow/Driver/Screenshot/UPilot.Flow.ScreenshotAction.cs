using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using static UnityEditor.TypeCache;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using InputSystemKey = UnityEngine.InputSystem.Key;
using UnityEngine.UIElements;

namespace CodingRiver.UPilot.Flow
{
    public sealed class ScreenshotAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            if (context.ScreenshotManager == null)
            {
                throw new UPilotFlowException(ErrorCodes.ScreenshotSaveFailed, "Screenshot manager is not initialized.");
            }

            if (!parameters.TryGetValue("tag", out string tag) || string.IsNullOrWhiteSpace(tag))
            {
                tag = context.CurrentStepId;
            }

            context.Log($"screenshot: tag={tag}, case={context.CurrentCaseName}, step={context.CurrentStepIndex}");
            string path = await context.ScreenshotManager.CaptureAsync(context.CurrentCaseName, context.CurrentStepIndex, tag, context.CancellationToken);
            if (path == null)
            {
                context.Log("screenshot: skipped (unfocused)");
                return;
            }
            context.Log($"screenshot: saved {path}");
            context.AddAttachment(path);
        }
    }
}
