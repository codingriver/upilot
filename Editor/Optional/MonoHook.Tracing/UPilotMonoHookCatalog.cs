// -----------------------------------------------------------------------
// UPilot Editor - MonoHook point catalog backed by attribute discovery.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;

namespace CodingRiver.UPilot
{
    public enum UPilotMonoHookPointCategory
    {
        Lifecycle,
        GameObject,
        Component,
        Transform,
        Custom,
    }

    public static class UPilotMonoHookCategoryId
    {
        public const string Lifecycle = "lifecycle";
        public const string GameObject = "gameObject";
        public const string Component = "component";
        public const string Transform = "transform";

        public static string FromLegacy(UPilotMonoHookPointCategory category)
        {
            switch (category)
            {
                case UPilotMonoHookPointCategory.Lifecycle: return Lifecycle;
                case UPilotMonoHookPointCategory.GameObject: return GameObject;
                case UPilotMonoHookPointCategory.Component: return Component;
                case UPilotMonoHookPointCategory.Transform: return Transform;
                default: return "custom";
            }
        }

        public static UPilotMonoHookPointCategory ToLegacy(string categoryId)
        {
            if (string.Equals(categoryId, Lifecycle, StringComparison.Ordinal)) return UPilotMonoHookPointCategory.Lifecycle;
            if (string.Equals(categoryId, GameObject, StringComparison.Ordinal)) return UPilotMonoHookPointCategory.GameObject;
            if (string.Equals(categoryId, Component, StringComparison.Ordinal)) return UPilotMonoHookPointCategory.Component;
            if (string.Equals(categoryId, Transform, StringComparison.Ordinal)) return UPilotMonoHookPointCategory.Transform;
            return UPilotMonoHookPointCategory.Custom;
        }
    }

    [Serializable]
    public sealed class UPilotMonoHookPointDefinition
    {
        public string Id;
        public string DisplayName;
        public string CategoryId;
        public string CategoryDisplayName;
        public UPilotMonoHookPointCategory Category;
        public bool DefaultEnabled;
        public bool HighFrequency;
        public int Order;
        public int CategoryOrder;

        internal UPilotMonoHookPointDefinition(
            Type providerType,
            UPilotMonoHookPointAttribute attribute)
        {
            Id = string.IsNullOrWhiteSpace(attribute.Id)
                ? UPilotMonoHookPointIdentity.FromProviderType(providerType)
                : attribute.Id;
            DisplayName = attribute.DisplayName;
            CategoryId = attribute.Category;
            CategoryDisplayName = string.IsNullOrEmpty(attribute.CategoryDisplayName)
                ? attribute.Category
                : attribute.CategoryDisplayName;
            Category = UPilotMonoHookCategoryId.ToLegacy(attribute.Category);
            DefaultEnabled = attribute.DefaultEnabled;
            HighFrequency = attribute.HighFrequency;
            Order = attribute.Order;
            CategoryOrder = attribute.CategoryOrder;
        }
    }

    public static class UPilotMonoHookCatalog
    {
        public static IReadOnlyList<UPilotMonoHookPointDefinition> All =>
            UPilotMonoHookRegistry.Instance.Definitions;

        public static UPilotMonoHookPointDefinition Find(string id)
        {
            return UPilotMonoHookRegistry.Instance.Find(id)?.Definition;
        }

        public static void Refresh()
        {
            UPilotMonoHookRegistry.Instance.Refresh();
        }
    }
}
