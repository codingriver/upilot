// -----------------------------------------------------------------------
// UPilot Editor — https://github.com/codingriver/upilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CodingRiver.UPilot
{
    /// <summary>
    /// Central command router: maps command names to async handler delegates.
    /// Handlers are registered by service modules during Bridge initialization.
    /// Automatically wraps every invocation with <see cref="OperationContext"/> for full lifecycle tracking.
    /// </summary>
    public sealed class UPilotCommandRouter
    {
        public delegate Task CommandHandler(string id, string json, CancellationToken token);

        private readonly Dictionary<string, CommandHandler> _handlers = new();
        private readonly Dictionary<string, CommandDescriptor> _descriptors = new();

        /// <summary>Register a handler for a command name. Overwrites if already registered.</summary>
        public void Register(string commandName, CommandHandler handler)
        {
            Register(new CommandDescriptor(commandName), handler);
        }

        public void Register(CommandDescriptor descriptor, CommandHandler handler)
        {
            var commandName = descriptor?.Name;
            if (string.IsNullOrEmpty(commandName))
                throw new ArgumentNullException(nameof(commandName));
            _handlers[commandName] = handler ?? throw new ArgumentNullException(nameof(handler));
            _descriptors[commandName] = descriptor;
        }

        /// <summary>
        /// Try to dispatch a command. Returns true if a handler was found and invoked.
        /// Automatically creates OperationContext, tracks lifecycle, and handles exceptions.
        /// </summary>
        public async Task<bool> TryHandleAsync(string commandName, string id, string json, CancellationToken token)
        {
            if (!_handlers.TryGetValue(commandName, out var handler))
                return false;

            var tracker = UPilotOperationTracker.Instance;
            var ctx = tracker.BeginOperation(id, commandName);

            try
            {
                await handler(id, json, token);

                if (ctx != null)
                {
                    // Only auto-complete if handler didn't already Complete/Fail
                    var entry = GetEntryForContext(id);
                    if (entry != null && !entry.CompletedAt.HasValue)
                        ctx.Complete();
                }
            }
            catch (OperationCanceledException)
            {
                ctx?.Fail("CANCELLED", "操作已取消");
                throw;
            }
            catch (Exception ex)
            {
                ctx?.Fail("EXCEPTION", ex.Message);
                throw;
            }
            finally
            {
                tracker.EndOperation(id);
            }

            return true;
        }

        /// <summary>Number of registered commands (for diagnostics).</summary>
        public int Count => _handlers.Count;

        public List<CommandDescriptor> GetDescriptors()
        {
            return new List<CommandDescriptor>(_descriptors.Values);
        }

        private static OperationLogEntry GetEntryForContext(string commandId)
        {
            var entries = UPilotOperationTracker.Instance.GetEntriesCopy();
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                if (entries[i].CommandId == commandId)
                    return entries[i];
            }
            return null;
        }
    }

    [Serializable]
    public sealed class CommandDescriptor
    {
        public string Name;
        public string Category;
        public bool Idempotent;
        public bool Destructive;
        public string PlayModePolicy;
        public string Feature;
        public int TimeoutMs;
        public string[] CapabilityRequirements;

        public CommandDescriptor(
            string name,
            string category = "core",
            bool idempotent = true,
            bool destructive = false,
            string playModePolicy = "allowed",
            string feature = "core",
            int timeoutMs = 30000,
            string[] capabilityRequirements = null)
        {
            Name = name ?? string.Empty;
            Category = category ?? "core";
            Idempotent = idempotent;
            Destructive = destructive;
            PlayModePolicy = playModePolicy ?? "allowed";
            Feature = feature ?? "core";
            TimeoutMs = timeoutMs;
            CapabilityRequirements = capabilityRequirements ?? Array.Empty<string>();
        }
    }
}
