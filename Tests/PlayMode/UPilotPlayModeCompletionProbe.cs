using System.Collections;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotPlayModeCompletionProbe
    {
        [UnityTest]
        public IEnumerator CompletesNormallyAndLetsRunnerExit()
        {
            Assert.That(Application.isPlaying, Is.True);
            yield return null;
            yield return null;
            Assert.That(Application.isPlaying, Is.True);
        }

        [UnityTest, Explicit("Targeted UP-011 acceptance only."), Timeout(450000)]
        public IEnumerator LongRunBeyondSixMinutes()
        {
            string project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            Assert.That(project.Replace('\\', '/'), Is.EqualTo("D:/upilot/Tests~/UPilotTest").IgnoreCase);
            string runGuid = File.ReadAllText(Path.Combine(project, "Library", "UPilot", "TestRuns", "active-run.txt")).Trim();
            Assert.That(Guid.TryParse(runGuid, out _), Is.True);
            string directory = Path.Combine(project, "Log", "P0P1", "LongReload", runGuid);
            Directory.CreateDirectory(directory);
            // CreateNew fails if the same run is replayed instead of recovered.
            using (var start = new FileStream(Path.Combine(directory, "invoked-once.txt"), FileMode.CreateNew))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(runGuid);
                start.Write(bytes, 0, bytes.Length);
                start.Flush(true);
            }
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < 370)
            {
                Assert.That(Application.isPlaying, Is.True);
                yield return null;
            }
            File.WriteAllText(Path.Combine(directory, "completed.json"),
                "{\"runGuid\":\"" + runGuid + "\",\"invocationCount\":1,\"elapsedMs\":" + clock.ElapsedMilliseconds + "}");
        }

        [UnityTest, Explicit("Expected failure evidence, not a passing regression suite.")]
        public IEnumerator ExpectedFailureAfterReload()
        {
            Assert.That(Application.isPlaying, Is.True);
            yield return null;
            Assert.Fail("P0_EXPECTED_PLAYMODE_FAILURE_DETAIL");
        }
    }
}
