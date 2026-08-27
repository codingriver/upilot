// -----------------------------------------------------------------------
// UPilot Editor - attribute-discovered MonoHook point registry.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot
{
    public sealed class UPilotMonoHookPointDescriptor
    {
        public UPilotMonoHookPointDefinition Definition { get; }
        public Type ProviderType { get; }
        public IUPilotMonoHookPointProvider Provider { get; }
        public string DiscoveryError { get; }
        public bool IsValid => Provider != null && string.IsNullOrEmpty(DiscoveryError);

        internal UPilotMonoHookPointDescriptor(
            UPilotMonoHookPointDefinition definition,
            Type providerType,
            IUPilotMonoHookPointProvider provider,
            string discoveryError)
        {
            Definition = definition;
            ProviderType = providerType;
            Provider = provider;
            DiscoveryError = discoveryError ?? string.Empty;
        }
    }

    public sealed class UPilotMonoHookRegistry
    {
        private static readonly Lazy<UPilotMonoHookRegistry> LazyInstance =
            new Lazy<UPilotMonoHookRegistry>(() => new UPilotMonoHookRegistry());

        private readonly List<UPilotMonoHookPointDescriptor> _points =
            new List<UPilotMonoHookPointDescriptor>();
        private readonly List<UPilotMonoHookPointDefinition> _definitions =
            new List<UPilotMonoHookPointDefinition>();
        private readonly Dictionary<string, UPilotMonoHookPointDescriptor> _byId =
            new Dictionary<string, UPilotMonoHookPointDescriptor>(StringComparer.Ordinal);

        public static UPilotMonoHookRegistry Instance => LazyInstance.Value;
        public IReadOnlyList<UPilotMonoHookPointDescriptor> Points => _points;
        public IReadOnlyList<UPilotMonoHookPointDefinition> Definitions => _definitions;
        public UPilotMonoHookContext Context { get; }

        private UPilotMonoHookRegistry()
        {
            Context = new UPilotMonoHookContext(
                new UPilotMonoHookFactory(),
                new UPilotMonoHookEventSink(),
                Application.unityVersion);
            Refresh();
        }

        public UPilotMonoHookPointDescriptor Find(string pointId)
        {
            if (string.IsNullOrEmpty(pointId)) return null;
            _byId.TryGetValue(pointId, out var descriptor);
            return descriptor;
        }

        public void Refresh()
        {
            var previousProviders = _points
                .Where(point => point.Provider != null && point.ProviderType != null)
                .GroupBy(point => point.Definition.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            _points.Clear();
            _definitions.Clear();
            _byId.Clear();

            var discovered = new List<UPilotMonoHookPointDescriptor>();
            foreach (var type in TypeCache.GetTypesWithAttribute<UPilotMonoHookPointAttribute>())
            {
                var attribute = type.GetCustomAttribute<UPilotMonoHookPointAttribute>(false);
                if (attribute == null) continue;

                var definition = new UPilotMonoHookPointDefinition(type, attribute);
                string error = Validate(type, definition);
                if (string.IsNullOrWhiteSpace(definition.Id))
                    definition.Id = "__invalid__:" + (type.FullName ?? type.Name);
                IUPilotMonoHookPointProvider provider = null;
                if (string.IsNullOrEmpty(error))
                {
                    try
                    {
                        if (previousProviders.TryGetValue(definition.Id, out var previous) && previous.ProviderType == type)
                            provider = previous.Provider;
                        else
                            provider = Activator.CreateInstance(type, true) as IUPilotMonoHookPointProvider;
                        if (provider == null)
                            error = "无法创建 IUPilotMonoHookPointProvider 实例";
                    }
                    catch (Exception ex)
                    {
                        error = "创建 Provider 失败：" + ex.GetBaseException().Message;
                    }
                }

                discovered.Add(new UPilotMonoHookPointDescriptor(definition, type, provider, error));
            }

            foreach (var group in discovered.GroupBy(item => item.Definition.Id, StringComparer.Ordinal))
            {
                var entries = group.ToList();
                if (entries.Count == 1)
                {
                    Add(entries[0]);
                    continue;
                }

                var first = entries[0];
                string types = string.Join(", ", entries.Select(item => item.ProviderType?.FullName ?? "(unknown)"));
                Add(new UPilotMonoHookPointDescriptor(
                    first.Definition,
                    first.ProviderType,
                    null,
                    "点位 ID 重复：" + types));
            }

            _points.Sort(CompareDescriptors);
            _definitions.AddRange(_points.Select(point => point.Definition));
        }

        private void Add(UPilotMonoHookPointDescriptor descriptor)
        {
            _points.Add(descriptor);
            _byId[descriptor.Definition.Id] = descriptor;
        }

        private static string Validate(Type type, UPilotMonoHookPointDefinition definition)
        {
            if (string.IsNullOrWhiteSpace(definition.Id)) return "点位 ID 不能为空";
            if (string.IsNullOrWhiteSpace(definition.DisplayName)) return "显示名称不能为空";
            if (string.IsNullOrWhiteSpace(definition.CategoryId)) return "分类不能为空";
            if (type == null || type.IsAbstract) return "Provider 必须是非抽象类";
            if (!typeof(IUPilotMonoHookPointProvider).IsAssignableFrom(type))
                return "Provider 必须实现 IUPilotMonoHookPointProvider";
            if (type.GetConstructor(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    Type.EmptyTypes,
                    null) == null)
            {
                return "Provider 必须提供无参构造方法";
            }
            return string.Empty;
        }

        private static int CompareDescriptors(
            UPilotMonoHookPointDescriptor left,
            UPilotMonoHookPointDescriptor right)
        {
            int result = left.Definition.CategoryOrder.CompareTo(right.Definition.CategoryOrder);
            if (result != 0) return result;
            result = string.Compare(left.Definition.CategoryDisplayName, right.Definition.CategoryDisplayName, StringComparison.Ordinal);
            if (result != 0) return result;
            result = left.Definition.Order.CompareTo(right.Definition.Order);
            if (result != 0) return result;
            result = string.Compare(left.Definition.DisplayName, right.Definition.DisplayName, StringComparison.Ordinal);
            if (result != 0) return result;
            return string.Compare(left.Definition.Id, right.Definition.Id, StringComparison.Ordinal);
        }
    }
}
