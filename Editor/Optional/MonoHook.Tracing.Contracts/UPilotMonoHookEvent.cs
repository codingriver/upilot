// -----------------------------------------------------------------------
// UPilot Editor - lightweight MonoHook event record.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using UnityEngine;

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
        public string targetType;
        public string targetGlobalObjectId;
        public string eventSource;
        public string methodSignature;
        public string beforeValue;
        public string afterValue;
        public string stackTrace;
        public string filterProfileId;
        public string filterProfileName;
        public string filterReason;

        [NonSerialized] public UnityEngine.Object target;
        [NonSerialized] internal bool filterEvaluated;
    }
}
