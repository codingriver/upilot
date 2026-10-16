// SPDX-License-Identifier: MIT
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using NUnit.Framework;
using StopManager = CodingRiver.UPilot.UPilotMcpServerManager;

namespace CodingRiver.UPilot.Tests
{
#if UNITY_EDITOR_WIN
    public sealed class UPilotStopPortSnapshotTests
    {
        [TestCase(true)]
        [TestCase(false)]
        public void ListenerAppearingInFinalSnapshotIsValidatedAndCanBeStopped(bool ownsHttp)
        {
            var http = FreePort();
            var ws = OtherPort(http);
            var scope = Scope(http, ws);
            OwnedListener child = null;
            try
            {
                using var discovery = StopManager.DiscoverStopTargets(scope, Array.Empty<int>(),
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 8000, beforeFinalPortSnapshot: () =>
                    {
                        child = OwnedListener.Start(http, ws, ownsHttp ? http : ws);
                        child.BeginListening();
                    });

                Assert.That(child, Is.Not.Null);
                Assert.That(discovery.Targets.Select(target => target.ProcessId), Is.EqualTo(new[] { child.Process.Id }));
                Assert.That(discovery.UnmatchedPortOwners, Is.Empty,
                    "A newly observed owner must be checked before it is labelled unmatched.");
                Assert.That(discovery.ListeningPorts[child.Process.Id], Does.Contain(ownsHttp ? http : ws));
                var target = discovery.Targets.Single();
                Assert.That(target.EntryPath, Is.EqualTo(scope.ServerExe));
                target.Terminate();
                Assert.That(child.Process.WaitForExit(5000), Is.True);
                Assert.That(SpinWait.SpinUntil(() => UPilotPortAllocator.IsPortAvailable(http) &&
                    UPilotPortAllocator.IsPortAvailable(ws), 5000), Is.True);
            }
            finally { child?.Dispose(); }
        }

        [Test]
        public void AlreadyVerifiedCandidateBecomingListenerDoesNotAcquireDuplicateTargets()
        {
            var http = FreePort();
            var ws = OtherPort(http);
            using var child = OwnedListener.Start(http, ws, http);
            using var discovery = StopManager.DiscoverStopTargets(Scope(http, ws), new[] { child.Process.Id },
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 8000,
                beforeFinalPortSnapshot: child.BeginListening);

            Assert.That(discovery.Targets.Select(target => target.ProcessId), Is.EqualTo(new[] { child.Process.Id }));
            Assert.That(discovery.UnmatchedPortOwners, Is.Empty);
            Assert.That(discovery.ListeningPorts[child.Process.Id], Does.Contain(http));
            Assert.That(child.Process.HasExited, Is.False, "Discovery must retain its handle without terminating the target.");
        }

        [TestCase("wrong_port")]
        [TestCase("duplicate_port")]
        [TestCase("different_entry")]
        public void NewListenerStillRequiresExactEntryAndUniqueMatchingPorts(string mismatch)
        {
            var http = FreePort();
            var ws = OtherPort(http);
            var declaredWs = mismatch == "wrong_port" ? OtherPort(http, ws) : ws;
            var scope = mismatch == "different_entry"
                ? new StopManager.ServerStopScope(UPilotProjectConfig.ProjectRoot, "",
                    Path.Combine(Path.GetTempPath(), "other-upilot-server.exe"), http, ws)
                : Scope(http, ws);
            OwnedListener child = null;
            try
            {
                using var discovery = StopManager.DiscoverStopTargets(scope, Array.Empty<int>(),
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 8000, beforeFinalPortSnapshot: () =>
                    {
                        child = OwnedListener.Start(http, declaredWs, http,
                            mismatch == "duplicate_port" ? " --http-port " + http : "");
                        child.BeginListening();
                    });

                Assert.That(discovery.Targets, Is.Empty);
                Assert.That(discovery.UnmatchedPortOwners, Is.EqualTo(new[] { child.Process.Id }));
                Assert.That(child.Process.HasExited, Is.False,
                    "A new port owner with mismatching identity remains outside the stop target set.");
            }
            finally { child?.Dispose(); }
        }

        private static StopManager.ServerStopScope Scope(int http, int ws) =>
            new(UPilotProjectConfig.ProjectRoot, "", OwnedListener.Executable, http, ws);

        private static int OtherPort(params int[] excluded)
        {
            int port;
            do { port = FreePort(); } while (excluded.Contains(port));
            return port;
        }

        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
            finally { listener.Stop(); }
        }

        // Every process here belongs to this fixture; the actual Unity and MCP Server are never targets.
        private sealed class OwnedListener : IDisposable
        {
            internal readonly Process Process;
            private readonly string _script;
            internal static string Executable => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");

            private OwnedListener(Process process, string script) { Process = process; _script = script; }

            internal static OwnedListener Start(int http, int ws, int listeningPort, string extraArguments = "")
            {
                var script = Path.Combine(Path.GetTempPath(), "upilot-snapshot-listener-" + Guid.NewGuid().ToString("N") + ".ps1");
                File.WriteAllText(script,
                    "$ErrorActionPreference='Stop'\n" +
                    "[Console]::WriteLine('READY'); [Console]::Out.Flush()\n" +
                    "if ([Console]::ReadLine() -ne 'listen') { exit 2 }\n" +
                    "$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, " + listeningPort + ")\n" +
                    "try { $listener.Start(); [Console]::WriteLine('LISTENING'); [Console]::Out.Flush(); Start-Sleep -Seconds 60 } finally { $listener.Stop() }\n",
                    new UTF8Encoding(false));
                Process process;
                try
                {
                    process = Process.Start(new ProcessStartInfo
                    {
                        FileName = Executable,
                        Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script +
                            "\" --http-port " + http + " --port " + ws + extraArguments,
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    });
                }
                catch { File.Delete(script); throw; }
                if (process == null) { File.Delete(script); throw new InvalidOperationException("The owned listener did not start."); }
                var owned = new OwnedListener(process, script);
                try { owned.ExpectOutput("READY"); return owned; }
                catch { owned.Dispose(); throw; }
            }

            internal void BeginListening()
            {
                Process.StandardInput.WriteLine("listen");
                Process.StandardInput.Flush();
                ExpectOutput("LISTENING");
            }

            private void ExpectOutput(string expected)
            {
                var line = Process.StandardOutput.ReadLineAsync();
                Assert.That(line.Wait(5000), Is.True, "The owned listener did not reach " + expected + ".");
                Assert.That(line.Result, Is.EqualTo(expected));
                Assert.That(Process.HasExited, Is.False);
            }

            public void Dispose()
            {
                try
                {
                    if (!Process.HasExited)
                    {
                        Process.Kill();
                        if (!Process.WaitForExit(5000)) throw new TimeoutException("The owned snapshot listener did not exit.");
                    }
                }
                finally { Process.Dispose(); File.Delete(_script); }
            }
        }
    }
#endif
}
