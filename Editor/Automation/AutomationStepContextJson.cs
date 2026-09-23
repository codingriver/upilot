using System;
using System.Xml.Linq;
using UnityEngine;

namespace CodingRiver.UPilot.Automation
{
    /// <summary>Detached snapshots only. Persistence goes through the guarded service callbacks.</summary>
    internal static class AutomationStepContextJson
    {
        internal static string Create(AutomationStepRun run, int index, bool validation = false)
        {
            var root = AutomationStepJsonCodec.ParseObject("{}");
            AutomationStepJsonCodec.SetProperty(root, "checkpoint",
                AutomationStepJsonCodec.ParseObject(run.steps[index].checkpointJson));
            AutomationStepJsonCodec.SetProperty(root, "shared",
                AutomationStepJsonCodec.ParseObject(run.sharedCheckpointJson));
            if (validation)
                AutomationStepJsonCodec.SetProperty(root, "plan",
                    AutomationStepJsonCodec.ParseObject(JsonUtility.ToJson(run.plan)));
            return AutomationStepJsonCodec.WriteJson(root);
        }
        internal static string Checkpoint(string contextJson)
        {
            var root = AutomationStepJsonCodec.ParseObject(contextJson);
            var checkpoint = root.Element("checkpoint");
            if (checkpoint == null || (string)checkpoint.Attribute("type") != "object")
                throw new InvalidOperationException("STEP_CHECKPOINT_INVALID");
            return AutomationStepJsonCodec.WriteJson(new XElement("root", checkpoint.Attributes(), checkpoint.Nodes()));
        }
    }
}
