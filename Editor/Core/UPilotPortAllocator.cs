// -----------------------------------------------------------------------
// upilot Editor — localhost port allocation helpers.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace CodingRiver.UPilot
{
    public static class UPilotPortAllocator
    {
        public static bool IsPortAvailable(int port)
        {
            if (port <= 0 || port > 65535)
                return false;

            TcpListener listener = null;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                listener?.Stop();
            }
        }

        public static (int wsPort, int httpPort) FindAvailablePair(
            int startWsPort = UPilotBridge.DefaultWsPort,
            int startHttpPort = UPilotBridge.DefaultHttpPort,
            int maxAttempts = 100) =>
            FindAvailablePair(startWsPort, startHttpPort, maxAttempts, IsPortAvailable);

        internal static (int wsPort, int httpPort) FindAvailablePair(
            int ws, int http, int attempts, Func<int, bool> available)
        {
            if (ws == http) http = http < 65535 ? http + 1 : http - 1;
            // The reference index is deliberately not consulted; only real bindability matters.
            var wsCandidates = new List<int>(2);
            for (var i = 0; i < attempts && ws <= 65535; i++, ws++)
            {
                if (ws > 0 && available(ws))
                {
                    wsCandidates.Add(ws);
                    if (wsCandidates.Count == 2) break;
                }
            }
            for (var i = 0; i < attempts && http <= 65535 && wsCandidates.Count > 0; i++, http++)
            {
                if (http <= 0 || !available(http)) continue;
                foreach (var candidate in wsCandidates)
                    if (candidate != http) return (candidate, http);
            }
            throw new IOException("未找到可监听的 HTTP/WS 端口对，请手动指定其他端口范围。");
        }
    }
}
