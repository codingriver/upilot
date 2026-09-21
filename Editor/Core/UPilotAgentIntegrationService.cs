// SPDX-License-Identifier: MIT
using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace CodingRiver.UPilot
{
    public sealed class UPilotAgentIntegrationService
    {
        [Serializable] private sealed class Request { public bool apply; public string scope; }
        [Serializable] private sealed class Message { public Request payload; }
        private readonly UPilotBridge bridge;
        public UPilotAgentIntegrationService(UPilotBridge bridge) { this.bridge = bridge; }
        public void RegisterCommands()
        {
            bridge.Router.Register("agent.integrations.check", (id, json, token) => Handle(id, json, token, false));
            bridge.Router.Register(new CommandDescriptor("agent.integrations.sync", idempotent: false, destructive: true),
                (id, json, token) => Handle(id, json, token, true));
        }
        private async Task Handle(string id, string json, CancellationToken token, bool sync)
        {
            var request = JsonUtility.FromJson<Message>(json)?.payload ?? new Request();
            var command = sync ? "agent.integrations.sync" : "agent.integrations.check";
            var result = await EvaluateAsync(id, sync && request.apply, request.scope, command, token);
            await bridge.SendResultAsync(id, command, result, token);
        }

        internal async Task<AgentIntegrationResult> EvaluateAsync(string id, bool apply, string scope,
            string command, CancellationToken token)
        {
            var completion = new TaskCompletionSource<AgentIntegrationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            // Bridge handlers run off-thread. PackageInfo and project configuration require the Editor thread,
            // even for read-only previews; use the tracked queue just like other Unity service entry points.
            bridge.EnqueueTracked(id, () =>
            {
                if (token.IsCancellationRequested) { completion.TrySetCanceled(); return; }
                try
                {
                    AgentIntegrationResult result;
                    if (apply && UPilotProjectConfig.Load().safety?.writeAccessApproved != true)
                        result = new AgentIntegrationResult { ok = false, status = "write_not_authorized",
                            error = "Project write authorization is required.", dryRun = false };
                    else if (!string.IsNullOrEmpty(scope) && scope != "all" && scope != "shared")
                        result = new AgentIntegrationResult { ok = false, status = "invalid_scope", error = "Unsupported public scope." };
                    else
                        result = UPilotAgentSetup.SyncAgentIntegrations(apply,
                            scope == "shared" ? AgentIntegrationScope.SharedAgents : AgentIntegrationScope.All, "bridge:" + command);
                    completion.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    completion.TrySetResult(new AgentIntegrationResult {
                        ok = false, status = "failed", dryRun = !apply, error = ex.Message });
                }
            });
            using (token.Register(() => completion.TrySetCanceled()))
                return await completion.Task;
        }
    }
}
