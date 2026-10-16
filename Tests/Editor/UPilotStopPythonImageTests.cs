// SPDX-License-Identifier: MIT
using System.IO;
using NUnit.Framework;
using StopManager = CodingRiver.UPilot.UPilotMcpServerManager;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotStopPythonImageTests
    {
        private static readonly string Root = Path.Combine(Path.GetTempPath(), "UPilot Stop Python Image Tests");
        private static readonly string Script = Path.Combine(Root, "server", "run_upilot_mcp.py");
        private static StopManager.ServerStopScope Scope() => new(Root, Script, "", 18011, 18765);
        private static string Quote(string value) => "\"" + value + "\"";
        private static string Command(string image, string script = null) =>
            Quote(image) + " " + Quote(script ?? Script) + " --http-port 18011 --port 18765";

        [TestCase("python.exe")]
        [TestCase("python3.exe")]
        [TestCase("python311.exe")]
        [TestCase("python3.11.exe")]
        [TestCase("python")]
        [TestCase("python3")]
        [TestCase("python3.11")]
        [TestCase("pythonw.exe")]
        [TestCase("pythonw3.11.exe")]
        [TestCase("PYTHON.EXE")]
        [TestCase("py.exe")]
        public void PythonImagesStillRequireTheExactScriptAndUniquePortPair(string name)
        {
            var image = Path.Combine(Root, "runtime", name);
            Assert.That(Scope().Matches(Command(image), image), Is.True);
            Assert.That(Scope().Matches(Command(image, Script + ".other"), image), Is.False);
            Assert.That(Scope().Matches(Command(image) + " --port 18765", image), Is.False);
            Assert.That(Scope().Matches(Quote(image) + " --http-port 18011 --port 18765", image), Is.False);
        }

        [TestCase("arbitrary.exe")]
        [TestCase("powershell.exe")]
        [TestCase("pwsh.exe")]
        [TestCase("cmd.exe")]
        [TestCase("node.exe")]
        [TestCase("python-helper.exe")]
        [TestCase("python3-helper.exe")]
        [TestCase("python.exe.bak")]
        [TestCase("python3..11.exe")]
        [TestCase("python.3.exe")]
        [TestCase("py-helper.exe")]
        [TestCase("pyw.exe")]
        public void ArbitraryImageCannotAuthorizeStopByTakingTheServerScriptAsAnOrdinaryArgument(string name)
        {
            var image = Path.Combine(Root, "other", name);
            Assert.That(Scope().Matches(Command(image), image), Is.False);
            Assert.That(Scope().IsCommandCandidate(Command(image)), Is.False);
        }

        [TestCase("python.exe")]
        [TestCase("py.exe")]
        public void PythonImageMustStillAgreeWithTheAbsoluteArgvZeroPath(string name)
        {
            var image = Path.Combine(Root, "runtime", name);
            var otherImage = Path.Combine(Root, "other", name);
            Assert.That(Scope().Matches(Command(image), otherImage), Is.False);
            Assert.That(Scope().Matches(Command(otherImage), image), Is.False);
            Assert.That(Scope().Matches(Command(image), Path.Combine(Root, "arbitrary.exe")), Is.False);
        }
    }
}
