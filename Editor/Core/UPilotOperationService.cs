// -----------------------------------------------------------------------
// UPilot Editor - command capability and operation diagnostics.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace CodingRiver.UPilot
{
    [Serializable]
    public sealed class OperationListMessage
    {
        public OperationListRequest payload;
    }

    [Serializable]
    public sealed class OperationListRequest
    {
        public string status = "";
        public int limit = 50;
    }

    [Serializable]
    public sealed class OperationGetMessage
    {
        public OperationGetRequest payload;
    }

    [Serializable]
    public sealed class OperationGetRequest
    {
        public string commandId = "";
    }

    [Serializable]
    public sealed class OperationStepPayload
    {
        public string time;
        public string step;
        public string detail;
    }

    [Serializable]
    public sealed class OperationPayload
    {
        public string commandId;
        public string commandName;
        public string description;
        public string phase;
        public string currentStep;
        public string receivedAt;
        public string completedAt;
        public long elapsedMs;
        public int progress;
        public bool isStuck;
        public bool resultReported;
        public string errorCode;
        public string errorMessage;
        public List<OperationStepPayload> steps = new();
    }

    [Serializable]
    public sealed class OperationListPayload
    {
        public int active;
        public int total;
        public int failed;
        public int stuck;
        public List<OperationPayload> operations = new();
    }

    [Serializable]
    public sealed class CommandCapabilityPayload
    {
        public string name;
        public string category;
        public bool idempotent;
        public bool destructive;
        public string playModePolicy;
        public string feature;
    }

    [Serializable]
    public sealed class CommandCapabilitiesPayload
    {
        public int registryVersion = 2;
        public int count;
        public List<CommandCapabilityPayload> commands = new();
    }

    public sealed class UPilotOperationService
    {
        private readonly UPilotBridge _bridge;

        public UPilotOperationService(UPilotBridge bridge)
        {
            _bridge = bridge;
        }

        public void RegisterCommands()
        {
            _bridge.Router.Register("operation.list", HandleListAsync);
            _bridge.Router.Register("operation.get", HandleGetAsync);
            _bridge.Router.Register("capabilities.list", HandleCapabilitiesAsync);
        }

        private async Task HandleListAsync(string id, string json, CancellationToken token)
        {
            var request = JsonUtility.FromJson<OperationListMessage>(json)?.payload ?? new OperationListRequest();
            var entries = UPilotOperationTracker.Instance.GetEntriesCopy();
            var status = (request.status ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(status))
                entries = entries.Where(entry => string.Equals(entry.Phase, status, StringComparison.OrdinalIgnoreCase)).ToList();

            var limit = Math.Max(1, Math.Min(request.limit, 200));
            var payload = new OperationListPayload
            {
                active = UPilotOperationTracker.Instance.ActiveCount,
                total = UPilotOperationTracker.Instance.TotalCount,
                failed = UPilotOperationTracker.Instance.FailedCount,
                stuck = UPilotOperationTracker.Instance.StuckCount,
                operations = entries.Skip(Math.Max(0, entries.Count - limit)).Select(ToPayload).ToList(),
            };
            await _bridge.SendResultAsync(id, "operation.list", payload, token);
        }

        private async Task HandleGetAsync(string id, string json, CancellationToken token)
        {
            var commandId = JsonUtility.FromJson<OperationGetMessage>(json)?.payload?.commandId ?? string.Empty;
            var entry = UPilotOperationTracker.Instance.GetEntriesCopy()
                .LastOrDefault(item => string.Equals(item.CommandId, commandId, StringComparison.Ordinal));
            if (entry == null)
            {
                await _bridge.SendErrorAsync(id, "OPERATION_NOT_FOUND", $"Operation not found: {commandId}", token, "operation.get");
                return;
            }
            await _bridge.SendResultAsync(id, "operation.get", ToPayload(entry), token);
        }

        private async Task HandleCapabilitiesAsync(string id, string json, CancellationToken token)
        {
            var descriptors = _bridge.Router.GetDescriptors().OrderBy(item => item.Name).ToList();
            var payload = new CommandCapabilitiesPayload { count = descriptors.Count };
            foreach (var descriptor in descriptors)
            {
                payload.commands.Add(new CommandCapabilityPayload
                {
                    name = descriptor.Name,
                    category = descriptor.Category,
                    idempotent = descriptor.Idempotent,
                    destructive = descriptor.Destructive,
                    playModePolicy = descriptor.PlayModePolicy,
                    feature = descriptor.Feature,
                });
            }
            await _bridge.SendResultAsync(id, "capabilities.list", payload, token);
        }

        private static OperationPayload ToPayload(OperationLogEntry entry)
        {
            var elapsed = entry.CompletedAt.HasValue
                ? entry.ElapsedMs
                : (long)(DateTime.Now - entry.ReceivedAt).TotalMilliseconds;
            var payload = new OperationPayload
            {
                commandId = entry.CommandId,
                commandName = entry.CommandName,
                description = entry.Description,
                phase = entry.Phase,
                currentStep = entry.CurrentStep,
                receivedAt = entry.ReceivedAt.ToUniversalTime().ToString("O"),
                completedAt = entry.CompletedAt?.ToUniversalTime().ToString("O") ?? string.Empty,
                elapsedMs = Math.Max(0, elapsed),
                progress = entry.Progress,
                isStuck = entry.IsStuck,
                resultReported = entry.ResultReported,
                errorCode = entry.ErrorCode,
                errorMessage = entry.ErrorMessage,
            };
            lock (entry.Steps)
            {
                foreach (var step in entry.Steps)
                {
                    payload.steps.Add(new OperationStepPayload
                    {
                        time = step.Time.ToUniversalTime().ToString("O"),
                        step = step.Step,
                        detail = step.Detail,
                    });
                }
            }
            return payload;
        }
    }
}
