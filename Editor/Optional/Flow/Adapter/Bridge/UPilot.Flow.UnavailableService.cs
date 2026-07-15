// -----------------------------------------------------------------------
// UPilot Editor - unavailable UPilot Flow MCP bridge
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System.Threading;
using System.Threading.Tasks;

namespace CodingRiver.UPilot
{
    public sealed class UPilotFlowUnavailableService
    {
        private const string ErrorCode = "UIFLOW_UNAVAILABLE";

        private readonly UPilotBridge _bridge;

        public UPilotFlowUnavailableService(UPilotBridge bridge)
        {
            _bridge = bridge;
        }

        public void RegisterCommands()
        {
            _bridge.Router.Register("upilot_flow.run", HandleUnavailableAsync);
            _bridge.Router.Register("upilot_flow.results", HandleUnavailableAsync);
            _bridge.Router.Register("upilot_flow.cancel", HandleUnavailableAsync);
            _bridge.Router.Register("upilot_flow.force_reset", HandleUnavailableAsync);
        }

        private Task HandleUnavailableAsync(string id, string json, CancellationToken token)
        {
            return _bridge.SendErrorAsync(
                id,
                ErrorCode,
                "UPilot Flow is not compiled in this Unity version. Enable UPILOT_ENABLE_FLOW in Unity 6+ with the required UI Test Framework packages installed.",
                token,
                "upilot_flow.unavailable");
        }
    }
}
