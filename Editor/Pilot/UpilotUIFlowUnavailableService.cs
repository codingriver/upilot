// -----------------------------------------------------------------------
// Upilot Editor - unavailable UIFlow MCP bridge
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System.Threading;
using System.Threading.Tasks;

namespace codingriver.upilot
{
    public sealed class UpilotUIFlowUnavailableService
    {
        private const string ErrorCode = "UIFLOW_UNAVAILABLE";

        private readonly UpilotBridge _bridge;

        public UpilotUIFlowUnavailableService(UpilotBridge bridge)
        {
            _bridge = bridge;
        }

        public void RegisterCommands()
        {
            _bridge.Router.Register("uiflow.run", HandleUnavailableAsync);
            _bridge.Router.Register("uiflow.results", HandleUnavailableAsync);
            _bridge.Router.Register("uiflow.cancel", HandleUnavailableAsync);
            _bridge.Router.Register("uiflow.force_reset", HandleUnavailableAsync);
        }

        private Task HandleUnavailableAsync(string id, string json, CancellationToken token)
        {
            return _bridge.SendErrorAsync(
                id,
                ErrorCode,
                "UIFlow is not compiled in this Unity version. Enable UPILOT_ENABLE_UIFLOW in Unity 6+ with the required UI Test Framework packages installed.",
                token,
                "uiflow.unavailable");
        }
    }
}
