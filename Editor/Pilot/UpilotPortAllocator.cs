// -----------------------------------------------------------------------
// upilot Editor — localhost port allocation helpers.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System.Net;
using System.Net.Sockets;

namespace codingriver.upilot
{
    public static class UpilotPortAllocator
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
            int startWsPort = UpilotBridge.DefaultWsPort,
            int startHttpPort = UpilotBridge.DefaultHttpPort,
            int maxAttempts = 100)
        {
            var ws = startWsPort;
            var http = startHttpPort;
            for (var i = 0; i < maxAttempts; i++)
            {
                if (ws != http && IsPortAvailable(ws) && IsPortAvailable(http))
                    return (ws, http);

                ws++;
                http++;
            }

            return (startWsPort, startHttpPort);
        }
    }
}
