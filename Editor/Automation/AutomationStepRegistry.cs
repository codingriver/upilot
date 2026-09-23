using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace CodingRiver.UPilot.Automation
{
    /// <summary>TypeCache discovery without constructing steps.</summary>
    public sealed class AutomationStepRegistry
    {
        private readonly Dictionary<string, Type> _types = new(StringComparer.Ordinal);
        private readonly Dictionary<string, AutomationStepDescriptor> _descriptors = new(StringComparer.Ordinal);
        public IReadOnlyList<AutomationDiagnostic> Diagnostics => _diagnostics.AsReadOnly();
        private readonly List<AutomationDiagnostic> _diagnostics = new();
        public AutomationStepRegistry() : this(TypeCache.GetTypesWithAttribute<AutomationStepAttribute>()) { }
        internal AutomationStepRegistry(IEnumerable<Type> candidates) : this(candidates.Select(type =>
            new KeyValuePair<Type, AutomationStepAttribute>(type,
                (AutomationStepAttribute)Attribute.GetCustomAttribute(type, typeof(AutomationStepAttribute), false)))) { }
        internal AutomationStepRegistry(IEnumerable<KeyValuePair<Type, AutomationStepAttribute>> candidates)
        {
            foreach (var entry in candidates.OrderBy(t => t.Key.FullName, StringComparer.Ordinal))
            {
                var type = entry.Key;
                var attribute = entry.Value;
                if (attribute == null) continue;
                if (string.IsNullOrWhiteSpace(attribute.Id) || attribute.Id != attribute.Id.Trim())
                    Add("STEP_ID_INVALID", type.FullName);
                else if (_descriptors.ContainsKey(attribute.Id))
                {
                    Add("STEP_ID_DUPLICATE", attribute.Id);
                    _types.Remove(attribute.Id);
                }
                else
                {
                    _descriptors.Add(attribute.Id, new AutomationStepDescriptor
                    {
                        stepId = attribute.Id, description = attribute.Description,
                        argumentsExample = attribute.ArgumentsExample,
                        typeIdentity = type.AssemblyQualifiedName,
                        timeoutSeconds = attribute.TimeoutSeconds, pollIntervalSeconds = attribute.PollIntervalSeconds,
                    });
                    if (!type.IsClass || type.IsAbstract || type.IsGenericType || type.ContainsGenericParameters
                        || !typeof(IAutomationStep).IsAssignableFrom(type) || type.GetConstructor(Type.EmptyTypes) == null)
                        Add("STEP_CONTRACT_INVALID", attribute.Id);
                    else if (!AutomationStepJson.Finite(attribute.TimeoutSeconds) || attribute.TimeoutSeconds <= 0
                        || !AutomationStepJson.Finite(attribute.PollIntervalSeconds) || attribute.PollIntervalSeconds < 0)
                        Add("STEP_METADATA_INVALID", attribute.Id);
                    else _types.Add(attribute.Id, type);
                }
            }
        }
        public AutomationStepDescriptor[] Catalog() => _descriptors.Values.Select(AutomationStepJson.Copy).ToArray();
        internal bool TryGet(string id, out AutomationStepDescriptor descriptor) =>
            _descriptors.TryGetValue(id ?? "", out descriptor) && _types.ContainsKey(id ?? "");
        internal IAutomationStep Create(string id) => (IAutomationStep)Activator.CreateInstance(_types[id]);
        private void Add(string code, string id) => AutomationCatalog.Add(_diagnostics, code, code + ": " + id, id);
    }
}
