// -----------------------------------------------------------------------
// UPilot Editor - lightweight MonoHook event record.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;

namespace CodingRiver.UPilot
{
    [Serializable]
    public sealed class UPilotMonoHookEvent
    {
        public long sequence;
        public string timestampUtc;
        public string pointId;
        public string kind;
        public string phase;
        public int frame;
        public string objectName;
        public int instanceId;
        public string hierarchyPath;
        public string scenePath;
        public string componentType;
        public string beforeValue;
        public string afterValue;
        public string stackTrace;
    }
}

