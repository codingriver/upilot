// -----------------------------------------------------------------------
// UPilot Editor - stable MonoHook point identity helpers.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;

namespace CodingRiver.UPilot
{
    public static class UPilotMonoHookPointIdentity
    {
        public static string FromProviderType(Type providerType)
        {
            if (providerType == null) throw new ArgumentNullException(nameof(providerType));

            string assemblyName = providerType.Assembly.GetName().Name;
            if (string.IsNullOrEmpty(assemblyName)) assemblyName = "UnknownAssembly";

            string typeName = providerType.FullName;
            if (string.IsNullOrEmpty(typeName)) typeName = providerType.Name;

            return $"provider:{assemblyName}:{typeName}";
        }
    }
}
