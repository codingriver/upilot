// SPDX-License-Identifier: MIT
using System;
using UnityEditor;

namespace CodingRiver.UPilot
{
    // Initialize on the Editor thread; readers may run on Bridge worker threads.
    [InitializeOnLoad]
    internal static class UPilotServiceLifetime
    {
        private const string Key = "UPilot.ServiceLifetime";
        private static volatile string _id;
        static UPilotServiceLifetime()
        {
            _id = SessionState.GetString(Key, "");
            if (_id.Length == 0) Advance();
        }
        internal static string Id => _id;
        internal static string Advance()
        {
            var id = Guid.NewGuid().ToString("N");
            SessionState.SetString(Key, id);
            _id = id;
            return id;
        }
    }
}
