// -----------------------------------------------------------------------
// UPilot Editor - declarative MonoHook point metadata.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;

namespace CodingRiver.UPilot
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class UPilotMonoHookPointAttribute : Attribute
    {
        public string Id { get; set; }
        public string DisplayName { get; }
        public string Category { get; }

        public string CategoryDisplayName { get; set; }
        public bool DefaultEnabled { get; set; }
        public bool HighFrequency { get; set; }
        public int Order { get; set; }
        public int CategoryOrder { get; set; }

        public UPilotMonoHookPointAttribute(string displayName, string category)
        {
            DisplayName = displayName;
            Category = category;
            CategoryDisplayName = category;
            DefaultEnabled = false;
        }

        public UPilotMonoHookPointAttribute(string id, string displayName, string category)
            : this(displayName, category)
        {
            Id = id;
        }
    }
}
