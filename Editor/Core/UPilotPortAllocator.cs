// -----------------------------------------------------------------------
// upilot Editor — localhost port allocation helpers.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

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
            int maxAttempts = 100)
        {
            return UPilotPortRegistry.ForUser().Recommend(
                UPilotProjectConfig.ProjectRoot, startWsPort, startHttpPort, maxAttempts);
        }
    }
}
