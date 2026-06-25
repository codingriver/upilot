// -----------------------------------------------------------------------
// UnityPilot Editor - unavailable UnityUIFlow MCP bridge
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System.Threading;
using System.Threading.Tasks;

namespace codingriver.unity.pilot
{
    public sealed class UnityPilotUnityUIFlowUnavailableService
    {
        private const string ErrorCode = "UIFLOW_UNAVAILABLE";

        private readonly UnityPilotBridge _bridge;

        public UnityPilotUnityUIFlowUnavailableService(UnityPilotBridge bridge)
        {
            _bridge = bridge;
        }

        public void RegisterCommands()
        {
            _bridge.Router.Register("unityuiflow.run", HandleUnavailableAsync);
            _bridge.Router.Register("unityuiflow.results", HandleUnavailableAsync);
            _bridge.Router.Register("unityuiflow.cancel", HandleUnavailableAsync);
            _bridge.Router.Register("unityuiflow.force_reset", HandleUnavailableAsync);
        }

        private Task HandleUnavailableAsync(string id, string json, CancellationToken token)
        {
            return _bridge.SendErrorAsync(
                id,
                ErrorCode,
                "UnityUIFlow is not compiled in this Unity version. Enable UNITYPILOT_ENABLE_UIFLOW in Unity 6+ with the required UI Test Framework packages installed.",
                token,
                "unityuiflow.unavailable");
        }
    }
}
