using System;

namespace CodingRiver.UPilot.Automation
{
    [Serializable]
    public sealed class AutomationStepCapabilitiesPayload
    {
        public int discoveryVersion = 1;
        public int[] supportedPlanVersions = { 1 };
        public string callbackContract = "jsonString";
        public string[] captureOwnershipModes = { "planOwned", "operationBorrowed" };
    }

    // Independent of service initialization, registry discovery and executor state.
    internal static class AutomationStepCapabilities
    {
        internal static AutomationStepCapabilitiesPayload Describe() => new();
    }
}
