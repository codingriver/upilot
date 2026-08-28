// -----------------------------------------------------------------------
// UPilot Editor - public read-only access to manually captured MonoHook events.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace CodingRiver.UPilot
{
    public static class UPilotMonoHookTelemetry
    {
        public static int Count => UPilotMonoHookInstallationService.Events.Count;
        public static int DroppedCount => UPilotMonoHookInstallationService.Events.DroppedCount;
        public static int ConsoleDroppedCount => UPilotMonoHookInstallationService.ConsoleDroppedCount;
        public static int PerObjectDroppedCount => UPilotMonoHookInstallationService.PerObjectDroppedCount;
        public static int DuplicateDroppedCount => UPilotMonoHookInstallationService.DuplicateDroppedCount;

        public static List<UPilotMonoHookEvent> Read(int maxCount = 256)
        {
            return UPilotMonoHookInstallationService.Events.Read(maxCount);
        }

        public static List<UPilotMonoHookEvent> Snapshot(int maxCount = 256)
        {
            return UPilotMonoHookInstallationService.Events.Snapshot(maxCount);
        }

        public static int ExportJsonLines(string path, int maxCount = 2048)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Export path is required.", nameof(path));

            var events = Snapshot(maxCount);
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            using (var writer = new StreamWriter(path, false))
            {
                foreach (var hookEvent in events)
                    writer.WriteLine(JsonUtility.ToJson(hookEvent));
            }
            return events.Count;
        }

        public static void Clear()
        {
            UPilotMonoHookInstallationService.ClearEvents();
        }
    }
}
